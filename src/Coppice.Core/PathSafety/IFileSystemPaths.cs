using System.Text;
using Coppice.Ports;

namespace Coppice.Core.PathSafety;

/// <summary>
/// The outcome of resolving a path through links.
/// <para>
/// A distinct <see cref="Loop"/> case rather than a sentinel string, because "resolution did not
/// terminate" and "resolution produced this path" lead to opposite decisions. A cycle has no terminal
/// directory, so a caller doing a safety check must refuse it; returning the last hop instead would let a
/// junction loop pass containment and reach a recursive delete.
/// </para>
/// </summary>
/// <param name="Path">The resolved path, or the input path when resolution looped.</param>
/// <param name="IsLoop">True when link resolution did not terminate.</param>
public sealed record ResolvedPath(string Path, bool IsLoop)
{
    public static ResolvedPath At(string path) => new(path, false);

    /// <summary>A cycle. <see cref="Path"/> is empty; callers that need the input path kept it themselves.</summary>
    public static ResolvedPath Loop { get; } = new(string.Empty, true);
}

/// <summary>
/// Wrapper over an <see cref="IFileSystem"/> that provides path-safety primitives for one OS.
/// <para>
/// Pure: every input is an explicit string and an <see cref="OperatingSystemKind"/>. No OS detection, no
/// ambient state, so every refusal is a function of its arguments and reproducible in a test.
/// </para>
/// </summary>
public sealed class IFileSystemPaths(IFileSystem fs, OperatingSystemKind os)
{
    private readonly IFileSystem _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    private readonly OperatingSystemKind _os = os;

    /// <summary>The operating system this instance is configured for.</summary>
    public OperatingSystemKind OS => _os;

    /// <summary>
    /// Canonicalizes <paramref name="path"/> using <see cref="PathCanonicalizer"/>.
    /// </summary>
    public CanonicalResult Canonicalize(string path) =>
        PathCanonicalizer.Canonicalize(path, _os);

    /// <summary>
    /// Checks whether <paramref name="candidate"/> is strictly within <paramref name="root"/>.
    /// The candidate must be a proper descendant — equal to the root is NOT contained.
    /// Both paths should be canonical before calling this method.
    /// </summary>
    public bool IsWithin(string root, string candidate)
    {
        var separator = PathCanonicalizer.SeparatorFor(_os);

        // Ensure trailing separator on root for correct prefix matching.
        var rootedRoot = root.EndsWith(separator) || root.EndsWith('/')
            ? root
            : root + separator;

        var cand = PathCanonicalizer.IsRooted(candidate, _os)
            ? candidate
            : PathCanonicalizer.Combine(root, candidate, _os);

        // Root itself is NOT within root.
        if (PathCanonicalizer.PathsEqual(cand, root, _os))
        {
            return false;
        }

        return cand.StartsWith(rootedRoot, PathCanonicalizer.ComparisonFor(_os));
    }

    /// <summary>
    /// Resolves a path through symlinks and junctions.
    /// <para>
    /// Returns <see cref="ResolvedPath.Loop"/> when resolution does not terminate. It does NOT return the
    /// last path it reached: a cycle's final hop is somewhere INSIDE the root by construction, so handing
    /// it back would let a junction loop pass a containment check and reach a recursive delete — the one
    /// operation that follows a cycle forever. "Unresolvable" and "resolved to this safe-looking path" are
    /// different answers, and only the second one is dangerous.
    /// </para>
    /// </summary>
    public ResolvedPath Resolve(string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { path };
        string current = path;

        // EVERY prefix of the path is examined, not just the path itself. A directory that IS a junction is
        // the easy case; the one that matters is a path *under* a self-referencing junction, where the leaf
        // is an ordinary directory and every check that looks only at the leaf reports it clean. That is
        // how `C:\cache\pkg\1.0.0` came back "safe" while `C:\cache\pkg` pointed at itself.
        foreach (string ancestor in Ancestors(current))
        {
            if (IsLink(ancestor))
            {
                ResolvedPath ancestorResolution = ResolveOne(ancestor, seen);

                if (ancestorResolution.IsLoop)
                {
                    return ancestorResolution;
                }

                // Re-root the remaining segments under where the ancestor actually points, then continue
                // the walk. Without this the rest of the path would be joined onto the DECLARED location
                // rather than the real one, which is precisely the escape being defended against.
                string remainder = path[ancestor.Length..];
                current = ancestorResolution.Path + remainder;
            }
        }

        return ResolveOne(current, seen);
    }

    /// <summary>Every ancestor of <paramref name="path"/>, longest first. Includes the path itself.</summary>
    private IEnumerable<string> Ancestors(string path)
    {
        string current = path;

        while (!string.IsNullOrEmpty(current))
        {
            yield return current;

            int last = current.LastIndexOfAny(['\\', '/']);

            if (last <= 0)
            {
                // Reached a drive root or the filesystem root; there is nothing above it to check.
                yield break;
            }

            current = current[..last];
        }
    }

    private bool IsLink(string path)
    {
        FileEntry? entry = _fs.GetEntry(path);
        return entry is not null && entry.IsLink;
    }

    private ResolvedPath ResolveOne(string path, HashSet<string> seen)
    {
        string current = path;

        for (var hop = 0; hop < 40; hop++)
        {
            FileEntry? entry = _fs.GetEntry(current);

            if (entry is null || !entry.IsLink)
            {
                return ResolvedPath.At(current);
            }

            string? rawTarget = entry.LinkTarget;

            if (rawTarget is null)
            {
                return ResolvedPath.At(current);
            }

            // Resolve relative targets against the link's parent directory.
            string resolvedTarget = PathCanonicalizer.IsRooted(rawTarget, _os)
                ? PathCanonicalizer.Canonicalize(rawTarget, _os).Path
                : PathCanonicalizer.Combine(CurrentDirectory(current), rawTarget, _os);

            // A cycle: back where we started, or onto a path already walked. Either way there is no
            // directory here to reason about.
            if (PathCanonicalizer.PathsEqual(resolvedTarget, current, _os) || !seen.Add(resolvedTarget))
            {
                return ResolvedPath.Loop;
            }

            current = resolvedTarget;
        }

        return ResolvedPath.Loop;
    }

    /// <summary>
    /// Resolves a path, treating a cycle as "resolves to itself".
    /// <para>
    /// For callers that only need the path — de-duplication, display. NOT for safety decisions: a caller
    /// that must refuse a cycle wants <see cref="Resolve"/>.
    /// </para>
    /// </summary>
    public string ResolveRealPath(string path) => Resolve(path).Path;

    /// <summary>
    /// Checks whether resolving links from <paramref name="path"/> escapes <paramref name="root"/>.
    /// Returns true if the resolved path is outside the root.
    /// </summary>
    public bool EscapesRoot(string root, string path)
    {
        var canonicalPath = PathCanonicalizer.Canonicalize(path, _os);
        if (!canonicalPath.Ok)
        {
            return true; // Invalid input is treated as escaping.
        }

        var resolved = ResolveRealPath(canonicalPath.Path);
        return !IsWithin(root, resolved);
    }

    /// <summary>
    /// Determines whether a path contains relative segments (<c>.</c> or <c>..</c>) that were not
    /// fully resolved by canonicalization. Returns true if the canonical form still contains such segments.
    /// </summary>
    public bool HasRelativeSegments(string path)
    {
        var result = Canonicalize(path);
        if (!result.Ok)
        {
            return true;
        }

        var segments = result.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s == "." || s == "..");
    }

    private static string CurrentDirectory(string path)
    {
        var idx = path.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? path[..idx] : string.Empty;
    }

}

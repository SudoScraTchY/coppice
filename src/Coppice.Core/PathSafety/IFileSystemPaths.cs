using System.Text;
using Coppice.Ports;

namespace Coppice.Core.PathSafety;

/// <summary>
/// Thin wrapper over <see cref="IFileSystem"/> that exposes safe path operations used by the gateway:
/// canonicalization, containment checking, real-path resolution (with loop detection), and link-escape detection.
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
    /// Resolves symlink/junction chains starting at <paramref name="path"/>.
    /// Stops on a loop (max 40 hops, matching FakeFileSystem) and returns the terminal path.
    /// If the final target does not exist, the path is returned as-is (does not require existence).
    /// </summary>
    public string ResolveRealPath(string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { path };
        var current = path;
        for (var hop = 0; hop < 40; hop++)
        {
            var entry = _fs.GetEntry(current);
            if (entry is null || !entry.IsLink)
            {
                return current;
            }

            var rawTarget = entry.LinkTarget;
            if (rawTarget is null)
            {
                return current;
            }

            // Resolve relative targets against the link's parent directory.
            var resolvedTarget = PathCanonicalizer.IsRooted(rawTarget, _os)
                ? PathCanonicalizer.Canonicalize(rawTarget, _os).Path
                : PathCanonicalizer.Combine(CurrentDirectory(current), rawTarget, _os);

            // Detect loop: if we land back where we started, or on a path already seen, stop.
            if (PathCanonicalizer.PathsEqual(resolvedTarget, current, _os) || !seen.Add(resolvedTarget))
            {
                return current;
            }

            current = resolvedTarget;
        }

        // Loop detected — return the last resolved path (the gateway will reject it).
        return current;
    }

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

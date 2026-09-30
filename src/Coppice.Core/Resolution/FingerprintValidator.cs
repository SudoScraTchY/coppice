using Coppice.Core.PathSafety;
using Coppice.Ports;

namespace Coppice.Core.Resolution;

/// <summary>
/// A layout rule a cache root must satisfy (T-006, FR-03). Roots are untrusted: a resolved path is
/// only cleanable if it actually LOOKS like the thing it claims to be.
/// </summary>
public sealed record Fingerprint
{
    public required string LocationId { get; init; }

    /// <summary>
    /// Entry sub-directories that must all be present (e.g. <c>host/fxr</c> and <c>sdk</c> for a
    /// .NET root). Absence of any one fails the fingerprint outright — a required marker is a
    /// stronger signal than a ratio.
    /// </summary>
    public IReadOnlyList<string> RequiredPaths { get; init; } = [];

    /// <summary>
    /// Filename glob patterns that identify a versioned entry. The default is the ecosystem-wide
    /// <c>{name}/{version}</c> shape, which is what most tool caches look like.
    /// </summary>
    public IReadOnlyList<string> EntryPatterns { get; init; } = ["*/version"];

    /// <summary>Minimum fraction of entries that must match <see cref="EntryPatterns"/> (05, default 0.8).</summary>
    public double MinimumMatchRatio { get; init; } = 0.8;

    /// <summary>
    /// Directories that are never counted as entries. A root full of these is not a cache.
    /// </summary>
    public IReadOnlyList<string> IgnoredNames { get; init; } = [".git", ".coppice", "node_modules", "tmp", "temp"];
}

/// <summary>Why a fingerprint failed, so the report can explain it rather than just saying "no".</summary>
public enum FingerprintFailure
{
    None = 0,
    NotFound,
    NotADirectory,
    Empty,
    MissingRequiredPath,
    RatioTooLow,
}

/// <summary>The verdict, with enough detail for a `doctor` line and for the plan's reason string.</summary>
public sealed record FingerprintResult(
    bool Passed,
    FingerprintFailure Failure,
    string Message,
    int EntryCount,
    int MatchCount,
    double Ratio)
{
    public static FingerprintResult Ok(int entries, int matches, double ratio) =>
        new(true, FingerprintFailure.None, $"Layout matches ({matches}/{entries} entries).", entries, matches, ratio);

    public static FingerprintResult Fail(FingerprintFailure failure, string message, int entries = 0, int matches = 0) =>
        new(false, failure, message, entries, matches, entries == 0 ? 0d : (double)matches / entries);
}

/// <summary>
/// Validates that a resolved path actually looks like the location it claims to be (FR-03).
/// <para>
/// This is the gate that makes a poisoned tool survivable. A <c>go</c> binary on PATH that answers
/// <c>/</c> or the home directory produces a path that exists and passes the denylist on Windows —
/// but it cannot produce a directory whose entries look like <c>{name}/{version}</c>. A failed
/// fingerprint is reported and never cleaned (E-11, LR-3).
/// </para>
/// <para>
/// Pure and port-driven: it reads through <see cref="IFileSystem"/> and writes nothing.
/// </para>
/// </summary>
public sealed class FingerprintValidator
{
    private readonly IFileSystem _fs;
    private readonly OperatingSystemKind _os;

    public FingerprintValidator(IFileSystem fs, OperatingSystemKind os)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
        _os = os;
    }

    /// <summary>
    /// A delegate matching <see cref="FingerprintValidator.Validate"/>, so the resolution engine can
    /// take a gate without knowing what a fingerprint is.
    /// </summary>
    public Func<string, bool> AsPredicate(Fingerprint fingerprint) => path => Validate(fingerprint, path).Passed;

    public FingerprintResult Validate(Fingerprint fingerprint, string path)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_fs.DirectoryExists(path))
        {
            return FingerprintResult.Fail(FingerprintFailure.NotFound, $"'{path}' does not exist.");
        }

        // Gate 1 (05): required markers. Checked before the ratio because a missing marker is a
        // definitive answer, while a ratio needs enough entries to be meaningful.
        foreach (string required in fingerprint.RequiredPaths)
        {
            if (!_fs.DirectoryExists(Combine(path, required)))
            {
                return FingerprintResult.Fail(
                    FingerprintFailure.MissingRequiredPath,
                    $"'{path}' is missing the required marker '{required}', so it is not a {fingerprint.LocationId} root.");
            }
        }

        // Depth 1 only: the fingerprint judges the root's immediate layout. A recursive walk would
        // count grandchildren, so a root with three packages would report fifteen entries and
        // quietly pass on contents it never inspected.
        var entries = new List<FileEntry>();
        foreach (FileEntry entry in _fs.EnumerateEntries(path, new EnumerationRequest
        {
            FollowLinks = false,
            IncludeLinks = false,
            MaxDepth = 1,
        }))
        {
            if (entry.Kind == EntryKind.Directory && !IsIgnored(entry.Name, fingerprint))
            {
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
        {
            return FingerprintResult.Fail(
                FingerprintFailure.Empty,
                $"'{path}' contains no entries, so it cannot be a {fingerprint.LocationId} root.");
        }

        int matches = entries.Count(entry => MatchesAnyPattern(entry, fingerprint));
        double ratio = (double)matches / entries.Count;

        if (ratio < fingerprint.MinimumMatchRatio)
        {
            return new FingerprintResult(
                false,
                FingerprintFailure.RatioTooLow,
                $"only {matches} of {entries.Count} entries in '{path}' look like {fingerprint.LocationId} content "
                    + $"({ratio:P0} < {fingerprint.MinimumMatchRatio:P0} required), so it will be reported but never cleaned",
                entries.Count,
                matches,
                ratio);
        }

        return FingerprintResult.Ok(entries.Count, matches, ratio);
    }

    private static bool IsIgnored(string name, Fingerprint fingerprint) =>
        fingerprint.IgnoredNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the entry looks like a versioned cache entry. A <c>{name}/{version}</c> shape means
    /// a directory containing at least one child directory whose name carries a version — a
    /// heuristic, deliberately: the point is to distinguish a cache layout from a home directory,
    /// not to be exact about it.
    /// </summary>
    private bool MatchesAnyPattern(FileEntry entry, Fingerprint fingerprint)
    {
        foreach (string pattern in fingerprint.EntryPatterns)
        {
            bool matched = pattern switch
            {
                "*/version" => HasVersionLikeChild(entry),
                "*" => true,
                _ => entry.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            };

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A version-like child directory: the entry holds at least one subdirectory and every sampled
    /// subdirectory name looks like a version (starts with a digit). A real cache folder's versions
    /// all do; a home directory's children (Documents, Music) do not.
    /// </summary>
    private bool HasVersionLikeChild(FileEntry entry)
    {
        IReadOnlyList<FileEntry> children = _fs.EnumerateEntries(entry.Path, new EnumerationRequest
        {
            FollowLinks = false,
            IncludeLinks = false,
            MaxDepth = 1,
        });
        List<FileEntry> dirs = [.. children.Where(c => c.Kind == EntryKind.Directory)];

        if (dirs.Count == 0)
        {
            return false;
        }

        return dirs.All(d => char.IsAsciiDigit(d.Name[0]));
    }

    private string Combine(string root, string relative) =>
        PathCanonicalizer.Combine(root, relative, _os);
}

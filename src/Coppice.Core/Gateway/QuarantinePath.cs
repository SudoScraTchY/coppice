using System.Text;

namespace Coppice.Core.Gateway;

/// <summary>
/// Where a quarantined item lives, and how it gets back (T-031's quarantine step; T-032 does the listing
/// and restore).
/// <para>
/// The layout is <c>&lt;root-parent&gt;/.coppice/quarantine/&lt;location-id&gt;/&lt;relative-path-under-root&gt;</c>,
/// per 06. <b>Root-parent</b>, not the root itself: the destination must not be inside the directory being
/// cleaned, or a recursive delete of the quarantine would take the quarantine with it. And
/// <b>same volume</b>, because a rename within a volume is cheap and atomic-ish, while a cross-volume
/// "trash" would force a copy+delete of exactly the files being removed — which is slower, uses the space
/// we were trying to free, and is not undoable.
/// </para>
/// </summary>
public static class QuarantinePath
{
    /// <summary>The directory name created under the root's parent.</summary>
    public const string DirectoryName = ".coppice";

    /// <summary>
    /// The quarantine destination for one item.
    /// <para>
    /// Derived from the root and the item's path, never supplied by the caller. A caller-chosen destination
    /// is a caller-chosen way out of the containment check that just approved the source.
    /// </para>
    /// </summary>
    public static string For(string root, string locationId, string itemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemPath);

        char sep = Sep(root);

        string trimmedRoot = root.TrimEnd('/', '\\');
        string parent = ParentOf(trimmedRoot, sep);

        // The item's own name, then its immediate parent, so two packages with the same leaf name (the
        // normal case: many "1.0.0" directories) do not collide in quarantine.
        string leaf = LeafOf(itemPath, sep);
        string container = LeafOf(ParentOf(itemPath, sep), sep);

        var builder = new StringBuilder();
        builder.Append(parent).Append(sep).Append(DirectoryName).Append(sep).Append("quarantine")
            .Append(sep).Append(SanitizeSegment(locationId))
            .Append(sep).Append(SanitizeSegment(container))
            .Append(sep).Append(SanitizeSegment(leaf));

        return builder.ToString();
    }

    /// <summary>
    /// Makes one path segment safe to use as a directory name.
    /// <para>
    /// A quarantine destination must not be able to escape its own directory, so a segment carrying a
    /// separator or a parent reference is replaced rather than honoured. The item path was already through
    /// containment, so this is belt-and-braces — but a destination built by string concatenation is exactly
    /// the shape where one missing check becomes an arbitrary write.
    /// </para>
    /// </summary>
    internal static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return "_";
        }

        // ':' is illegal in a Windows path and is the ADS separator; '_' keeps the result legal everywhere.
        var cleaned = new StringBuilder(segment.Length);

        foreach (char c in segment)
        {
            cleaned.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        string result = cleaned.ToString().TrimEnd('.', ' ');

        // "." and ".." would be parent references even after the character sweep.
        return result is "." or ".." or "" ? "_" : result;
    }

    private static char Sep(string path) => path.Contains('\\') ? '\\' : '/';

    private static string ParentOf(string path, char sep)
    {
        int last = path.LastIndexOf(sep);

        return last <= 0 ? path : path[..last];
    }

    private static string LeafOf(string path, char sep)
    {
        string trimmed = path.TrimEnd('/', '\\');
        int last = trimmed.LastIndexOf(sep);

        return last < 0 ? trimmed : trimmed[(last + 1)..];
    }
}

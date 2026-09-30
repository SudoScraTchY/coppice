using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Scanning;

/// <summary>
/// Enumerates the immediate children of a validated root and yields one <see cref="Item"/> per
/// entry (FR-04). This is the kernel's own default enumerator; plugins supply their own for
/// richer shapes, and both must produce items in the same deterministic order.
/// </summary>
public interface IEntryEnumerator
{
    /// <summary>Stable id for the enumerator, used in issues and diagnostics.</summary>
    string Id { get; }

    /// <summary>
    /// Yields items for one root. Implementations must not follow links out of the root, must
    /// respect <paramref name="cancellationToken"/>, and must emit in a deterministic order.
    /// </summary>
    IAsyncEnumerable<Item> EnumerateAsync(ScanContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default layout-agnostic enumerator: every immediate subdirectory of a cache root is one
/// item named after the directory, with its measured size. This is correct for the majority of
/// caches (<c>~/.nuget/packages</c>, <c>~/.cargo/registry</c>) where the child directory IS the
/// unit a user thinks about.
/// </summary>
public sealed class DirectoryEntryEnumerator : IEntryEnumerator
{
    private readonly IFileSystem _fs;

    public DirectoryEntryEnumerator(IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    public string Id => "directory-entry";

    public async IAsyncEnumerable<Item> EnumerateAsync(
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<FileEntry> entries = _fs.EnumerateEntries(context.RootPath, new EnumerationRequest
        {
            // Never follow links out of the root: a link farm must not pull foreign bytes into a
            // root's size (E-1), and a junction loop must not hang the scan (E-2).
            FollowLinks = false,
            IncludeLinks = false,
            MaxDepth = 1,
        });

        // Sorted here rather than by the VFS, so the output is identical regardless of the order
        // any particular adapter happens to return directories in (NFR-06).
        foreach (FileEntry entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Kind != EntryKind.Directory)
            {
                continue;
            }

            ulong size = _fs.MeasureSize(entry.Path);

            yield return new Item(
                Ecosystem: string.Empty,
                Kind: "entry",
                Name: entry.Name,
                Version: DetectVersion(entry.Name, out _),
                LocationId: context.LocationId,
                Path: entry.Path,
                Size: size,
                Risk: Risk.Review,
                Facts: Facts.From([new("enumerator", Id)]));
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The trailing version-looking run, e.g. <c>13.0.3</c> in <c>newtonsoft.json.13.0.3</c>.
    /// <para>
    /// Walks backwards from the end over digits and dots to find the run's end, then continues back
    /// to its start, then requires that run to be SEPARATED from the name by a <c>.</c> or <c>-</c>.
    /// The separator check is what distinguishes <c>pkg-2</c> and <c>pkg.2</c> (versioned) from
    /// <c>v2</c> (a name that happens to end in a digit).
    /// </para>
    /// <para>
    /// Returns an empty string rather than guessing when no version is present. This feeds the plan's
    /// reason string, where an honest blank beats a plausible wrong value.
    /// </para>
    /// </summary>
    public static string DetectVersion(string name, out bool parsed)
    {
        parsed = false;
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // 1. Take the maximal trailing run of digits and dots.
        int runStart = name.Length;
        while (runStart > 0 && IsVersionChar(name[runStart - 1]))
        {
            runStart--;
        }

        // 2. Trim dots from both ends of the run; a dot that touches the name's letters is a
        //    separator, not part of the version ("newtonsoft.json.13.0.3" -> "13.0.3").
        string run = name[runStart..].Trim('.');
        if (run.Length == 0 || !char.IsAsciiDigit(run[0]))
        {
            return string.Empty;
        }

        // 3. Locate the run's true start after trimming.
        int start = runStart;
        while (start < name.Length && name[start] == '.')
        {
            start++;
        }

        // 4. The run must be SEPARATED from the name. "pkg.2" and "pkg-2" are versioned; "pkg2"
        //    and "v2" are names that merely end in a digit.
        if (start > 0 && name[start - 1] is not ('.' or '-'))
        {
            return string.Empty;
        }

        parsed = true;
        return run;
    }

    private static bool IsVersionChar(char c) => char.IsAsciiDigit(c) || c == '.';

}

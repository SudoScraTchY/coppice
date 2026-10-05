using System.Globalization;
using System.Text;

using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>
/// The numbers every output format has to agree on (T-027, FR-18).
/// <para>
/// Totals are computed ONCE, here, and passed to each renderer. A renderer that summed items itself
/// would be free to disagree with the console report and the JSON — and a user comparing two exports of
/// the same scan would have no way to tell which was right. One source, three renderings.
/// </para>
/// </summary>
public sealed record ReportTotals
{
    public required int ItemCount { get; init; }

    /// <summary>
    /// Bytes across every item. Computed unchecked-but-saturating: an overflow here would report a
    /// smaller total than the parts, which is worse than admitting the number is unrepresentable.
    /// </summary>
    public required ulong TotalBytes { get; init; }

    public required int ReferencedCount { get; init; }

    public required int UnreferencedCount { get; init; }

    public required int UnknownUsageCount { get; init; }

    public required int ProblemCount { get; init; }

    public required int IssueCount { get; init; }

    /// <summary>Totals per ecosystem, keyed by ecosystem name.</summary>
    public required IReadOnlyDictionary<string, EcosystemTotal> ByEcosystem { get; init; }

    public static ReportTotals From(IReadOnlyList<Item> items, IReadOnlyList<Problem> problems, IReadOnlyList<ScanIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(issues);

        var byEcosystem = new Dictionary<string, EcosystemTotal>(StringComparer.Ordinal);

        ulong totalBytes = 0;

        foreach (Item item in items)
        {
            // Saturating rather than wrapping: a total below its parts would be an outright lie about how
            // much disk the user would get back.
            totalBytes = totalBytes > ulong.MaxValue - item.Size
                ? ulong.MaxValue
                : totalBytes + item.Size;

            EcosystemTotal bucket = byEcosystem.TryGetValue(item.Ecosystem, out EcosystemTotal? existing)
                ? existing
                : new EcosystemTotal(item.Ecosystem, 0, 0);

            byEcosystem[item.Ecosystem] = bucket with
            {
                ItemCount = bucket.ItemCount + 1,
                Bytes = bucket.Bytes > ulong.MaxValue - item.Size ? ulong.MaxValue : bucket.Bytes + item.Size,
            };
        }

        // Usage is counted by UsageBreakdown, not here. Two counters that both read the same facts are
        // two things that can disagree, and the export formats must agree with the console exactly.
        UsageBreakdown usage = UsageBreakdown.FromItems(items);

        return new ReportTotals
        {
            ItemCount = items.Count,
            TotalBytes = totalBytes,
            ReferencedCount = usage.Referenced,
            UnreferencedCount = usage.Unreferenced,
            UnknownUsageCount = usage.Unknown,
            ProblemCount = problems.Count,
            IssueCount = issues.Count,
            ByEcosystem = byEcosystem,
        };
    }

    /// <summary>
    /// Usage as recorded by the scan, read from the item's facts.
    /// <para>
    /// Read the same way the console renderer reads it, so a missing fact means "Unknown" in every format
    /// rather than "Unknown" in the table and a blank in the CSV.
    /// </para>
    /// </summary>
    public static Usage UsageOf(Item item) =>
        item.Facts.TryGetValue("usage", out string? raw)
            ? Enum.TryParse(raw, ignoreCase: true, out Usage parsed) ? parsed : Usage.Unknown
            : Usage.Unknown;

    /// <summary>Invariant every format must satisfy: the buckets account for every item.</summary>
    public bool IsConsistent =>
        ReferencedCount + UnreferencedCount + UnknownUsageCount == ItemCount
        && ByEcosystem.Values.Aggregate(0, (acc, e) => acc + e.ItemCount) == ItemCount;

    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];

        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal below KB, none for bytes: "1.5 GB" reads as a figure a human sized a risk against,
        // while "1.5 KB" would imply a precision a cache directory does not have.
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.ToString("F1", CultureInfo.InvariantCulture)} {units[unit]}");
    }
}

/// <summary>Per-ecosystem counts and bytes.</summary>
public sealed record EcosystemTotal(string Ecosystem, int ItemCount, ulong Bytes);

/// <summary>
/// CSV export (T-027, FR-18, 10-formats: "CSV (flat item table + totals header)").
/// <para>
/// A real CSV, not a table that looks like one. Quoting, embedded newlines and embedded quotes are the
/// whole reason the format exists for machine consumption: a package named
/// <c>evil", version "1.0</c> must not be able to forge a column. Item names come from manifests and
/// directories, so they are attacker-influenced in exactly the way CSV quoting exists for.
/// </para>
/// </summary>
public static class CsvReport
{
    /// <summary>Column order, fixed. A consumer reads by position, so this must not vary between runs.</summary>
    public static readonly string[] Columns =
    [
        "ecosystem", "kind", "name", "version", "location_id", "path", "size_bytes", "usage", "risk",
    ];

    public static string Render(string snapshotId, IReadOnlyList<Item> items, ReportTotals totals)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        var csv = new StringBuilder();

        // Totals first, as a header block, so a reader that only opens the file still learns the scale.
        // Comment lines are the conventional way to carry metadata without a consumer reading it as data;
        // a totals ROW would be indistinguishable from a real record to a naive parser.
        csv.Append("# coppice scan totals\n");
        csv.Append(CultureInfo.InvariantCulture, $"# snapshot_id,{Quote(snapshotId)}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# item_count,{totals.ItemCount}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# total_bytes,{totals.TotalBytes}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# referenced,{totals.ReferencedCount}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# unreferenced,{totals.UnreferencedCount}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# unknown_usage,{totals.UnknownUsageCount}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# problems,{totals.ProblemCount}\n");
        csv.Append(CultureInfo.InvariantCulture, $"# issues,{totals.IssueCount}\n");

        foreach ((string ecosystem, EcosystemTotal eco) in totals.ByEcosystem.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            csv.Append(CultureInfo.InvariantCulture, $"# ecosystem:{eco.Ecosystem},{eco.ItemCount},{eco.Bytes}\n");
        }

        csv.Append('\n');

        csv.AppendJoin(',', Columns);
        csv.Append('\n');

        foreach (Item item in items)
        {
            csv.AppendJoin(
                ',',
                Quote(item.Ecosystem),
                Quote(item.Kind),
                Quote(item.Name),
                Quote(item.Version),
                Quote(item.LocationId),
                Quote(item.Path),
                item.Size.ToString(CultureInfo.InvariantCulture),
                Quote(ReportTotals.UsageOf(item).ToString()),
                Quote(item.Risk.ToString()));

            csv.Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>
    /// Renders a field as a single RFC 4180 field.
    /// <para>
    /// Quotes when the value contains a comma, a quote, a newline or leading/trailing whitespace, and
    /// doubles any embedded quote. CR and LF are both handled because a Windows path or a manifest-supplied
    /// name can carry either, and a lone CR would otherwise re-split the row.
    /// </para>
    /// </summary>
    public static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsQuotes =
            value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value != value.Trim();

        return needsQuotes
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

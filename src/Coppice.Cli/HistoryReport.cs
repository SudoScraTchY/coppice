using System.Globalization;
using System.Text;
using System.Text.Json;
using Coppice.Application;

namespace Coppice.Cli;

/// <summary>
/// Renders a snapshot history for the <c>history</c> verb (T-041).
/// <para>
/// The text form is the human summary: one block per scan with its totals and the change from the
/// previous scan, then the same change per ecosystem. The JSON form is a versioned document with
/// ordinal-sorted lists and camelCase names, so a consumer can depend on the shape and two runs over
/// one store produce the same bytes.
/// </para>
/// </summary>
public static class HistoryReport
{
    /// <summary>How many scans a default report covers. Big enough for a trend, small enough to read.</summary>
    public const int DefaultLimit = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToText(SnapshotHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.IsEmpty)
        {
            return history.SkippedSnapshotIds.Count == 0
                ? "No snapshots yet. Run 'coppice scan' to create the first one."
                : "No readable snapshots in the stored history: " + string.Join(", ", history.SkippedSnapshotIds);
        }

        var text = new StringBuilder();
        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"coppice history — {history.Points.Count} snapshot(s), oldest first"));

        foreach (string skipped in history.SkippedSnapshotIds)
        {
            text.AppendLine($"skipped: {skipped} (could not be loaded)");
        }

        foreach (SnapshotPoint point in history.Points)
        {
            SnapshotDiff diff = point.Diff;
            string timestamp = FormatTimestamp(point.CreatedAtUtc);

            text.AppendLine(string.Join(
                "  ",
                timestamp,
                point.SnapshotId,
                ItemLabel(diff.ItemCount),
                FormatBytes(diff.TotalBytes),
                diff.HasPrevious ? FormatDelta(diff.ItemDelta, diff.ByteDelta) : "baseline"));

            foreach (EcosystemDelta ecosystem in diff.Ecosystems)
            {
                text.AppendLine("    " + string.Join(
                    "  ",
                    ecosystem.Ecosystem,
                    ItemLabel(ecosystem.ItemCount),
                    FormatBytes(ecosystem.TotalBytes),
                    diff.HasPrevious ? FormatDelta(ecosystem.ItemDelta, ecosystem.ByteDelta) : "baseline"));
            }
        }

        SnapshotPoint? earliest = history.Earliest;
        SnapshotPoint? latest = history.Latest;

        if (history.Points.Count > 1 && earliest is not null && latest is not null)
        {
            text.AppendLine();
            text.AppendLine(
                $"Since {earliest.SnapshotId}: "
                + FormatDelta(
                    latest.ItemCount - earliest.ItemCount,
                    (long)latest.TotalBytes - (long)earliest.TotalBytes));
        }

        return text.ToString();
    }

    public static string ToJson(SnapshotHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        HistoryDocument document = new(
            Schema: 1,
            Snapshots:
            [
                .. history.Points.Select(point => new HistorySnapshotDocument(
                    point.SnapshotId,
                    point.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    point.ItemCount,
                    point.TotalBytes,
                    point.Diff.PreviousSnapshotId,
                    point.Diff.HasPrevious ? point.Diff.ItemDelta : 0,
                    point.Diff.HasPrevious ? point.Diff.ByteDelta : 0,
                    point.Diff.NewItemIds,
                    point.Diff.RemovedItemIds,
                    [
                        .. point.Diff.Ecosystems.Select(ecosystem => new HistoryEcosystemDocument(
                            ecosystem.Ecosystem,
                            ecosystem.ItemCount,
                            ecosystem.TotalBytes,
                            point.Diff.HasPrevious ? ecosystem.ItemDelta : 0,
                            point.Diff.HasPrevious ? ecosystem.ByteDelta : 0)),
                    ])),
            ],
            SkippedSnapshotIds: history.SkippedSnapshotIds);

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>"+2 item(s), -1.5 MB" — a zero component is omitted rather than printed as noise.</summary>
    public static string FormatDelta(int itemDelta, long byteDelta)
    {
        if (itemDelta == 0 && byteDelta == 0)
        {
            return "no change";
        }

        var parts = new List<string>(2);
        if (itemDelta != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Signed(itemDelta)} item(s)"));
        }

        if (byteDelta != 0)
        {
            parts.Add(SignedBytes(byteDelta));
        }

        return string.Join(", ", parts);
    }

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

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.ToString("F1", CultureInfo.InvariantCulture)} {units[unit]}");
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string ItemLabel(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} item(s)");

    private static string Signed(int value) =>
        value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private static string SignedBytes(long delta) =>
        delta < 0 ? "-" + FormatBytes((ulong)-delta) : "+" + FormatBytes((ulong)delta);

    /// <summary>
    /// The versioned JSON document. <c>schema</c> is first so a consumer can branch before parsing the
    /// rest; the lists are already ordinal-sorted by the application types.
    /// </summary>
    public sealed record HistoryDocument(
        int Schema,
        IReadOnlyList<HistorySnapshotDocument> Snapshots,
        IReadOnlyList<string> SkippedSnapshotIds);

    public sealed record HistorySnapshotDocument(
        string SnapshotId,
        string CreatedAtUtc,
        int ItemCount,
        ulong TotalBytes,
        string? PreviousSnapshotId,
        int ItemDelta,
        long ByteDelta,
        IReadOnlyList<string> NewItemIds,
        IReadOnlyList<string> RemovedItemIds,
        IReadOnlyList<HistoryEcosystemDocument> Ecosystems);

    public sealed record HistoryEcosystemDocument(
        string Ecosystem,
        int ItemCount,
        ulong TotalBytes,
        int ItemDelta,
        long ByteDelta);
}

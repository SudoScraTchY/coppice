using System.Globalization;
using Coppice.Application;

namespace Coppice.Gui.ViewModels;

/// <summary>One ecosystem line on the history page: its totals and how they changed.</summary>
public sealed record HistoryEcosystemRow(
    string Ecosystem,
    string Totals,
    string Delta,
    bool IsGrowth,
    bool IsDecline)
{
    public bool IsFlat => !IsGrowth && !IsDecline;

    /// <summary>The one-line form the list row shows, e.g. "dotnet · +1 item(s), +500 B".</summary>
    public string Label => $"{Ecosystem} · {Delta}";

    public override string ToString() => $"{Ecosystem} — {Totals} — {Delta}";
}

/// <summary>
/// One scan as the history page renders it. Every value here is computed by <see cref="HistoryTrend"/>,
/// not by the view: the page binds text and two bar heights, nothing else.
/// </summary>
public sealed record HistoryTrendPoint(
    string SnapshotId,
    string CapturedAt,
    string ShortDate,
    int ItemCount,
    ulong TotalBytes,
    string ItemLabel,
    string TotalLabel,
    string? PreviousSnapshotId,
    int ItemDelta,
    long ByteDelta,
    string DeltaLabel,
    IReadOnlyList<HistoryEcosystemRow> Ecosystems)
{
    /// <summary>Height in device-independent pixels for the byte bar in the chart.</summary>
    public int ByteBarHeight { get; init; }

    /// <summary>Height in device-independent pixels for the item-count bar in the chart.</summary>
    public int ItemBarHeight { get; init; }

    public bool IsBaseline => PreviousSnapshotId is null;

    public bool IsGrowth => !IsDecline && (ItemDelta > 0 || ByteDelta > 0);

    public bool IsDecline => ItemDelta < 0 || ByteDelta < 0;

    public bool IsFlat => !IsGrowth && !IsDecline;

    public override string ToString() => $"{CapturedAt} — {ItemLabel} — {TotalLabel} — {DeltaLabel}";
}

/// <summary>
/// The trend behind the GUI's history page (T-041). It turns an application <see cref="SnapshotHistory"/>
/// into text, per-ecosystem deltas and bounded bar heights, so the view has no trend arithmetic and the
/// model can be tested headlessly.
/// <para>
/// The chart scales each metric against its own maximum across the loaded window. That keeps a
/// byte chart legible next to an item chart, and it means the oldest point has a real structure, not a
/// zero-height bar, once anything has been scanned. Heights are capped at <see cref="MaxBarHeight"/>,
/// so twelve snapshots cannot grow the page without bound.
/// </para>
/// </summary>
public sealed class HistoryTrend
{
    public const int MaxBarHeight = 120;
    private const int MinVisibleBarHeight = 2;

    private HistoryTrend(IReadOnlyList<HistoryTrendPoint> points)
    {
        Points = points;
        Recent = [.. points.Reverse()];
    }

    /// <summary>Oldest first — the order the chart reads in.</summary>
    public IReadOnlyList<HistoryTrendPoint> Points { get; }

    /// <summary>Newest first — the order the snapshot list reads in.</summary>
    public IReadOnlyList<HistoryTrendPoint> Recent { get; }

    public bool IsEmpty => Points.Count == 0;

    public int SnapshotCount => Points.Count;

    public int SkippedCount { get; private init; }

    public string Summary
    {
        get
        {
            if (IsEmpty)
            {
                return "No snapshots yet. Run a scan to start a history.";
            }

            if (Points.Count == 1)
            {
                return "1 snapshot · the baseline for future scans";
            }

            string summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{Points.Count} snapshot(s) · {Points[0].ShortDate} to {Points[^1].ShortDate}");

            return SkippedCount == 0
                ? summary
                : string.Create(CultureInfo.InvariantCulture, $"{summary} · {SkippedCount} unreadable");
        }
    }

    public string LatestSummary
    {
        get
        {
            if (IsEmpty)
            {
                return "Scan once to create a baseline; scan again to see growth.";
            }

            HistoryTrendPoint latest = Points[^1];
            if (!latest.IsBaseline)
            {
                return "Since the previous scan: " + latest.DeltaLabel;
            }

            return Points.Count == 1
                ? "This is the first snapshot, so it is the baseline. The next scan will show what grew."
                : "The oldest snapshot in this window has no earlier scan to compare with.";
        }
    }

    public string? LatestSnapshotId => IsEmpty ? null : Points[^1].SnapshotId;

    public static HistoryTrend From(SnapshotHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var points = new List<HistoryTrendPoint>(history.Points.Count);
        foreach (SnapshotPoint point in history.Points)
        {
            List<HistoryEcosystemRow> ecosystems =
            [
                .. point.Diff.Ecosystems.Select(ecosystem => new HistoryEcosystemRow(
                    ecosystem.Ecosystem,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{ecosystem.ItemCount} item(s) · {FormatBytes(ecosystem.TotalBytes)}"),
                    point.Diff.HasPrevious
                        ? FormatDelta(ecosystem.ItemDelta, ecosystem.ByteDelta)
                        : "baseline",
                    ecosystem.ItemDelta > 0 || ecosystem.ByteDelta > 0,
                    ecosystem.ItemDelta < 0 || ecosystem.ByteDelta < 0)),
            ];

            points.Add(new HistoryTrendPoint(
                point.SnapshotId,
                point.CreatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                point.CreatedAtUtc.ToUniversalTime().ToString("MMM d", CultureInfo.InvariantCulture),
                point.ItemCount,
                point.TotalBytes,
                string.Create(CultureInfo.InvariantCulture, $"{point.ItemCount} item(s)"),
                FormatBytes(point.TotalBytes),
                point.Diff.PreviousSnapshotId,
                point.Diff.ItemDelta,
                point.Diff.ByteDelta,
                point.Diff.HasPrevious ? FormatDelta(point.Diff.ItemDelta, point.Diff.ByteDelta) : "baseline",
                ecosystems));
        }

        ulong maxBytes = points.Count == 0 ? 0 : points.Max(point => point.TotalBytes);
        int maxItems = points.Count == 0 ? 0 : points.Max(point => point.ItemCount);

        for (int i = 0; i < points.Count; i++)
        {
            points[i] = points[i] with
            {
                ByteBarHeight = Scale(points[i].TotalBytes, maxBytes),
                ItemBarHeight = Scale((ulong)points[i].ItemCount, (ulong)maxItems),
            };
        }

        return new HistoryTrend(points)
        {
            SkippedCount = history.SkippedSnapshotIds.Count,
        };
    }

    /// <summary>Scales a value against the window maximum, with a floor so a non-zero value stays visible.</summary>
    private static int Scale(ulong value, ulong maximum)
    {
        if (value == 0 || maximum == 0)
        {
            return 0;
        }

        int height = (int)Math.Round((double)value / maximum * MaxBarHeight, MidpointRounding.AwayFromZero);
        return Math.Clamp(height, MinVisibleBarHeight, MaxBarHeight);
    }

    private static string FormatDelta(int itemDelta, long byteDelta)
    {
        if (itemDelta == 0 && byteDelta == 0)
        {
            return "no change";
        }

        var parts = new List<string>(2);
        if (itemDelta != 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{itemDelta.ToString("+0;-0;0", CultureInfo.InvariantCulture)} item(s)"));
        }

        if (byteDelta != 0)
        {
            parts.Add(byteDelta < 0
                ? "-" + FormatBytes((ulong)-byteDelta)
                : "+" + FormatBytes((ulong)byteDelta));
        }

        return string.Join(", ", parts);
    }

    private static string FormatBytes(ulong bytes)
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
}

using Coppice.Core.Snapshots;

namespace Coppice.Application;

/// <summary>One stored scan in a history, paired with its diff against the previous point.</summary>
public sealed record SnapshotPoint(Snapshot Snapshot, SnapshotDiff Diff)
{
    public string SnapshotId => Snapshot.SnapshotId;

    public DateTimeOffset CreatedAtUtc => Snapshot.CreatedAtUtc;

    public int ItemCount => Diff.ItemCount;

    public ulong TotalBytes => Diff.TotalBytes;
}

/// <summary>
/// An ordered run of snapshots and the growth between them (T-041).
/// <para>
/// Points are oldest first, because that is the order a trend reads in. Ordering is by
/// <see cref="Snapshot.CreatedAtUtc"/> with the id as a tie-break, so two snapshots captured in the
/// same second still produce one deterministic report. The first point's diff is the baseline case
/// from <see cref="SnapshotDiff.Between"/>.
/// </para>
/// <para>
/// <see cref="SkippedSnapshotIds"/> names stored snapshots a caller asked for but could not read (an
/// old schema, a truncated file). They are reported rather than silently dropped: a gap in a trend is
/// information, and hiding it would let the chart imply the machine was unchanged for that scan.
/// </para>
/// </summary>
public sealed record SnapshotHistory
{
    public required IReadOnlyList<SnapshotPoint> Points { get; init; }

    public IReadOnlyList<string> SkippedSnapshotIds { get; init; } = [];

    public bool IsEmpty => Points.Count == 0;

    public SnapshotPoint? Earliest => Points.Count == 0 ? null : Points[0];

    public SnapshotPoint? Latest => Points.Count == 0 ? null : Points[^1];

    /// <summary>Wraps and orders snapshots, computing each point's diff against the previous one.</summary>
    public static SnapshotHistory Build(
        IEnumerable<Snapshot> snapshots,
        IEnumerable<string>? skippedSnapshotIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        Snapshot[] ordered =
        [
            .. snapshots
                .OrderBy(snapshot => snapshot.CreatedAtUtc)
                .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal),
        ];

        var points = new List<SnapshotPoint>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            Snapshot? previous = i == 0 ? null : ordered[i - 1];
            points.Add(new SnapshotPoint(ordered[i], SnapshotDiff.Between(previous, ordered[i])));
        }

        return new SnapshotHistory
        {
            Points = points,
            SkippedSnapshotIds = skippedSnapshotIds is null
                ? []
                : [.. skippedSnapshotIds.Order(StringComparer.Ordinal)],
        };
    }
}

using Coppice.Core.Domain;
using Coppice.Core.Snapshots;

namespace Coppice.Application;

/// <summary>One ecosystem's totals in a snapshot, and how they changed since the previous one.</summary>
/// <param name="Ecosystem">The ecosystem id the totals belong to.</param>
/// <param name="ItemCount">Items in this ecosystem in the later snapshot.</param>
/// <param name="TotalBytes">Bytes in this ecosystem in the later snapshot.</param>
/// <param name="ItemDelta">Change in item count; negative when the ecosystem shrank.</param>
/// <param name="ByteDelta">Change in bytes; negative when the ecosystem shrank.</param>
public sealed record EcosystemDelta(
    string Ecosystem,
    int ItemCount,
    ulong TotalBytes,
    int ItemDelta,
    long ByteDelta)
{
    public bool Grew => ItemDelta > 0 || ByteDelta > 0;

    public bool Shrank => ItemDelta < 0 || ByteDelta < 0;
}

/// <summary>
/// The difference between two scans (T-041, nice-to-have #4).
/// <para>
/// Totals come from the items themselves rather than <see cref="Snapshot.Usage"/>: the usage breakdown
/// buckets bytes by risk, and a snapshot loaded from disk is checksum-verified but a caller may also
/// hand in one built in memory. Summing the items is the one answer that cannot disagree with the
/// inventory the rest of the report shows.
/// </para>
/// <para>
/// The first snapshot has no previous one. That is a BASELINE, not a machine that grew from zero: its
/// deltas are zero and its new/removed id lists are empty, so a trend cannot report a first scan as
/// "+4 GB" and send the user looking for a culprit.
/// </para>
/// <para>
/// An ecosystem present on only one side still appears, with the missing side counted as zero. An item
/// that moved between ecosystems has a different <see cref="Item.ItemId"/> on each side (the id includes
/// the ecosystem), so it shows up once as removed and once as new — which is exactly what moved means,
/// and never throws.
/// </para>
/// </summary>
public sealed record SnapshotDiff
{
    /// <summary>The later snapshot this diff describes.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>The earlier snapshot, or null when this is the baseline.</summary>
    public string? PreviousSnapshotId { get; init; }

    public bool HasPrevious => PreviousSnapshotId is not null;

    public required int ItemCount { get; init; }

    public required int PreviousItemCount { get; init; }

    public required int ItemDelta { get; init; }

    public required ulong TotalBytes { get; init; }

    public required ulong PreviousTotalBytes { get; init; }

    public required long ByteDelta { get; init; }

    /// <summary>Every ecosystem either snapshot touched, sorted ordinally for a stable report.</summary>
    public required IReadOnlyList<EcosystemDelta> Ecosystems { get; init; }

    /// <summary>Item ids in the later snapshot only, sorted ordinally.</summary>
    public required IReadOnlyList<string> NewItemIds { get; init; }

    /// <summary>Item ids in the earlier snapshot only, sorted ordinally.</summary>
    public required IReadOnlyList<string> RemovedItemIds { get; init; }

    /// <summary>
    /// Compares two snapshots. <paramref name="previous"/> is null for a baseline (the first stored
    /// scan, or the oldest one a caller asked to load).
    /// </summary>
    public static SnapshotDiff Between(Snapshot? previous, Snapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Dictionary<string, Bucket> currentByEcosystem = TallyByEcosystem(current.Items);
        Dictionary<string, Bucket> previousByEcosystem = previous is null
            ? new Dictionary<string, Bucket>(StringComparer.Ordinal)
            : TallyByEcosystem(previous.Items);

        IEnumerable<string> previousEcosystems = previous is null ? [] : previous.Ecosystems;

        string[] ecosystems =
        [
            .. currentByEcosystem.Keys
                .Concat(previousByEcosystem.Keys)
                .Concat(current.Ecosystems)
                .Concat(previousEcosystems)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        var deltas = new List<EcosystemDelta>(ecosystems.Length);
        foreach (string ecosystem in ecosystems)
        {
            Bucket now = currentByEcosystem.GetValueOrDefault(ecosystem);
            Bucket before = previousByEcosystem.GetValueOrDefault(ecosystem);

            // A baseline compares against nothing, so it grows by nothing. The totals are still
            // reported; only the change is suppressed.
            int itemDelta = previous is null ? 0 : now.Items - before.Items;
            long byteDelta = previous is null ? 0 : (long)now.Bytes - (long)before.Bytes;

            deltas.Add(new EcosystemDelta(ecosystem, now.Items, now.Bytes, itemDelta, byteDelta));
        }

        int itemCount = current.Items.Count;
        ulong totalBytes = SumBytes(current.Items);
        int previousItemCount = previous?.Items.Count ?? 0;
        ulong previousTotalBytes = previous is null ? 0 : SumBytes(previous.Items);

        return new SnapshotDiff
        {
            SnapshotId = current.SnapshotId,
            PreviousSnapshotId = previous?.SnapshotId,
            ItemCount = itemCount,
            PreviousItemCount = previousItemCount,
            ItemDelta = previous is null ? 0 : itemCount - previousItemCount,
            TotalBytes = totalBytes,
            PreviousTotalBytes = previousTotalBytes,
            ByteDelta = previous is null ? 0 : (long)totalBytes - (long)previousTotalBytes,
            Ecosystems = deltas,
            NewItemIds = previous is null ? [] : Added(current.Items, previous.Items),
            RemovedItemIds = previous is null ? [] : Added(previous.Items, current.Items),
        };
    }

    private static Dictionary<string, Bucket> TallyByEcosystem(IReadOnlyList<Item> items)
    {
        var tally = new Dictionary<string, Bucket>(StringComparer.Ordinal);

        foreach (Item item in items)
        {
            Bucket current = tally.GetValueOrDefault(item.Ecosystem);
            tally[item.Ecosystem] = new Bucket(current.Items + 1, current.Bytes + item.Size);
        }

        return tally;
    }

    private static ulong SumBytes(IReadOnlyList<Item> items)
    {
        ulong total = 0;
        foreach (Item item in items)
        {
            total += item.Size;
        }

        return total;
    }

    /// <summary>Ids in <paramref name="right"/> but not <paramref name="left"/>, ordered ordinally.</summary>
    private static string[] Added(IReadOnlyList<Item> right, IReadOnlyList<Item> left)
    {
        HashSet<string> leftIds = [.. left.Select(item => item.ItemId)];

        return
        [
            .. right
                .Select(item => item.ItemId)
                .Where(id => !leftIds.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    private readonly record struct Bucket(int Items, ulong Bytes);
}

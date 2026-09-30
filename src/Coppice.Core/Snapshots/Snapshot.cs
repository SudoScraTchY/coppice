using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Snapshots;

/// <summary>
/// A persisted scan. Snapshots are what make reports comparable over time, which is the whole basis
/// of the v0.4 history/trends feature — and what lets a plan be validated against the machine state
/// that actually produced it rather than against live state that has since moved.
/// </summary>
public sealed record Snapshot
{
    /// <summary>
    /// Schema version of the document. The authoritative value is
    /// <see cref="SnapshotStore.CurrentSchemaVersion"/>; a snapshot written by an older or newer
    /// build is refused on load rather than guessed at.
    /// </summary>
    public int SchemaVersion { get; init; }

    public required string SnapshotId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required IReadOnlyList<Item> Items { get; init; }

    public required IReadOnlyList<ScanIssue> Issues { get; init; }

    /// <summary>Ecosystems the scan covered, including those with no items.</summary>
    public IReadOnlyList<string> Ecosystems { get; init; } = [];

    public UsageBreakdown Usage { get; init; } = UsageBreakdown.Zero;

    /// <summary>Checksum over the canonical content, so tampering is detectable (FR-11).</summary>
    public required string Checksum { get; init; }

    /// <summary>
    /// The content the checksum covers. Deliberately EXCLUDES <see cref="SnapshotId"/> and
    /// <see cref="CreatedAtUtc"/>: the checksum answers "is this the same machine state?", which is
    /// what a plan is justified by and what a trend report compares. If it covered the id, every
    /// re-scan would look different and neither use would work.
    /// </summary>
    private SnapshotContent Content() => new(
        SchemaVersion,
        Items,
        Issues,
        Ecosystems,
        Usage);

    /// <summary>Recomputes the checksum from the content.</summary>
    public string ComputeChecksum() => DomainJson.ComputeChecksum(Content());

    /// <summary>True when the stored checksum matches the content it claims to cover.</summary>
    public bool IsIntact() =>
        string.Equals(Checksum, ComputeChecksum(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a snapshot with a correct checksum and deterministic item order.</summary>
    public static Snapshot Create(
        string snapshotId,
        DateTimeOffset createdAtUtc,
        IEnumerable<Item> items,
        IEnumerable<ScanIssue> issues,
        IEnumerable<string> ecosystems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        Item[] orderedItems = [.. Deterministic.OrderItems(items)];
        SnapshotContent content = new(
            SnapshotStore.CurrentSchemaVersion,
            orderedItems,
            Deterministic.OrderIssues(issues),
            [.. ecosystems.OrderBy(e => e, StringComparer.Ordinal)],
            UsageBreakdown.FromItems(orderedItems));

        return new Snapshot
        {
            SchemaVersion = content.SchemaVersion,
            SnapshotId = snapshotId,
            CreatedAtUtc = createdAtUtc,
            Items = content.Items,
            Issues = content.Issues,
            Ecosystems = content.Ecosystems,
            Usage = content.Usage,
            Checksum = DomainJson.ComputeChecksum(content),
        };
    }

    /// <summary>Id form: <c>snap_20250112T1015Z</c>, sortable as a string and safe as a filename.</summary>
    public static string FormatId(DateTimeOffset timestampUtc) =>
        $"snap_{timestampUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";

    private sealed record SnapshotContent(
        int SchemaVersion,
        IReadOnlyList<Item> Items,
        IReadOnlyList<ScanIssue> Issues,
        IReadOnlyList<string> Ecosystems,
        UsageBreakdown Usage);
}

/// <summary>Why a snapshot document could not be loaded.</summary>
public enum SnapshotLoadFailure
{
    None = 0,
    NotFound,
    Malformed,
    UnsupportedSchemaVersion,
    ChecksumMismatch,
}

/// <summary>The outcome of a load, carrying enough detail to explain a refusal in a report.</summary>
public sealed record SnapshotLoadResult(Snapshot? Snapshot, SnapshotLoadFailure Failure, string Message)
{
    public bool Ok => Snapshot is not null;

    public static SnapshotLoadResult Success(Snapshot snapshot) => new(snapshot, SnapshotLoadFailure.None, string.Empty);

    public static SnapshotLoadResult Fail(SnapshotLoadFailure failure, string message) => new(null, failure, message);
}

using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Snapshots;

/// <summary>
/// Persists and retrieves snapshots (T-009, 10-formats "Snapshot store").
/// <para>
/// Read-only with respect to the user's machine in the sense that matters: this writes only under
/// the state root it is given, never to a scanned location. Everything goes through
/// <see cref="IStateStore"/>, so the core still touches no filesystem type directly.
/// </para>
/// <para>
/// A schema version this build does not understand is REFUSED, not best-guessed. A snapshot feeds
/// plan validation, and guessing at a shape we cannot parse is how a plan ends up justified by
/// numbers that were never measured.
/// </para>
/// </summary>
public sealed class SnapshotStore
{
    /// <summary>Bumped on any incompatible change to the snapshot document shape.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string KeyPrefix = "snapshots/";

    private readonly IStateStore _state;
    private readonly IClock _clock;

    public SnapshotStore(IStateStore state, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        // Core must not depend on the adapters, so the default clock is a tiny inline one rather
        // than SystemClock from Coppice.Adapters (NFR-08).
        _clock = clock ?? new DefaultClock();
    }

    /// <summary>Writes a snapshot and returns it with its checksum filled in.</summary>
    public async Task<Snapshot> SaveAsync(Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await _state.WriteAsync(
            KeyPrefix + snapshot.SnapshotId + ".json",
            "application/json",
            DomainJson.Serialize(snapshot, indented: true),
            cancellationToken).ConfigureAwait(false);

        return snapshot;
    }

    /// <summary>Captures a scan result as a snapshot with a generated id and a correct checksum.</summary>
    public Task<Snapshot> CaptureAsync(
        IReadOnlyList<Item> items,
        IReadOnlyList<ScanIssue> issues,
        IEnumerable<string> ecosystems,
        string? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(issues);

        DateTimeOffset now = _clock.UtcNow;
        Snapshot snapshot = Snapshot.Create(
            snapshotId ?? Snapshot.FormatId(now),
            now,
            items,
            issues,
            ecosystems);

        return SaveAsync(snapshot, cancellationToken);
    }

    public async Task<SnapshotLoadResult> LoadAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        StateDocument? document = await _state
            .ReadAsync(KeyPrefix + snapshotId + ".json", cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return SnapshotLoadResult.Fail(SnapshotLoadFailure.NotFound, $"No snapshot '{snapshotId}'.");
        }

        Snapshot? snapshot;
        try
        {
            snapshot = DomainJson.Deserialize<Snapshot>(document.Json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return SnapshotLoadResult.Fail(SnapshotLoadFailure.Malformed, $"Snapshot '{snapshotId}' is not valid JSON: {ex.Message}");
        }

        if (snapshot is null)
        {
            return SnapshotLoadResult.Fail(SnapshotLoadFailure.Malformed, $"Snapshot '{snapshotId}' deserialized to nothing.");
        }

        if (snapshot.SchemaVersion != CurrentSchemaVersion)
        {
            return SnapshotLoadResult.Fail(
                SnapshotLoadFailure.UnsupportedSchemaVersion,
                $"Snapshot '{snapshotId}' has schema version {snapshot.SchemaVersion}; this build understands {CurrentSchemaVersion}. "
                    + "It was not loaded rather than guessed at.");
        }

        if (!snapshot.IsIntact())
        {
            return SnapshotLoadResult.Fail(
                SnapshotLoadFailure.ChecksumMismatch,
                $"Snapshot '{snapshotId}' failed its checksum. The file has been modified or truncated since it was written.");
        }

        return SnapshotLoadResult.Success(snapshot);
    }

    /// <summary>
    /// All snapshot ids, newest first. "Newest" is by the TIMESTAMP inside the id, not by the id
    /// string: ids like <c>snap_old</c> sort after <c>snap_new</c> lexically, so a lexical sort
    /// would return the wrong snapshot as the latest. Ids carrying a real timestamp sort correctly
    /// either way; the others are ordered by what can be parsed and fall to the end.
    /// </summary>
    public IReadOnlyList<string> ListIds() => [.. _state.ListKeys(KeyPrefix)
        .Select(k => k[KeyPrefix.Length..])
        .Where(k => k.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        .Select(k => k[..^".json".Length])
        .OrderByDescending(TimestampOf, Comparer<DateTimeOffset>.Default)
        .ThenByDescending(id => id, StringComparer.Ordinal)];

    /// <summary>
    /// The timestamp encoded in a canonical snapshot id, or <see cref="DateTimeOffset.MinValue"/> for
    /// an id that does not carry one (a user-named or hand-written snapshot).
    /// </summary>
    private static DateTimeOffset TimestampOf(string snapshotId)
    {
        const string Prefix = "snap_";
        const string Suffix = "Z";

        if (!snapshotId.StartsWith(Prefix, StringComparison.Ordinal)
            || !snapshotId.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return DateTimeOffset.MinValue;
        }

        string stamp = snapshotId[Prefix.Length..^Suffix.Length];
        if (stamp.Length != 15 || stamp[8] != 'T')
        {
            return DateTimeOffset.MinValue;
        }

        string iso = $"{stamp[..4]}-{stamp[4..6]}-{stamp[6..8]}T{stamp[9..11]}:{stamp[11..13]}:{stamp[13..15]}Z";
        return DateTimeOffset.TryParse(
            iso,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> snapshots and returns the ids it removed.
    /// Pruned ids are RETURNED rather than merely logged, so a caller can tell the user exactly what
    /// history is gone — silently discarding data is how a user loses the baseline they needed.
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAsync(int keep, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);

        IReadOnlyList<string> ids = ListIds();
        if (ids.Count <= keep)
        {
            return [];
        }

        string[] pruned = [.. ids.Skip(keep)];
        foreach (string id in pruned)
        {
            await _state.DeleteAsync(KeyPrefix + id + ".json", cancellationToken).ConfigureAwait(false);
        }

        return pruned;
    }

    /// <summary>The newest snapshot, or null when the store is empty.</summary>
    public async Task<SnapshotLoadResult> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        string? newest = ListIds().FirstOrDefault();
        return newest is null
            ? SnapshotLoadResult.Fail(SnapshotLoadFailure.NotFound, "No snapshots have been saved yet.")
            : await LoadAsync(newest, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string snapshotId, CancellationToken cancellationToken = default) =>
        _state.DeleteAsync(KeyPrefix + snapshotId + ".json", cancellationToken);

    private sealed class DefaultClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

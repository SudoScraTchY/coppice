using Coppice.Core.Snapshots;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>Reads stored snapshots for a front-end that has no scan of its own to show yet.</summary>
public sealed class SnapshotLibrary
{
    private readonly SnapshotStore _store;

    public SnapshotLibrary(IStateStore state, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _store = new SnapshotStore(state, clock);
    }

    public async Task<Snapshot?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        string? newest = _store.ListIds().FirstOrDefault();
        if (newest is null)
        {
            return null;
        }

        SnapshotLoadResult loaded = await _store.LoadAsync(newest, cancellationToken).ConfigureAwait(false);
        return loaded.Ok ? loaded.Snapshot : null;
    }

    /// <summary>
    /// The newest <paramref name="limit"/> snapshots, oldest first, with each one's diff against the
    /// previous. A stored snapshot that cannot be loaded (old schema, truncated file) is named in
    /// <see cref="SnapshotHistory.SkippedSnapshotIds"/> instead of failing the whole report: one bad
    /// file must not erase the history around it.
    /// </summary>
    public async Task<SnapshotHistory> LoadHistoryAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var loaded = new List<Snapshot>(limit);
        var skipped = new List<string>();

        foreach (string id in _store.ListIds(limit))
        {
            SnapshotLoadResult result = await _store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                loaded.Add(result.Snapshot!);
            }
            else
            {
                skipped.Add(id);
            }
        }

        return SnapshotHistory.Build(loaded, skipped);
    }
}

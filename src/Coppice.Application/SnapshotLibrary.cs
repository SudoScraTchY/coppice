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
}

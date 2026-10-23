using Coppice.Core.Quarantine;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>One quarantine row, with the filesystem check a "missing content" marker needs.</summary>
public sealed record QuarantineItem(QuarantineEntry Entry, bool ContentPresent);

/// <summary>The quarantine view a front-end lists and restores through.</summary>
public sealed class QuarantineLibrary
{
    private readonly IFileSystem _fs;
    private readonly QuarantineIndex _index;
    private readonly QuarantineRestore _restore;

    public QuarantineLibrary(IFileSystem fs, QuarantineIndex index, QuarantineRestore restore)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(restore);

        _fs = fs;
        _index = index;
        _restore = restore;
    }

    public async Task<IReadOnlyList<QuarantineItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<QuarantineEntry> entries = await _index.ListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. entries.Select(entry => new QuarantineItem(
                entry,
                _fs.DirectoryExists(entry.QuarantinePath) || _fs.FileExists(entry.QuarantinePath))),
        ];
    }

    public Task<RestoreResult> RestoreAsync(string entryId, CancellationToken cancellationToken = default) =>
        _restore.RestoreAsync(entryId, cancellationToken);
}

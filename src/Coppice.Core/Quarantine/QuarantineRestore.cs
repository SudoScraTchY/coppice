using Coppice.Core.Audit;
using Coppice.Core.Gateway;
using Coppice.Core.PathSafety;
using Coppice.Ports;

namespace Coppice.Core.Quarantine;

/// <summary>
/// Puts a quarantined item back where it came from (T-032, FR-14).
/// <para>
/// A restore is a rename in the opposite direction, and it re-runs the same safety chain a quarantine did.
/// That symmetry is the point: if the world changed between the move and the restore — the root was
/// re-pointed, the original path now sits somewhere a restore must not touch — the restore refuses exactly
/// as the original deletion would have.
/// </para>
/// <para>
/// The destination is checked for occupancy BEFORE anything moves. Overwriting a path that now holds
/// something else would be the one genuinely destructive thing a restore could do, and "the user probably
/// does not care" is not a reason.
/// </para>
/// </summary>
public sealed class QuarantineRestore
{
    private readonly IFileSystem _fs;
    private readonly OperatingSystemKind _os;
    private readonly QuarantineIndex _index;
    private readonly AuditLog? _audit;

    public QuarantineRestore(IFileSystem fs, OperatingSystemKind os, QuarantineIndex index, AuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(index);

        _fs = fs;
        _os = os;
        _index = index;
        _audit = audit;
    }

    /// <summary>
    /// Restores one entry by id.
    /// <para>
    /// The content hash is verified before the move, not after. Checking afterwards would mean telling the
    /// user "restored, though it was different" — which is worse than refusing, because they would then go
    /// and look for a version that no longer exists in either place.
    /// </para>
    /// </summary>
    /// <summary>Restores one entry by id; the cancellation-free form most callers want.</summary>
    public Task<RestoreResult> RestoreAsync(string entryId) => RestoreAsync(entryId, CancellationToken.None);

    /// <summary>The same restore, for callers already flowing a cancellation signal.</summary>
    public async Task<RestoreResult> RestoreAsync(string entryId, CancellationToken cancellationToken)
    {
        QuarantineEntry? entry = await _index.GetAsync(entryId, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RestoreResult.Fail(RestoreFailure.NotFound, $"No quarantine entry '{entryId}'.");
        }

        if (!_fs.DirectoryExists(entry.QuarantinePath) && !_fs.FileExists(entry.QuarantinePath))
        {
            return RestoreResult.Fail(
                RestoreFailure.ContentMissing,
                $"The quarantined copy at '{entry.QuarantinePath}' is gone. It was pruned, or removed by hand.");
        }

        // Verify the content is what we put there. Cheap relative to the guarantee it buys.
        if (!entry.ContentHash.Equals(QuarantineIndex.HashTree(_fs, entry.QuarantinePath), StringComparison.Ordinal))
        {
            return RestoreResult.Fail(
                RestoreFailure.ContentAltered,
                $"The quarantined copy at '{entry.QuarantinePath}' no longer matches what was recorded. "
                    + "Restoring altered content would overwrite a location the user may have since cleaned out.");
        }

        // The original destination must still be inside its root, and must not have been filled by
        // something else in the meantime.
        var paths = new IFileSystemPaths(_fs, _os);
        ContainmentResult containment = ContainmentCheck.Check(paths, entry.Root, entry.OriginalPath);

        if (!containment.Safe)
        {
            return RestoreResult.Fail(
                RestoreFailure.DestinationUnsafe,
                $"'{entry.OriginalPath}' is no longer a safe destination: {containment.Message}");
        }

        if (_fs.DirectoryExists(entry.OriginalPath) || _fs.FileExists(entry.OriginalPath))
        {
            return RestoreResult.Fail(
                RestoreFailure.DestinationOccupied,
                $"'{entry.OriginalPath}' already exists. Move or rename it first — restoring would replace it.");
        }

        try
        {
            // The same-volume rename the quarantine used, run backwards. If the volume changed underneath
            // us this throws rather than copying, and a copy would double the space the user was trying to
            // free — the failure mode 06 calls out explicitly.
            _fs.Move(entry.QuarantinePath, entry.OriginalPath);
        }
        catch (System.IO.IOException ex)
        {
            return RestoreResult.Fail(
                RestoreFailure.MoveFailed,
                $"Could not move '{entry.QuarantinePath}' back: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return RestoreResult.Fail(
                RestoreFailure.MoveFailed,
                $"Not permitted to restore '{entry.OriginalPath}': {ex.Message}");
        }

        await _index.DeleteAsync(entryId, cancellationToken).ConfigureAwait(false);

        // A restore is a mutation, not an undo of one (S-11): the item moved again, so the log records it.
        await AppendAuditBestEffortAsync(new AuditRecord
        {
            StepId = entry.ItemId,
            Eco = entry.Ecosystem,
            Kind = entry.Kind,
            Action = AuditRecord.RestoreAction,
            From = entry.QuarantinePath,
            To = entry.OriginalPath,
            Bytes = entry.SizeBytes,
            Result = AuditRecord.OkResult,
        }).ConfigureAwait(false);

        return RestoreResult.Ok(
            entry.OriginalPath,
            $"Restored '{entry.Name} {entry.Version}' to '{entry.OriginalPath}'.");
    }

    /// <summary>
    /// Removes quarantine content older than <paramref name="age"/>, returning what it discarded.
    /// <para>
    /// Deletes rather than moves, so this is the one operation in the project that discards data
    /// permanently — which is why it is a distinct, explicitly-called method rather than a side effect of
    /// listing. It also removes the index entries, since an index row pointing at a deleted directory is a
    /// restore that fails for a reason the user cannot act on.
    /// </para>
    /// </summary>
    /// <summary>Removes entries older than <paramref name="age"/>; the cancellation-free form most callers want.</summary>
    public Task<IReadOnlyList<QuarantineEntry>> PruneAsync(TimeSpan age) => PruneAsync(age, CancellationToken.None);

    /// <summary>The same prune, for callers already flowing a cancellation signal.</summary>
    public async Task<IReadOnlyList<QuarantineEntry>> PruneAsync(
        TimeSpan age,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<QuarantineEntry> all = await _index.ListAsync(cancellationToken).ConfigureAwait(false);

        var pruned = new List<QuarantineEntry>();

        foreach (QuarantineEntry entry in all)
        {
            if (DateTimeOffset.UtcNow - entry.QuarantinedAtUtc < age)
            {
                continue;
            }

            try
            {
                if (_fs.DirectoryExists(entry.QuarantinePath) || _fs.FileExists(entry.QuarantinePath))
                {
                    _fs.DeleteFile(entry.QuarantinePath, recursive: true);
                }

                await _index.DeleteAsync(entry.EntryId, cancellationToken).ConfigureAwait(false);

                // The discard happened; the audit line records it (S-12). A failed append must not turn a
                // completed prune into a reported failure — the index row is already gone, and `audit
                // verify` reports the gap from the log's side.
                await AppendAuditBestEffortAsync(new AuditRecord
                {
                    StepId = entry.ItemId,
                    Eco = entry.Ecosystem,
                    Kind = entry.Kind,
                    Action = AuditRecord.PruneAction,
                    From = entry.QuarantinePath,
                    Bytes = entry.SizeBytes,
                    Result = AuditRecord.OkResult,
                }).ConfigureAwait(false);

                pruned.Add(entry);
            }
            catch (System.IO.IOException)
            {
                // Left in place and still indexed. A prune that reported success for something it could
                // not delete would make `quarantine list` a list of lies.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }

        return pruned;
    }

    /// <summary>
    /// A cross-volume restore is refused rather than attempted.
    /// <para>
    /// The quarantine move already commits to same-volume; this makes the failure explicit if the volume
    /// layout changed between the two operations. Copy-then-delete would use the space the user was trying
    /// to free, could fail halfway leaving two copies, and is not undoable — so the safe answer is to say so.
    /// </para>
    /// </summary>
    public RestoreResult RefuseCrossVolume(string quarantinePath, string originalPath)
    {
        string source = VolumeOf(quarantinePath);
        string destination = VolumeOf(originalPath);

        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return RestoreResult.Fail(
                RestoreFailure.MoveFailed,
                $"'{quarantinePath}' and '{originalPath}' are on different volumes ('{source}' vs '{destination}'). "
                    + "Coppice will not copy across volumes to restore, because that doubles the space and cannot be "
                    + "undone part-way. Move the files manually if this is what you need.");
        }

        return RestoreResult.Ok(originalPath, "Same volume; a plain rename will work.");
    }

    /// <summary>The volume a path lives on, or an empty string when it cannot be determined.</summary>
    private string VolumeOf(string path)
    {
        if (_os == OperatingSystemKind.Windows)
        {
            return path.Length >= 2 && path[1] == ':' ? path[..2] : string.Empty;
        }

        // On unix the whole filesystem is one namespace for rename purposes; a mount point is the real
        // boundary, and determining one portably is not possible from a path string alone.
        return "/";
    }

    /// <summary>
    /// Records one mutation in the audit log. A failed append must not fail a mutation that already
    /// happened; the resulting gap is exactly what `audit verify` detects from the other side.
    /// </summary>
    private async Task AppendAuditBestEffortAsync(AuditRecord record)
    {
        if (_audit is null)
        {
            return;
        }

        try
        {
            await _audit.AppendAsync(record).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The mutation stands; the log carries a gap that verify reports.
        }
    }
}

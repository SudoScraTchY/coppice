using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Quarantine;

/// <summary>
/// One quarantined item, as recorded in the index (T-032, FR-14, S-11, 06-safety-model "Quarantine").
/// <para>
/// The original path is kept because that is the only thing that makes a restore possible — a quarantine
/// that cannot name where the item came from is a trash can with extra steps. The timestamps are kept for
/// <c>quarantine prune --older-than</c> and for answering "when did this happen", and the item id ties the
/// entry back to the plan that caused it.
/// </para>
/// </summary>
public sealed record QuarantineEntry
{
    /// <summary>Stable id for this quarantine entry. Derived, so a re-quarantine of the same item is idempotent.</summary>
    public required string EntryId { get; init; }

    /// <summary>Where the item was before it was moved. The restore destination.</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Where it is now, under the quarantine root.</summary>
    public required string QuarantinePath { get; init; }

    /// <summary>The validated root it came from — the containment boundary, recorded for the restore check.</summary>
    public required string Root { get; init; }

    /// <summary>Location id, for grouping in <c>quarantine list</c>.</summary>
    public required string LocationId { get; init; }

    /// <summary>The plan step that caused this move, for the audit trail.</summary>
    public required string ItemId { get; init; }

    public required ulong SizeBytes { get; init; }

    public required DateTimeOffset QuarantinedAtUtc { get; init; }

    /// <summary>
    /// Hash of the item's contents when it was quarantined.
    /// <para>
    /// This is what makes "restore is byte-identical" checkable rather than asserted. Size alone would miss
    /// a same-length rewrite; mtime alone is defeated by a restore that preserves it. Hashing on the way IN
    /// costs one read of data already being moved, and it is what turns T-032's acceptance criterion into a
    /// comparison rather than a claim.
    /// </para>
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>Where the item came from, for a user reading the list.</summary>
    public required string Ecosystem { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }
}

/// <summary>Why a restore did not happen.</summary>
public enum RestoreFailure
{
    /// <summary>No entry with that id.</summary>
    NotFound,

    /// <summary>The quarantined content is gone — pruned, or removed by hand.</summary>
    ContentMissing,

    /// <summary>Something already occupies the original path. Restoring would overwrite it.</summary>
    DestinationOccupied,

    /// <summary>The original path is outside its root, or the root no longer validates.</summary>
    DestinationUnsafe,

    /// <summary>The quarantined content no longer hashes to what the index recorded.</summary>
    ContentAltered,

    /// <summary>The move itself failed (permissions, cross-volume, disk).</summary>
    MoveFailed,
}

/// <summary>The outcome of a restore attempt.</summary>
public sealed record RestoreResult
{
    private RestoreResult(bool restored, RestoreFailure? failure, string? path, string message)
    {
        Restored = restored;
        Failure = failure;
        RestoredPath = path;
        Message = message;
    }

    public bool Restored { get; }

    public RestoreFailure? Failure { get; }

    /// <summary>Where the item now lives, when it was restored.</summary>
    public string? RestoredPath { get; }

    public string Message { get; }

    /// <summary>
    /// False when the item moved back but its audit line could not be written. The restore still
    /// happened; the caller must not claim the mutation is in the log.
    /// </summary>
    public bool Audited { get; private init; } = true;

    public static RestoreResult Ok(string path, string message, bool audited = true) =>
        new(true, null, path, message) { Audited = audited };

    public static RestoreResult Fail(RestoreFailure failure, string message) => new(false, failure, null, message);
}

/// <summary>
/// Reads and writes the quarantine index (T-032).
/// <para>
/// The index is stored through <see cref="IStateStore"/> rather than beside the quarantined content,
/// because the content lives on the user's volume and the state store lives in Coppice's own state
/// directory. That separation is deliberate: a user who clears their cache directory should not silently
/// destroy the record of what was moved out of it.
/// </para>
/// <para>
/// One JSON document per entry rather than one growing file. A single index file would need read-modify-write
/// under concurrency, and a crash mid-write would lose every entry rather than one. Per-entry documents
/// also make "is this entry still there" a key lookup, which is what restore needs.
/// </para>
/// </summary>
public sealed class QuarantineIndex
{
    /// <summary>Bumped on any incompatible change to the index document.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string KeyPrefix = "quarantine/";

    private readonly IStateStore _state;

    public QuarantineIndex(IStateStore state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <summary>Writes or replaces one entry.</summary>
    public async Task SaveAsync(QuarantineEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        QuarantineDocument document = new(CurrentSchemaVersion, entry);

        await _state.WriteAsync(
            KeyPrefix + entry.EntryId + ".json",
            "application/json",
            DomainJson.Serialize(document, indented: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One entry by id, or null. A document from a different schema is refused rather than parsed: the
    /// entry records where a user's files went, and guessing at its shape could send a restore to the
    /// wrong place.
    /// </summary>
    public async Task<QuarantineEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        StateDocument? document = await _state.ReadAsync(KeyPrefix + entryId + ".json", cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        QuarantineDocument? parsed;

        try
        {
            parsed = DomainJson.Deserialize<QuarantineDocument>(document.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.SchemaVersion != CurrentSchemaVersion)
        {
            return null;
        }

        return parsed.Entry;
    }

    /// <summary>
    /// Every entry, newest first.
    /// <para>
    /// Sorted by timestamp rather than by key, because the key is a content hash: a user reading
    /// <c>quarantine list</c> is asking "what did you just do", and hash order would answer with something
    /// arbitrary.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<QuarantineEntry>();

        foreach (string key in _state.ListKeys(KeyPrefix).Order(StringComparer.Ordinal))
        {
            StateDocument? document = await _state.ReadAsync(key, cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                continue;
            }

            try
            {
                QuarantineDocument? parsed = DomainJson.Deserialize<QuarantineDocument>(document.Json);

                if (parsed is not null && parsed.SchemaVersion == CurrentSchemaVersion)
                {
                    entries.Add(parsed.Entry);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A corrupt entry is skipped rather than fatal: the others are still restorable, and losing
                // the whole list because one document is malformed would be a worse outcome than listing
                // twenty-nine of thirty.
            }
        }

        return
        [
            .. entries
                .OrderByDescending(e => e.QuarantinedAtUtc)
                .ThenBy(e => e.EntryId, StringComparer.Ordinal),
        ];
    }

    /// <summary>Removes an entry's record. The caller has already moved the content back.</summary>
    public Task DeleteAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        return _state.DeleteAsync(KeyPrefix + entryId + ".json", cancellationToken);
    }

    /// <summary>
    /// A stable id for a quarantined item: the original path and the moment of the move.
    /// <para>
    /// Derived rather than random so that re-quarantining the same path at the same instant is idempotent —
    /// a gateway that retried would otherwise leave two index entries pointing at one directory, and
    /// restoring both would fail on the second.
    /// </para>
    /// </summary>
    public static string FormatEntryId(string originalPath, DateTimeOffset atUtc)
    {
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{originalPath}|{atUtc.UtcDateTime:O}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        return Convert.ToHexStringLower(hash[..8]);
    }

    /// <summary>
    /// Hashes a directory tree's contents, for the byte-identity check.
    /// <para>
    /// Paths AND bytes: hashing contents alone would treat two different trees with identical files as the
    /// same item, and would miss a restore that put files in the wrong place.
    /// </para>
    /// <para>
    /// Entries are ordered ORDINALLY, not with the per-OS case rules. A hash computed on Windows and one
    /// computed on Linux for the same tree would otherwise differ purely because NTFS is case-insensitive
    /// and ext4 is not — which would make the byte-identity guarantee fail for reasons that have nothing to
    /// do with the bytes.
    /// </para>
    /// </summary>
    public static string HashTree(IFileSystem fs, string path)
    {
        var builder = new StringBuilder();
        Walk(fs, path, builder);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    private static void Walk(IFileSystem fs, string path, StringBuilder builder)
    {
        foreach (FileEntry entry in fs.EnumerateEntries(path)
            .OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            // The RELATIVE path goes in, not the absolute one: a restore must land at the same absolute
            // path but the tree itself may be compared from a different root.
            string relative = entry.Path.Length > path.Length ? entry.Path[path.Length..] : entry.Name;

            builder.Append(relative).Append('|').Append(entry.Length).Append('|');

            if (entry.Kind == EntryKind.File)
            {
                builder.Append(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    fs.ReadSmallBytes(entry.Path, 1024 * 1024 * 64))));
            }

            builder.Append('\n');
        }
    }

    private sealed record QuarantineDocument(int SchemaVersion, QuarantineEntry Entry);
}

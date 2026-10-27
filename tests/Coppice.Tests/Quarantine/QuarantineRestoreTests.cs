using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Quarantine;
using Coppice.Ports;
using Coppice.Tests.Support;

using OperatingSystemKind = Coppice.Ports.OperatingSystemKind;
using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Quarantine;

/// <summary>
/// T-032: quarantine + restore (FR-14, S-11).
/// <para>
/// The load-bearing criterion is "restore is byte-identical", which means the test must compare BYTES, not
/// assert that a file exists afterwards. Existence is what a broken restore looks like: a directory at the
/// right path containing the wrong contents.
/// </para>
/// </summary>
public sealed class QuarantineRestoreTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";

    private static readonly DateTimeOffset _now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => _now;
    }

    /// <summary>A package with two files of known content, so a hash comparison has something to bite on.</summary>
    private static FakeFileSystem Cache(string package = "newtonsoft.json")
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectory($@"{Root}\{package}\1.0.0");
        fs.AddFile($@"{Root}\{package}\1.0.0\newtonsoft.json.nuspec", "<package>real content</package>");
        fs.AddFile($@"{Root}\{package}\1.0.0\lib\net45\newtonsoft.json.dll", "BINARY-ISH-CONTENT-0123456789");
        return fs;
    }

    private static GatewayStep Step(string path) =>
        new()
        {
            ItemId = "abc123",
            Path = path,
            Root = Root,
            AllowlistPattern = "*/*",
            Risk = Risk.Safe,
            Action = RemovalAction.PathDelete(path),
            Fingerprint = new SnapshotFingerprint(path, 0, DateTimeOffset.UnixEpoch),
            Ecosystem = "dotnet",
            Kind = "package",
            Name = "newtonsoft.json",
            Version = "1.0.0",
        };

    /// <summary>
    /// Quarantines through the real gateway, so the index entry is written by the same code path.
    /// <para>
    /// Returns the tree hash captured BEFORE the move as well, because the byte-identity criterion is
    /// "identical to what was there", and the only way to know that is to have measured it first. Hashing
    /// after the move would compare the restored tree against itself and pass regardless.
    /// </para>
    /// </summary>
    private static async Task<(Gateway Gateway, QuarantineIndex Index, FakeFileSystem Fs, FakeStateStore State, string HashBefore)> QuarantineOneAsync(
        string package = "newtonsoft.json")
    {
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);
        FakeFileSystem fs = Cache(package);
        var gateway = new Gateway(fs, Win, index, new FixedClock());

        string path = $@"{Root}\{package}\1.0.0";
        string hashBefore = QuarantineIndex.HashTree(fs, path);

        GatewayOutcome outcome = (await gateway.Execute(Step(path))).Result;

        Assert.Equal(GatewayOutcome.Quarantined, outcome);

        return (gateway, index, fs, state, hashBefore);
    }

    // ---- byte identity --------------------------------------------------------------------------------

    [Fact]
    public async Task A_restored_item_is_byte_identical_to_what_was_quarantined()
    {
        (_, QuarantineIndex index, FakeFileSystem fs, _, string hashBefore) = await QuarantineOneAsync();
        string original = $@"{Root}\newtonsoft.json\1.0.0";

        // The hash of the live tree BEFORE the move, captured by QuarantineOneAsync. Hashing the
        // original path NOW would hash an empty spot — the item is in quarantine — and the assertion
        // would compare emptiness against the restored tree.
        Assert.False(string.IsNullOrEmpty(hashBefore));

        IReadOnlyList<QuarantineEntry> entries = await index.ListAsync();
        QuarantineEntry entry = Assert.Single(entries);

        var restore = new QuarantineRestore(fs, Win, index);
        RestoreResult result = await restore.RestoreAsync(entry.EntryId);

        Assert.True(result.Restored, result.Message);
        Assert.True(fs.DirectoryExists(original));

        // THE acceptance criterion: the tree at the original path hashes to what it did before the move.
        // Not "a directory exists" — a broken restore produces a directory with the wrong contents.
        Assert.Equal(hashBefore, QuarantineIndex.HashTree(fs, original));
    }

    [Fact]
    public async Task A_failed_audit_append_keeps_the_index_row_so_verify_can_see_it()
    {
        // The content moved back, so the restore succeeded. Deleting the index row after a failed
        // append would erase the only record of that mutation; keeping it leaves both sides visible.
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();
        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        var restore = new QuarantineRestore(
            fs,
            Win,
            index,
            new AuditLog(new FailingAppendLog(), new FixedClock()));

        RestoreResult result = await restore.RestoreAsync(entry.EntryId);

        Assert.True(result.Restored, result.Message);
        Assert.False(result.Audited);
        Assert.Contains("audit line could not", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fs.DirectoryExists(entry.OriginalPath));

        QuarantineEntry kept = Assert.Single(await index.ListAsync());
        Assert.Equal(entry.EntryId, kept.EntryId);
    }

    private sealed class FailingAppendLog : IAppendLog
    {
        public Task AppendLineAsync(string key, string line) =>
            throw new System.IO.IOException("synthetic disk full");

        public Task<string?> ReadAllTextAsync(string key) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task The_indexed_hash_is_the_hash_of_what_was_actually_moved()
    {
        // The hash is taken BEFORE the move. Taken after, it would hash the quarantine directory — which
        // would make restore's verification a check of the move, not of what was moved.
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();

        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        Assert.Equal(entry.ContentHash, QuarantineIndex.HashTree(fs, entry.QuarantinePath));
    }

    [Fact]
    public async Task Restoring_removes_the_index_entry()
    {
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();

        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        var restore = new QuarantineRestore(fs, Win, index);
        await restore.RestoreAsync(entry.EntryId);

        // A stale entry would make `quarantine list` show something already restored, and a second restore
        // would fail confusingly rather than saying "nothing to restore".
        Assert.Empty(await index.ListAsync());
    }

    [Fact]
    public async Task The_quarantined_tree_is_gone_from_quarantine_after_restore()
    {
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();

        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        var restore = new QuarantineRestore(fs, Win, index);
        await restore.RestoreAsync(entry.EntryId);

        Assert.False(fs.DirectoryExists(entry.QuarantinePath));
    }

    // ---- index survives a restart ---------------------------------------------------------------------

    [Fact]
    public async Task The_index_survives_a_process_restart()
    {
        // Two separate QuarantineIndex instances over the SAME state store: the second is what a later
        // process sees. An index held in memory would make every restore depend on the run that created it.
        var state = new FakeStateStore();

        // The fake must carry the tree: an empty filesystem cannot pass the lock probe (a missing path
        // probes as Unknown, which the gateway refuses), so the step would never reach the move and the
        // index would stay empty for a reason that has nothing to do with restarts.
        var first = new Gateway(Cache(), Win, new QuarantineIndex(state), new FixedClock());
        _ = first.Execute(Step($@"{Root}\newtonsoft.json\1.0.0"));

        var second = new QuarantineIndex(state);
        IReadOnlyList<QuarantineEntry> entries = await second.ListAsync();

        QuarantineEntry entry = Assert.Single(entries);
        Assert.Equal($@"{Root}\newtonsoft.json\1.0.0", entry.OriginalPath);
        Assert.Equal("newtonsoft.json", entry.Name);
    }

    [Fact]
    public async Task Restore_works_from_a_different_run_over_the_same_state()
    {
        var state = new FakeStateStore();
        FakeFileSystem fs = Cache();

        var applyRun = new Gateway(fs, Win, new QuarantineIndex(state), new FixedClock());

        // Captured BEFORE the move: after it, the original path is an empty spot and the "before" hash
        // would be the hash of nothing.
        string before = QuarantineIndex.HashTree(fs, $@"{Root}\newtonsoft.json\1.0.0");

        _ = applyRun.Execute(Step($@"{Root}\newtonsoft.json\1.0.0"));

        // A fresh index and a fresh restore service — no shared objects with the apply run.
        var restoreRun = new QuarantineRestore(fs, Win, new QuarantineIndex(state));
        QuarantineEntry entry = Assert.Single(await new QuarantineIndex(state).ListAsync());

        RestoreResult result = await restoreRun.RestoreAsync(entry.EntryId);

        Assert.True(result.Restored, result.Message);
        Assert.Equal(before, QuarantineIndex.HashTree(fs, entry.OriginalPath));
    }

    // ---- refusals ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_entry_id_is_refused_rather_than_ignored()
    {
        var index = new QuarantineIndex(new FakeStateStore());
        var restore = new QuarantineRestore(new FakeFileSystem(Win), Win, index);

        RestoreResult result = await restore.RestoreAsync("nope");

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.NotFound, result.Failure);
    }

    [Fact]
    public async Task Restoring_over_an_occupied_path_is_refused()
    {
        // The one genuinely destructive thing a restore could do. "The user probably does not care" is
        // not a reason to overwrite whatever is there.
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();
        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        // Something now occupies the original path.
        fs.AddFile($@"{Root}\newtonsoft.json\1.0.0\something-new.txt", "a newer install");

        var restore = new QuarantineRestore(fs, Win, index);
        RestoreResult result = await restore.RestoreAsync(entry.EntryId);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.DestinationOccupied, result.Failure);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);

        // The newer content is untouched, and the quarantined copy still exists to restore later.
        Assert.True(fs.FileExists($@"{Root}\newtonsoft.json\1.0.0\something-new.txt"));
        Assert.True(fs.DirectoryExists(entry.QuarantinePath));
    }

    [Fact]
    public async Task Restoring_altered_quarantine_content_is_refused()
    {
        // Verified BEFORE the move. Checking afterwards would mean telling the user "restored, though it was
        // different" — and the version they were trying to recover is now gone from both places.
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();
        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        fs.AddFile(
            $"{entry.QuarantinePath}\\something-else.dll",
            "content that was never in the original package");

        var restore = new QuarantineRestore(fs, Win, index);
        RestoreResult result = await restore.RestoreAsync(entry.EntryId);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.ContentAltered, result.Failure);
        Assert.False(fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
    }

    [Fact]
    public async Task Restoring_content_that_was_pruned_by_hand_is_refused_with_a_reason()
    {
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();
        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        fs.DeleteFile(entry.QuarantinePath, recursive: true);

        var restore = new QuarantineRestore(fs, Win, index);
        RestoreResult result = await restore.RestoreAsync(entry.EntryId);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.ContentMissing, result.Failure);
    }

    [Fact]
    public async Task Restoring_to_a_root_that_no_longer_validates_is_refused()
    {
        // The symmetry that matters: if the world changed between the move and the restore, the restore
        // refuses exactly as the original deletion would have.
        (_, QuarantineIndex index, FakeFileSystem fs, _, _) = await QuarantineOneAsync();
        QuarantineEntry entry = Assert.Single(await index.ListAsync());

        // Re-point the entry's root at a denylisted location.
        await index.SaveAsync(entry with { Root = @"C:\Windows" });

        var restore = new QuarantineRestore(fs, Win, index);
        RestoreResult result = await restore.RestoreAsync(entry.EntryId);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.DestinationUnsafe, result.Failure);
    }

    // ---- cross-volume --------------------------------------------------------------------------------

    [Fact]
    public void A_cross_volume_restore_is_refused_rather_than_copied()
    {
        var restore = new QuarantineRestore(new FakeFileSystem(Win), Win, new QuarantineIndex(new FakeStateStore()));

        RestoreResult result = restore.RefuseCrossVolume(@"D:\quarantine\pkg", @"C:\cache\pkg");

        Assert.False(result.Restored);
        Assert.Contains("different volumes", result.Message, StringComparison.OrdinalIgnoreCase);

        // And it explains why, because "it refused" without a reason leaves the user thinking Coppice is broken.
        Assert.Contains("cannot be undone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_same_volume_restore_is_allowed()
    {
        var restore = new QuarantineRestore(new FakeFileSystem(Win), Win, new QuarantineIndex(new FakeStateStore()));

        RestoreResult result = restore.RefuseCrossVolume(@"C:\.coppice\quarantine\pkg", @"C:\cache\pkg");

        Assert.True(result.Restored);
    }

    // ---- prune ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Prune_removes_only_entries_older_than_the_given_age()
    {
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);
        FakeFileSystem fs = Cache();
        var gateway = new Gateway(fs, Win, index, new FixedClock());

        _ = gateway.Execute(Step($@"{Root}\newtonsoft.json\1.0.0"));

        // Age zero means "everything", which is what `--older-than 0` should mean. The clock is pinned so
        // this test does not depend on the day it runs.
        var restore = new QuarantineRestore(fs, Win, index, clock: new FixedClock());
        IReadOnlyList<QuarantineEntry> pruned = await restore.PruneAsync(TimeSpan.Zero);

        Assert.Single(pruned);
        Assert.Empty(await index.ListAsync());
        Assert.False(fs.DirectoryExists(pruned[0].QuarantinePath));
    }

    [Fact]
    public async Task Prune_keeps_entries_newer_than_the_given_age()
    {
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);
        FakeFileSystem fs = Cache();
        var gateway = new Gateway(fs, Win, index, new FixedClock());

        _ = gateway.Execute(Step($@"{Root}\newtonsoft.json\1.0.0"));

        var restore = new QuarantineRestore(fs, Win, index, clock: new FixedClock());
        IReadOnlyList<QuarantineEntry> pruned = await restore.PruneAsync(TimeSpan.FromDays(30));

        Assert.Empty(pruned);
        Assert.Single(await index.ListAsync());
    }

    // ---- the index itself ----------------------------------------------------------------------------

    [Fact]
    public async Task List_is_newest_first()
    {
        // A user asking "what did you just do" must not be answered in content-hash order.
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);

        await index.SaveAsync(Entry("older", DateTimeOffset.UtcNow.AddHours(-2)));
        await index.SaveAsync(Entry("newest", DateTimeOffset.UtcNow));
        await index.SaveAsync(Entry("middle", DateTimeOffset.UtcNow.AddHours(-1)));

        IReadOnlyList<QuarantineEntry> entries = await index.ListAsync();

        Assert.Equal(["newest", "middle", "older"], entries.Select(e => e.Name));
    }

    [Fact]
    public async Task An_entry_from_another_schema_version_is_not_returned()
    {
        // The entry records where a user's files went. Guessing at a shape we cannot parse could send a
        // restore to the wrong place.
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);

        await state.WriteAsync(
            "quarantine/old.json",
            "application/json",
            DomainJson.Serialize(new { schemaVersion = 99, entry = new { originalPath = @"C:\x" } }),
            CancellationToken.None);

        Assert.Empty(await index.ListAsync());
        Assert.Null(await index.GetAsync("old"));
    }

    [Fact]
    public async Task A_corrupt_entry_does_not_hide_the_others()
    {
        // Losing twenty-nine restorable entries because one document is malformed is a worse outcome than
        // listing twenty-nine of thirty.
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);

        await index.SaveAsync(Entry("good-one", DateTimeOffset.UtcNow));
        await state.WriteAsync("quarantine/broken.json", "application/json", "{ not json", CancellationToken.None);

        QuarantineEntry entry = Assert.Single(await index.ListAsync());
        Assert.Equal("good-one", entry.Name);
    }

    [Fact]
    public void An_entry_id_is_stable_for_the_same_path_and_instant()
    {
        // So that a gateway retry does not leave two index entries pointing at one directory.
        string first = QuarantineIndex.FormatEntryId(@"C:\cache\pkg\1.0", _now);
        string second = QuarantineIndex.FormatEntryId(@"C:\cache\pkg\1.0", _now);

        Assert.Equal(first, second);
        Assert.NotEqual(first, QuarantineIndex.FormatEntryId(@"C:\cache\pkg\1.0", _now.AddSeconds(1)));
        Assert.NotEqual(first, QuarantineIndex.FormatEntryId(@"C:\cache\other\1.0", _now));
    }

    [Fact]
    public void The_tree_hash_notices_a_changed_file_of_the_same_length()
    {
        // The case that rules out size-only verification: same byte count, different content.
        var before = new FakeFileSystem(Win);
        before.AddDirectory(Root);
        before.AddFile($@"{Root}\a.dll", "AAAA");

        var after = new FakeFileSystem(Win);
        after.AddDirectory(Root);
        after.AddFile($@"{Root}\a.dll", "BBBB");

        Assert.NotEqual(QuarantineIndex.HashTree(before, Root), QuarantineIndex.HashTree(after, Root));
    }

    [Fact]
    public void The_tree_hash_notices_a_renamed_file_of_the_same_length()
    {
        // And the case that rules out content-only verification: identical bytes, different place.
        var before = new FakeFileSystem(Win);
        before.AddDirectory(Root);
        before.AddFile($@"{Root}\a.dll", "AAAA");

        var after = new FakeFileSystem(Win);
        after.AddDirectory(Root);
        after.AddFile($@"{Root}\b.dll", "AAAA");

        Assert.NotEqual(QuarantineIndex.HashTree(before, Root), QuarantineIndex.HashTree(after, Root));
    }

    private static QuarantineEntry Entry(string name, DateTimeOffset at) =>
        new()
        {
            EntryId = name,
            OriginalPath = $@"{Root}\{name}\1.0.0",
            QuarantinePath = $@"C:\.coppice\quarantine\{name}",
            Root = Root,
            LocationId = "cache",
            ItemId = "abc",
            SizeBytes = 100,
            QuarantinedAtUtc = at,
            ContentHash = "deadbeef",
            Ecosystem = "dotnet",
            Kind = "package",
            Name = name,
            Version = "1.0.0",
        };
}

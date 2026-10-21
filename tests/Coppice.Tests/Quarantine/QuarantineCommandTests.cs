using System.Text.Json;
using Coppice.Cli;
using Coppice.Core.Audit;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Quarantine;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Quarantine;

/// <summary>
/// T-044: <c>coppice quarantine list|restore|prune</c> (FR-14, S-11).
/// <para>
/// The quarantine is the user's undo, and prune is the only operation in Coppice that discards data for
/// good. These tests pin the boundaries that matter: list never mutates or prompts, restore asks before
/// moving and refuses an occupied destination without moving, and prune touches only entries older than
/// the threshold, only after a typed yes, and leaves an audit line for every discard.
/// </para>
/// </summary>
public sealed class QuarantineCommandTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public required FakeFileSystem Fs { get; init; }

        public required FakeEnvironment Env { get; init; }

        public required FakeStateStore State { get; init; }

        public required FakeAppendLog Log { get; init; }

        public required FakeClock Clock { get; init; }

        public QuarantineIndex Index() => new(State);

        public QuarantineCommand Command(FakeApplyConsole console) =>
            new(Fs, Env, State, Log, console, Clock);
    }

    private static World BuildWorld()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        return new World
        {
            Fs = fs,
            Env = new FakeEnvironment(Win).With("NUGET_PACKAGES", Root),
            State = new FakeStateStore(),
            Log = new FakeAppendLog(),
            Clock = new FakeClock(Now),
        };
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

    /// <summary>Quarantines one real tree through the gateway, so the entry and its hash are genuine.</summary>
    private static async Task<QuarantineEntry> QuarantineOneAsync(World world)
    {
        string path = $@"{Root}\newtonsoft.json\1.0.0";
        world.Fs.AddDirectory(path);
        world.Fs.AddFile($@"{path}\newtonsoft.json.nuspec", "<package>real content</package>");

        var index = world.Index();
        var gateway = new Gateway(world.Fs, Win, index, world.Clock);
        GatewayOutcome outcome = (await gateway.Execute(Step(path))).Result;

        Assert.Equal(GatewayOutcome.Quarantined, outcome);

        world.Fs.Writes.Clear();
        return Assert.Single(await index.ListAsync());
    }

    /// <summary>
    /// An index entry with content and a chosen timestamp, for prune's age boundary. The content hash is
    /// real so the entry would also be restorable.
    /// </summary>
    private static async Task<QuarantineEntry> AddEntryAsync(World world, string name, DateTimeOffset atUtc)
    {
        string original = $@"{Root}\{name}\1.0.0";
        string quarantined = $@"{Root}\.coppice\quarantine\nuget-packages\{name}\1.0.0";
        world.Fs.AddDirectory(quarantined);
        world.Fs.AddFile($@"{quarantined}\content.bin", $"quarantined content for {name}");

        var entry = new QuarantineEntry
        {
            EntryId = QuarantineIndex.FormatEntryId(original, atUtc),
            OriginalPath = original,
            QuarantinePath = quarantined,
            Root = Root,
            LocationId = "nuget-packages",
            ItemId = $"item-{name}",
            SizeBytes = 100,
            QuarantinedAtUtc = atUtc,
            ContentHash = QuarantineIndex.HashTree(world.Fs, quarantined),
            Ecosystem = "dotnet",
            Kind = "package",
            Name = name,
            Version = "1.0.0",
        };

        await world.Index().SaveAsync(entry);
        return entry;
    }

    private static JsonElement LastJson(FakeApplyConsole console) =>
        JsonDocument.Parse(console.Out[^1]).RootElement.Clone();

    // ---- list -----------------------------------------------------------------------------------------

    [Fact]
    public async Task List_reports_entries_and_never_prompts_or_writes()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["list", "--json"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, console.ReadLineCalls);
        Assert.Empty(world.Fs.Writes);
        Assert.Empty(world.Log.Appended);

        JsonElement json = LastJson(console);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal(entry.EntryId, json.GetProperty("entries")[0].GetProperty("id").GetString());
        Assert.True(json.GetProperty("entries")[0].GetProperty("contentPresent").GetBoolean());
    }

    [Fact]
    public async Task List_marks_an_entry_whose_content_is_gone_instead_of_failing()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        world.Fs.DeleteFile(entry.QuarantinePath, recursive: true);

        var console = new FakeApplyConsole(interactive: false);
        int exit = world.Command(console).Run(["list", "--json"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(LastJson(console).GetProperty("entries")[0].GetProperty("contentPresent").GetBoolean());
    }

    // ---- restore --------------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_without_yes_cannot_ask_and_touches_nothing()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["restore", entry.EntryId]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Equal(0, console.ReadLineCalls);
        Assert.Empty(world.Fs.Writes);
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public async Task Restore_requires_a_typed_yes()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        var console = new FakeApplyConsole(interactive: true).Answer("sure");

        int exit = world.Command(console).Run(["restore", entry.EntryId]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, console.ReadLineCalls);
        Assert.Contains("Declined", console.AllOutput, StringComparison.Ordinal);
        Assert.False(world.Fs.DirectoryExists(entry.OriginalPath));
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public async Task Restore_moves_the_item_back_byte_identically_and_audits_it()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        string before = QuarantineIndex.HashTree(world.Fs, entry.QuarantinePath);
        var console = new FakeApplyConsole(interactive: true).Answer("yes");

        int exit = world.Command(console).Run(["restore", entry.EntryId]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(world.Fs.DirectoryExists(entry.OriginalPath));
        Assert.Equal(before, QuarantineIndex.HashTree(world.Fs, entry.OriginalPath));
        Assert.Empty(await world.Index().ListAsync());

        string audit = string.Join('\n', world.Log.Lines(AuditLog.Key));
        Assert.Contains(AuditRecord.RestoreAction, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_refuses_an_occupied_destination_with_a_safety_abort()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        world.Fs.AddDirectory(entry.OriginalPath);

        var console = new FakeApplyConsole(interactive: false);
        int exit = world.Command(console).Run(["restore", entry.EntryId, "--yes", "--ci"]);

        Assert.Equal(ExitCodes.SafetyAbort, exit);
        Assert.True(world.Fs.DirectoryExists(entry.OriginalPath));
        Assert.True(world.Fs.DirectoryExists(entry.QuarantinePath));
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public async Task Restore_refuses_altered_quarantined_content_with_a_safety_abort()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        world.Fs.AddFile($@"{entry.QuarantinePath}\newtonsoft.json.nuspec", "tampered");

        var console = new FakeApplyConsole(interactive: false);
        int exit = world.Command(console).Run(["restore", entry.EntryId, "--yes", "--ci"]);

        Assert.Equal(ExitCodes.SafetyAbort, exit);
        Assert.False(world.Fs.DirectoryExists(entry.OriginalPath));
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public async Task Ci_never_prompts_even_when_stdin_and_stdout_are_a_terminal()
    {
        World world = BuildWorld();
        QuarantineEntry entry = await QuarantineOneAsync(world);
        var console = new FakeApplyConsole(interactive: true);

        int restoreExit = world.Command(console).Run(["restore", entry.EntryId, "--ci"]);
        int pruneExit = world.Command(console).Run(["prune", "--ci"]);

        Assert.Equal(ExitCodes.UsageError, restoreExit);
        Assert.Equal(ExitCodes.UsageError, pruneExit);
        Assert.Equal(0, console.ReadLineCalls);
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public void Restore_of_an_unknown_id_is_a_runtime_error_without_a_prompt()
    {
        World world = BuildWorld();
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["restore", "does-not-exist", "--yes", "--ci"]);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Equal(0, console.ReadLineCalls);
        Assert.Contains("no quarantine entry", console.AllErrors, StringComparison.OrdinalIgnoreCase);
    }

    // ---- prune ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Prune_dry_run_reports_but_deletes_nothing()
    {
        World world = BuildWorld();
        QuarantineEntry old = await AddEntryAsync(world, "old.pkg", Now.AddDays(-40));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["prune", "--older-than", "30d", "--dry-run", "--ci"]);

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement json = LastJson(console);
        Assert.Equal(1, json.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(0, json.GetProperty("prunedCount").GetInt32());
        Assert.True(world.Fs.DirectoryExists(old.QuarantinePath));
        Assert.Single(await world.Index().ListAsync());
        Assert.Empty(world.Log.Appended);
    }

    [Fact]
    public async Task Prune_without_yes_cannot_ask_and_touches_nothing()
    {
        World world = BuildWorld();
        QuarantineEntry old = await AddEntryAsync(world, "old.pkg", Now.AddDays(-40));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["prune", "--older-than", "30d"]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Equal(0, console.ReadLineCalls);
        Assert.True(world.Fs.DirectoryExists(old.QuarantinePath));
        Assert.Single(await world.Index().ListAsync());
        Assert.Empty(world.Log.Appended);
    }

    [Fact]
    public async Task Prune_requires_a_typed_yes()
    {
        World world = BuildWorld();
        QuarantineEntry old = await AddEntryAsync(world, "old.pkg", Now.AddDays(-40));
        var console = new FakeApplyConsole(interactive: true).Answer("no");

        int exit = world.Command(console).Run(["prune", "--older-than", "30d"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, console.ReadLineCalls);
        Assert.Contains("Declined", console.AllOutput, StringComparison.Ordinal);
        Assert.True(world.Fs.DirectoryExists(old.QuarantinePath));
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public async Task Prune_deletes_only_old_entries_and_audits_each_discard()
    {
        World world = BuildWorld();
        QuarantineEntry old = await AddEntryAsync(world, "old.pkg", Now.AddDays(-40));
        QuarantineEntry recent = await AddEntryAsync(world, "recent.pkg", Now.AddHours(-1));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["prune", "--older-than", "30d", "--yes", "--ci"]);

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement json = LastJson(console);
        Assert.Equal(1, json.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(1, json.GetProperty("prunedCount").GetInt32());
        Assert.False(world.Fs.DirectoryExists(old.QuarantinePath));
        Assert.True(world.Fs.DirectoryExists(recent.QuarantinePath));

        QuarantineEntry remaining = Assert.Single(await world.Index().ListAsync());
        Assert.Equal(recent.EntryId, remaining.EntryId);

        string audit = string.Join('\n', world.Log.Lines(AuditLog.Key));
        Assert.Contains(AuditRecord.PruneAction, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_without_an_age_uses_thirty_days()
    {
        World world = BuildWorld();
        QuarantineEntry old = await AddEntryAsync(world, "old.pkg", Now.AddDays(-40));
        QuarantineEntry newer = await AddEntryAsync(world, "newer.pkg", Now.AddDays(-20));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["prune", "--yes", "--ci"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(world.Fs.DirectoryExists(old.QuarantinePath));
        Assert.True(world.Fs.DirectoryExists(newer.QuarantinePath));
    }

    // ---- parsing --------------------------------------------------------------------------------------

    [Fact]
    public async Task Unknown_subcommands_options_and_arguments_are_usage_errors()
    {
        string[][] cases =
        [
            [],
            ["bogus"],
            ["list", "extra"],
            ["restore"],
            ["restore", "a", "b"],
            ["prune", "an-id"],
            ["prune", "--older-than", "soon"],
            ["list", "--older-than", "1d"],
            ["restore", "an-id", "--dry-run"],
            ["restore", "an-id", "--nope"],
        ];

        foreach (string[] args in cases)
        {
            World world = BuildWorld();
            var console = new FakeApplyConsole(interactive: false);

            Assert.Equal(ExitCodes.UsageError, world.Command(console).Run(args));
            Assert.Empty(world.Fs.Writes);
            Assert.Empty(world.Log.Appended);
            Assert.Empty(await world.Index().ListAsync());
        }
    }
}

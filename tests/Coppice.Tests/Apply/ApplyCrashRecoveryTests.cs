using System.Globalization;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using GatewayEngine = Coppice.Core.Gateway.Gateway;
using Policy = Coppice.Core.Domain.Policy;
using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Apply;

/// <summary>
/// T-034, FR-16/S-16: a kill -9 at each point in the mutation leaves no partial item, and <c>--resume</c>
/// finishes the plan.
/// <para>
/// These are the tests the acceptance box is really about. An in-process crash is injected at the three
/// boundaries that matter — after the intent is durable but before the rename, after the rename but before
/// the index entry, and after the index entry but before the outcome — and each is resumed with a FRESH
/// runner (a new journal reader, as a restarted process would build). The invariant asserted afterwards is
/// the one 06 promises: the item is wholly at its source or wholly in quarantine with an index entry whose
/// recorded hash matches the bytes now sitting there, and every intent in the journal has an outcome.
/// </para>
/// <para>
/// A companion test, <c>ApplyRealProcessKillTests</c>, does the same against a real child process
/// killed with <c>Kill(entireProcessTree: true)</c>, because an in-process throw is a model of a kill, not a
/// kill. Both have to hold for the box to be ticked.
/// </para>
/// </summary>
public sealed class ApplyCrashRecoveryTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\Users\dev\.nuget\packages";
    private const string Package = "newtonsoft.json";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public required FakeFileSystem Fs { get; init; }

        public required FakeStateStore State { get; init; }

        public required FakeAppendLog Log { get; init; }

        public required QuarantineIndex Index { get; init; }

        public required Plan Plan { get; init; }

        public required string ItemPath { get; init; }

        public string JournalKey => ApplyJournal.KeyFor(Plan.PlanId);

        public string QuarantinePath => Core.Gateway.QuarantinePath.For(Root, "packages", ItemPath);

        /// <summary>A fresh runner over the same fake disk and log, as a restarted process would build.</summary>
        public ApplyRunner NewRunner()
        {
            var clock = new FakeClock(Stamp);
            var gateway = new GatewayEngine(Fs, Win, Index, clock, new FakeProcessRunner());
            return new ApplyRunner(gateway, Fs, Log, Index, clock);
        }

        public IReadOnlyList<BoundStep> Bind() =>
            PlanBinder.Bind(Plan, Snapshot, [new BindingRoot("nuget-packages", Root, true, false, "*/*")], Win, strict: false);

        public required Snapshot Snapshot { get; init; }
    }

    private static World BuildWorld()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        string itemPath = $@"{Root}\{Package}\1.0.0";
        fs.AddDirectoryOfSize(itemPath, 4096, lastWrite: Stamp);

        Item item = new(
            "dotnet", "package", Package, "1.0.0", "nuget-packages", itemPath, 4096, Risk.Safe,
            Facts.From([new KeyValuePair<string, string>("lastWriteUtc", Stamp.ToString("O", CultureInfo.InvariantCulture))]));

        Snapshot snapshot = Snapshot.Create("snap_1", Stamp, [item], [], ["dotnet"]);
        Plan plan = PlanBuilder.Build(
            snapshot.SnapshotId,
            Policy.Default,
            [new PlanStep(item.ItemId, Risk.Safe, "unused", RemovalAction.PathDelete(item.Path), item.Size)]).Plan!;

        var state = new FakeStateStore();

        return new World
        {
            Fs = fs,
            State = state,
            Log = new FakeAppendLog(),
            Index = new QuarantineIndex(state),
            Plan = plan,
            Snapshot = snapshot,
            ItemPath = itemPath,
        };
    }

    [Fact]
    public async Task A_kill_after_the_intent_before_the_rename_resumes_to_a_complete_quarantine()
    {
        World world = BuildWorld();
        world.Fs.OnBeforeMove = (_, _) => throw new SimulatedCrash("before rename");

        await Assert.ThrowsAsync<SimulatedCrash>(() => world.NewRunner().RunAsync(world.Plan, world.Bind(), new ApplyOptions(false, false, true)));

        // The intent is durable; nothing moved.
        Assert.True(world.Fs.DirectoryExists(world.ItemPath));
        Assert.False(world.Fs.DirectoryExists(world.QuarantinePath));

        world.Fs.OnBeforeMove = null;
        ApplySummary resumed = await world.NewRunner().RunAsync(world.Plan, world.Bind(), new ApplyOptions(false, true, true));

        Assert.Equal(ApplyStatus.Completed, resumed.Status);
        await AssertNoPartialItem(world);
    }

    [Fact]
    public async Task A_kill_after_the_rename_before_the_index_entry_resumes_and_rebuilds_the_entry()
    {
        World world = BuildWorld();
        world.Fs.OnMove = (_, _) => throw new SimulatedCrash("after rename, before index");

        await Assert.ThrowsAsync<SimulatedCrash>(() => world.NewRunner().RunAsync(world.Plan, world.Bind(), new ApplyOptions(false, false, true)));

        // Moved, but the index never recorded it.
        Assert.False(world.Fs.DirectoryExists(world.ItemPath));
        Assert.True(world.Fs.DirectoryExists(world.QuarantinePath));
        Assert.Empty(await world.Index.ListAsync());

        world.Fs.OnMove = null;
        ApplySummary resumed = await world.NewRunner().RunAsync(world.Plan, world.Bind(), new ApplyOptions(false, true, true));

        Assert.Equal(ApplyStatus.Completed, resumed.Status);
        Assert.Contains("recovered", resumed.Steps.Select(s => s.Result), StringComparer.Ordinal);
        await AssertNoPartialItem(world);
    }

    [Fact]
    public async Task A_kill_after_the_index_entry_before_the_outcome_resumes_to_a_recorded_quarantine()
    {
        World world = BuildWorld();
        world.Log.CrashOnPhase = "outcome";
        world.Log.CrashAfterLines = 1;

        await Assert.ThrowsAsync<SimulatedCrash>(() => world.NewRunner().RunAsync(world.Plan, world.Bind(), new ApplyOptions(false, false, true)));

        // Moved AND indexed, but the outcome line tore.
        Assert.False(world.Fs.DirectoryExists(world.ItemPath));
        Assert.True(world.Fs.DirectoryExists(world.QuarantinePath));
        Assert.Single(await world.Index.ListAsync());

        world.Log.CrashOnPhase = null;
        world.Log.CrashAfterLines = null;
        ApplySummary resumed = await world.NewRunner().RunAsync(world.Plan, world.Bind(), new ApplyOptions(false, true, true));

        Assert.Equal(ApplyStatus.Completed, resumed.Status);
        await AssertNoPartialItem(world);
    }

    /// <summary>
    /// The single invariant a resume has to restore: the item is wholly on one side, the quarantine side is
    /// backed by an index entry whose hash matches the bytes actually there, and the journal is closed with
    /// an outcome for every intent.
    /// </summary>
    private static async Task AssertNoPartialItem(World world)
    {
        bool atSource = world.Fs.DirectoryExists(world.ItemPath);
        bool inQuarantine = world.Fs.DirectoryExists(world.QuarantinePath);

        Assert.NotEqual(atSource, inQuarantine);

        if (inQuarantine)
        {
            IReadOnlyList<QuarantineEntry> entries = await world.Index.ListAsync();
            QuarantineEntry entry = Assert.Single(entries, e => string.Equals(e.QuarantinePath, world.QuarantinePath, StringComparison.Ordinal));

            Assert.Equal(QuarantineIndex.HashTree(world.Fs, world.QuarantinePath), entry.ContentHash);
            Assert.Equal(world.ItemPath, entry.OriginalPath);
        }

        string? text = await world.Log.ReadAllTextAsync(world.JournalKey);
        JournalState journal = text is null ? JournalState.Missing : ApplyJournal.Parse(text);

        Assert.False(journal.Corrupt);
        Assert.True(journal.Ended);
        Assert.Equal(journal.Intents.Keys, journal.Outcomes.Keys);
    }
}

using System.Globalization;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;
using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Apply;

/// <summary>
/// T-034: the <c>apply</c> command's decisions — the confirmation gate, the plan-file rules, and the exit
/// codes from 07.
/// <para>
/// The gate matters more than the run. A non-TTY apply without <c>--yes</c> must refuse BEFORE it reads a
/// plan, resolves a root, or builds a gateway, because the whole guarantee is that a scripted invocation
/// cannot reach a mutation by accident. These tests assert that nothing at all was touched, not merely that
/// the exit code was 2.
/// </para>
/// </summary>
public sealed class ApplyCommandTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";
    private const string PlanPath = @"C:\plans\plan.json";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public required FakeFileSystem Fs { get; init; }

        public required FakeStateStore State { get; init; }

        public required FakeAppendLog Log { get; init; }

        public required FakeEnvironment Env { get; init; }

        public required FakeProcessRunner Runner { get; init; }

        public required Plan Plan { get; init; }

        public required Snapshot Snapshot { get; init; }

        public ApplyCommand Command(FakeApplyConsole console) =>
            new(Fs, Runner, Env, State, Log, console, new FakeClock(Stamp));

        public string PlanJson => DomainJson.Serialize(Plan, indented: true);

        public QuarantineIndex Index() => new(State);

        public FakeFileSystem WritePlanFile(string json) => Fs.AddFile(PlanPath, json);
    }

    private static World BuildWorld(params (string Name, bool Locked)[] packages)
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        var items = new List<Item>();
        foreach ((string name, bool locked) in packages)
        {
            string path = $@"{Root}\{name}\1.0.0";
            fs.AddDirectoryOfSize(path, 4096, locked ? LockState.Locked : LockState.Unlocked, Stamp);
            items.Add(new Item(
                "dotnet", "package", name, "1.0.0", "nuget-packages", path, 4096, Risk.Safe,
                Facts.From([new KeyValuePair<string, string>("lastWriteUtc", Stamp.ToString("O", CultureInfo.InvariantCulture))])));
        }

        Snapshot snapshot = Snapshot.Create("snap_1", Stamp, items, [], ["dotnet"]);
        Plan plan = PlanBuilder.Build(
            snapshot.SnapshotId,
            Policy.Default,
            items.Select(i => new PlanStep(i.ItemId, Risk.Safe, "unused", RemovalAction.PathDelete(i.Path), i.Size))).Plan!;

        var state = new FakeStateStore();
        var clock = new FakeClock(Stamp);
        new SnapshotStore(state, clock).SaveAsync(snapshot).GetAwaiter().GetResult();

        var env = new FakeEnvironment(Win).With("NUGET_PACKAGES", Root);

        // The snapshot save is setup, not a command write; the gate tests assert the COMMAND touched
        // nothing, so the setup's own state write is cleared first.
        state.Writes.Clear();

        return new World
        {
            Fs = fs,
            State = state,
            Log = new FakeAppendLog(),
            Env = env,
            Runner = new FakeProcessRunner(),
            Plan = plan,
            Snapshot = snapshot,
        };
    }

    // ---- the confirmation gate ----

    [Fact]
    public void A_non_terminal_apply_without_yes_refuses_before_touching_anything()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        world.WritePlanFile(world.PlanJson);
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", PlanPath]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Empty(world.Fs.Writes);
        Assert.Empty(world.State.Writes);
        Assert.Empty(world.Log.Appended);
        Assert.Equal(0, console.ReadLineCalls);
        Assert.Contains("--yes", console.AllErrors, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_terminal_apply_with_yes_but_no_plan_is_still_a_usage_error()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--yes"]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Empty(world.Fs.Writes);
        Assert.Empty(world.Log.Appended);
    }

    [Fact]
    public void An_interactive_decline_moves_nothing()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        world.WritePlanFile(world.PlanJson);
        var console = new FakeApplyConsole(interactive: true).Answer("no");

        int exit = world.Command(console).Run(["--plan", PlanPath]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, console.ReadLineCalls);
        Assert.True(world.Fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
        Assert.DoesNotContain(world.Fs.Writes, w => w.StartsWith("Move", StringComparison.Ordinal));
        Assert.Empty(world.Log.Appended);
    }

    [Fact]
    public async Task An_interactive_yes_quarantines_the_item()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        world.WritePlanFile(world.PlanJson);
        var console = new FakeApplyConsole(interactive: true).Answer("yes");

        int exit = world.Command(console).Run(["--plan", PlanPath]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(world.Fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
        Assert.Contains("steps ok", console.AllOutput, StringComparison.Ordinal);
        Assert.Contains("Harvested", console.AllOutput, StringComparison.Ordinal);

        IReadOnlyList<QuarantineEntry> entries = await world.Index().ListAsync();
        Assert.Single(entries);
    }

    [Fact]
    public void Yes_skips_the_prompt_entirely()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        world.WritePlanFile(world.PlanJson);
        var console = new FakeApplyConsole(interactive: true);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, console.ReadLineCalls);
    }

    // ---- exit codes ----

    [Fact]
    public void A_partial_apply_exits_four()
    {
        World world = BuildWorld(("newtonsoft.json", false), ("locked.pkg", true));
        world.WritePlanFile(world.PlanJson);
        var console = new FakeApplyConsole(interactive: true);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes"]);

        Assert.Equal(ExitCodes.PartialApply, exit);
        Assert.Contains("failed", console.AllOutput, StringComparison.Ordinal);

        // The unlocked one still moved; the locked one did not.
        Assert.False(world.Fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
        Assert.True(world.Fs.DirectoryExists($@"{Root}\locked.pkg\1.0.0"));
    }

    [Fact]
    public void A_tampered_plan_file_is_a_safety_abort()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        Plan tampered = world.Plan with { Checksum = "deadbeefdeadbeef" };
        world.WritePlanFile(DomainJson.Serialize(tampered, indented: true));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes"]);

        Assert.Equal(ExitCodes.SafetyAbort, exit);
        Assert.Contains("checksum", console.AllErrors, StringComparison.Ordinal);
        Assert.True(world.Fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
    }

    [Fact]
    public void A_missing_plan_file_is_a_usage_error()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", @"C:\plans\nope.json", "--yes"]);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public void An_unknown_flag_is_a_usage_error()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        var console = new FakeApplyConsole(interactive: true);

        int exit = world.Command(console).Run(["--bogus"]);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task Applying_a_stored_plan_twice_refuses_the_second_as_already_applied()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        await new PlanStore(world.State).SaveAsync(world.Plan);
        var first = new FakeApplyConsole(interactive: true);

        Assert.Equal(ExitCodes.Success, world.Command(first).Run(["--yes"]));

        var second = new FakeApplyConsole(interactive: true);
        int exit = world.Command(second).Run(["--yes"]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("single-use", second.AllErrors, StringComparison.Ordinal);
    }
}

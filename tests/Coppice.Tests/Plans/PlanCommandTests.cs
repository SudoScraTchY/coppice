using System.Globalization;
using System.Text.Json;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Tests.Plans;

/// <summary>
/// T-044: <c>coppice plan</c> — the middle of scan → plan → apply (FR-11, 07-cli-tui).
/// <para>
/// The command is small, so the tests concentrate on the two things that make it safe to chain: the plan it
/// stores is the one <c>apply</c> loads without a file, and the file it writes under <c>--out</c> is one
/// <c>apply --plan</c> accepts. The rest is policy selection and the honest empty case.
/// </para>
/// </summary>
public sealed class PlanCommandTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";
    private const string PlanPath = @"C:\plans\plan.json";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public required FakeFileSystem Fs { get; init; }

        public required FakeStateStore State { get; init; }

        public required FakeEnvironment Env { get; init; }

        public required FakeAppendLog Log { get; init; }

        public required Snapshot Snapshot { get; init; }

        public required FakePlanFileWriter Writer { get; init; }

        public required FakeClock Clock { get; init; }

        public FakeApplyConsole PlanConsole { get; } = new(interactive: false);

        public PlanCommand Plan(Policy? basePolicy = null) =>
            new(State, Writer, PlanConsole, Clock, basePolicy: basePolicy);

        public ApplyCommand Apply(FakeApplyConsole console) =>
            new(Fs, new FakeProcessRunner(), Env, State, Log, console, Clock);

        public QuarantineIndex Index() => new(State);
    }

    private static World BuildWorld(params string[] versions)
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        var items = new List<Item>();
        foreach (string version in versions)
        {
            string path = $@"{Root}\legacy.pkg\{version}";
            fs.AddDirectoryOfSize(path, 4096, lastWrite: Stamp);
            items.Add(new Item(
                "dotnet",
                "package",
                "legacy.pkg",
                version,
                "nuget-packages",
                path,
                4096,
                Risk.Safe,
                Facts.From(
                [
                    new KeyValuePair<string, string>("usage", "Unreferenced"),
                    new KeyValuePair<string, string>(
                        "lastWriteUtc",
                        Stamp.ToString("O", CultureInfo.InvariantCulture)),
                ])));
        }

        Snapshot snapshot = Snapshot.Create("snap_1", Stamp, items, [], ["dotnet"]);
        var state = new FakeStateStore();
        var clock = new FakeClock(Stamp);
        new SnapshotStore(state, clock).SaveAsync(snapshot).GetAwaiter().GetResult();
        state.Writes.Clear();

        return new World
        {
            Fs = fs,
            State = state,
            Env = new FakeEnvironment(Win).With("NUGET_PACKAGES", Root),
            Log = new FakeAppendLog(),
            Snapshot = snapshot,
            Writer = new FakePlanFileWriter(),
            Clock = clock,
        };
    }

    private static JsonElement LastJson(FakeApplyConsole console) =>
        JsonDocument.Parse(console.Out[^1]).RootElement.Clone();

    private static string StoredPlanId(FakeStateStore state)
    {
        string key = Assert.Single(state.ListKeys("plans/"));
        return key["plans/".Length..^".json".Length];
    }

    // ---- build and store ------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_is_built_from_the_latest_snapshot_and_stored()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");

        int exit = world.Plan().Run(["--json"]);

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement json = LastJson(world.PlanConsole);
        Assert.Equal(1, json.GetProperty("stepCount").GetInt32());
        Assert.True(json.GetProperty("stored").GetBoolean());
        Assert.Single(world.State.ListKeys("plans/"));

        PlanLoadResult loaded = await new PlanStore(world.State).LoadAsync(StoredPlanId(world.State));
        Assert.True(loaded.IsSuccess, loaded.Message);

        // Default keeps the newest two; the one step is the oldest version, and the reason names the rule.
        Item oldest = world.Snapshot.Items.Single(item => item.Version == "1.0.0");
        PlanStep step = Assert.Single(loaded.Plan!.Steps);
        Assert.Equal(oldest.ItemId, step.ItemId);
        Assert.Contains("newest", step.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(world.Writer.Writes);
    }

    [Fact]
    public void Keep_latest_overrides_the_preset_keep_count()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");

        int exit = world.Plan().Run(["--keep-latest", "0", "--json"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(3, LastJson(world.PlanConsole).GetProperty("stepCount").GetInt32());
    }

    [Fact]
    public async Task A_configured_policy_applies_when_no_policy_flag_is_given()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");
        Policy configured = Policy.Default with { KeepLatestN = 1 };

        Assert.Equal(ExitCodes.Success, world.Plan(configured).Run(["--json"]));

        Plan stored = (await new PlanStore(world.State).LoadAsync(StoredPlanId(world.State))).Plan!;
        Assert.Equal(1, stored.Policy.KeepLatestN);
        Assert.Equal(2, stored.Steps.Count);
    }

    [Fact]
    public void An_explicit_policy_flag_overrides_the_configured_policy()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");
        Policy configured = Policy.Conservative with { KeepLatestN = 1 };

        // The config says keep 1; `--policy default` must replace the whole preset, so keep 2 wins
        // and only the oldest of three is proposed.
        Assert.Equal(ExitCodes.Success, world.Plan(configured).Run(["--policy", "default", "--json"]));
        Assert.Equal(1, LastJson(world.PlanConsole).GetProperty("stepCount").GetInt32());
    }

    [Fact]
    public void Configured_exclusions_survive_an_explicit_policy_flag()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");
        Policy configured = Policy.Default with { KeepLatestN = 1, Exclusions = ["legacy.pkg"] };

        Assert.Equal(ExitCodes.Success, world.Plan(configured).Run(["--policy", "aggressive", "--json"]));
        Assert.Equal(0, LastJson(world.PlanConsole).GetProperty("stepCount").GetInt32());
    }

    [Fact]
    public void Nothing_to_propose_is_success_and_writes_nothing()
    {
        World world = BuildWorld("1.0.0", "2.0.0");

        int exit = world.Plan().Run(["--out", PlanPath, "--json"]);

        Assert.Equal(ExitCodes.Success, exit);

        JsonElement json = LastJson(world.PlanConsole);
        Assert.Equal(0, json.GetProperty("stepCount").GetInt32());
        Assert.False(json.GetProperty("stored").GetBoolean());
        Assert.Contains("Nothing to propose", json.GetProperty("message").GetString()!, StringComparison.Ordinal);

        Assert.Empty(world.Writer.Writes);
        Assert.Empty(world.State.ListKeys("plans/"));
    }

    [Fact]
    public async Task The_cli_and_the_shared_preview_build_the_same_plan()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");

        Assert.Equal(ExitCodes.Success, world.Plan().Run(["--json"]));

        Plan stored = (await new PlanStore(world.State).LoadAsync(StoredPlanId(world.State))).Plan!;
        PlanPreview preview = PlanPreviewService.Preview(world.Snapshot, Policy.Default);

        // The TUI passes PlanPreviewService.Preview as its factory; this equality is the CLI half of
        // "one snapshot, one policy, one plan" (T-044 acceptance).
        Assert.Equal(preview.Plan, stored);
    }

    // ---- --out and the chain to apply ------------------------------------------------------------------

    [Fact]
    public async Task Out_writes_a_valid_plan_that_apply_executes()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");

        int exit = world.Plan().Run(["--out", PlanPath, "--json"]);
        Assert.Equal(ExitCodes.Success, exit);

        (string path, string json) = Assert.Single(world.Writer.Writes);
        Assert.Equal(PlanPath, path);
        Assert.True(PlanStore.Parse(json, "test").IsSuccess);

        world.Fs.AddFile(PlanPath, json);
        var console = new FakeApplyConsole(interactive: false);

        int applyExit = world.Apply(console).Run(["--plan", PlanPath, "--yes", "--ci"]);

        Assert.Equal(ExitCodes.Success, applyExit);
        Assert.False(world.Fs.DirectoryExists($@"{Root}\legacy.pkg\1.0.0"));
        Assert.True(world.Fs.DirectoryExists($@"{Root}\legacy.pkg\3.0.0"));
        QuarantineEntry entry = Assert.Single(await world.Index().ListAsync());
        Assert.Equal($@"{Root}\legacy.pkg\1.0.0", entry.OriginalPath);
    }

    [Fact]
    public async Task A_stored_plan_can_be_applied_without_a_plan_file()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");

        Assert.Equal(ExitCodes.Success, world.Plan().Run([]));

        var console = new FakeApplyConsole(interactive: true).Answer("yes");

        int exit = world.Apply(console).Run([]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, console.ReadLineCalls);
        Assert.False(world.Fs.DirectoryExists($@"{Root}\legacy.pkg\1.0.0"));
        Assert.Single(await world.Index().ListAsync());
    }

    [Fact]
    public void An_out_write_failure_is_reported_but_the_plan_remains_stored()
    {
        World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");
        world.Writer.Failure = new IOException("disk full");

        int exit = world.Plan().Run(["--out", PlanPath]);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Single(world.State.ListKeys("plans/"));
        Assert.Contains("stored", world.PlanConsole.AllErrors, StringComparison.OrdinalIgnoreCase);
    }

    // ---- refusal and output contracts ------------------------------------------------------------------

    [Fact]
    public void Invalid_flags_are_usage_errors_and_plan_nothing()
    {
        string[][] cases =
        [
            ["--policy", "wild"],
            ["--keep-latest", "-1"],
            ["--keep-latest", "nope"],
            ["--bogus"],
            ["--out"],
        ];

        foreach (string[] args in cases)
        {
            World world = BuildWorld("1.0.0", "2.0.0", "3.0.0");

            Assert.Equal(ExitCodes.UsageError, world.Plan().Run(args));
            Assert.Empty(world.State.ListKeys("plans/"));
            Assert.Empty(world.Writer.Writes);
        }
    }

    [Fact]
    public void With_no_snapshot_plan_is_a_runtime_error()
    {
        var console = new FakeApplyConsole(interactive: false);
        var command = new PlanCommand(new FakeStateStore(), new FakePlanFileWriter(), console, new FakeClock(Stamp));

        int exit = command.Run([]);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Contains("scan", console.AllErrors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ci_output_is_stable_across_fresh_runs_and_ansi_free()
    {
        World first = BuildWorld("1.0.0", "2.0.0", "3.0.0");
        World second = BuildWorld("1.0.0", "2.0.0", "3.0.0");

        Assert.Equal(ExitCodes.Success, first.Plan().Run(["--ci"]));
        Assert.Equal(ExitCodes.Success, second.Plan().Run(["--ci"]));

        string firstOutput = string.Join('\n', first.PlanConsole.Out);
        string secondOutput = string.Join('\n', second.PlanConsole.Out);

        Assert.Equal(firstOutput, secondOutput);
        Assert.DoesNotContain('\u001b', firstOutput);
        Assert.StartsWith("{", firstOutput, StringComparison.Ordinal);
    }
}

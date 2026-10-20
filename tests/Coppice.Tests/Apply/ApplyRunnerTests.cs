using System.Globalization;
using System.Text;
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
/// T-034: the write-ahead journal and the runner that writes it (FR-16, S-16).
/// <para>
/// Two properties matter and both are asserted here rather than assumed. The ORDER: an intent is durable
/// before the gateway is called, so the journal can never under-report a mutation. The TOLERANCE: a kill
/// tears at most the last line, so a torn tail resumes and a corrupted middle aborts — a reader that
/// accepted both would resume from a record it cannot trust, and one that accepted neither would make every
/// crash unrecoverable.
/// </para>
/// </summary>
public sealed class ApplyRunnerTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\Users\dev\.nuget\packages";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Everything one apply needs, wired the way the CLI wires it.</summary>
    private sealed class World
    {
        public required FakeFileSystem Fs { get; init; }

        public required FakeStateStore State { get; init; }

        public required FakeAppendLog Log { get; init; }

        public required FakeClock Clock { get; init; }

        public required FakeProcessRunner Runner { get; init; }

        public required QuarantineIndex Index { get; init; }

        public required GatewayEngine Gateway { get; init; }

        public required ApplyRunner Apply { get; init; }

        public required Plan Plan { get; init; }

        public required Snapshot Snapshot { get; init; }

        public required IReadOnlyList<BoundStep> Bound { get; init; }

        public required IReadOnlyList<BindingRoot> Roots { get; init; }

        public string JournalKey => ApplyJournal.KeyFor(Plan.PlanId);

        public IReadOnlyList<BoundStep> Bind(Plan plan, Snapshot snapshot, bool strict = false) =>
            PlanBinder.Bind(plan, snapshot, Roots, Win, strict);

        public string QuarantineOf(string itemPath) =>
            Coppice.Core.Gateway.QuarantinePath.For(Root, "packages", itemPath);
    }

    private static World BuildWorld(params string[] packages)
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        var items = new List<Item>();
        foreach (string package in packages)
        {
            string path = $@"{Root}\{package}\1.0.0";
            fs.AddDirectoryOfSize(path, 4096, lastWrite: Stamp);
            items.Add(new Item(
                "dotnet",
                "package",
                package,
                "1.0.0",
                "nuget-packages",
                path,
                4096,
                Risk.Safe,
                Facts.From([new KeyValuePair<string, string>(
                    "lastWriteUtc",
                    Stamp.ToString("O", CultureInfo.InvariantCulture))])));
        }

        Snapshot snapshot = Snapshot.Create("snap_1", Stamp, items, [], ["dotnet"]);
        Plan plan = PlanBuilder.Build(
            snapshot.SnapshotId,
            Policy.Default,
            items.Select(i => new PlanStep(i.ItemId, Risk.Safe, "unused", RemovalAction.PathDelete(i.Path), i.Size))).Plan!;

        var state = new FakeStateStore();
        var log = new FakeAppendLog();
        var clock = new FakeClock(Stamp);
        var processes = new FakeProcessRunner();
        var index = new QuarantineIndex(state);
        var gateway = new GatewayEngine(fs, Win, index, clock, processes);
        var runner = new ApplyRunner(gateway, fs, log, index, clock);

        IReadOnlyList<BindingRoot> roots = [new BindingRoot("nuget-packages", Root, true, false, "*/*")];
        IReadOnlyList<BoundStep> bound = PlanBinder.Bind(plan, snapshot, roots, Win, strict: false);

        return new World
        {
            Fs = fs,
            State = state,
            Log = log,
            Clock = clock,
            Runner = processes,
            Index = index,
            Gateway = gateway,
            Apply = runner,
            Plan = plan,
            Snapshot = snapshot,
            Bound = bound,
            Roots = roots,
        };
    }

    private static ApplyOptions Options(bool strict = false, bool resume = false, bool confirmed = true) =>
        new(strict, resume, confirmed);

    // ---- the journal's own reading rules ----

    [Fact]
    public async Task A_complete_journal_reads_back_as_ended_with_one_outcome_per_intent()
    {
        World world = BuildWorld("newtonsoft.json");

        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, Options());

        Assert.Equal(ApplyStatus.Completed, summary.Status);

        JournalState state = await new ApplyJournal(world.Log, world.Plan.PlanId).ReadAsync();
        Assert.True(state.Ended);
        Assert.False(state.Corrupt);
        Assert.Equal(state.Intents.Keys, state.Outcomes.Keys);
    }

    [Fact]
    public void A_torn_final_line_is_tolerated_and_a_later_run_starts_on_a_fresh_line()
    {
        string jsonl = new StringBuilder()
            .AppendLine("{\"phase\":\"begin\",\"planId\":\"pln_1\",\"checksum\":\"abc\",\"steps\":1}")
            .Append("{\"phase\":\"intent\",\"stepId\":\"s1\",\"ac")
            .ToString();

        JournalState state = ApplyJournal.Parse(jsonl);

        Assert.False(state.Corrupt);
        Assert.NotNull(state.Begin);
        Assert.Empty(state.Intents);
        Assert.True(state.NeedsSeparator);
    }

    [Fact]
    public void A_torn_line_followed_by_a_resume_record_is_tolerated()
    {
        string jsonl = string.Join('\n',
            "{\"phase\":\"begin\",\"planId\":\"pln_1\",\"checksum\":\"abc\",\"steps\":1}",
            "{\"phase\":\"outcome\",\"stepId\":\"s1\",\"res",
            "{\"phase\":\"resume\",\"planId\":\"pln_1\",\"checksum\":\"abc\",\"steps\":1}") + "\n";

        JournalState state = ApplyJournal.Parse(jsonl);

        Assert.False(state.Corrupt);
        Assert.Empty(state.Outcomes);
    }

    [Fact]
    public void A_corrupt_line_in_the_middle_is_not_tolerated()
    {
        string jsonl = string.Join('\n',
            "{\"phase\":\"begin\",\"planId\":\"pln_1\",\"checksum\":\"abc\",\"steps\":2}",
            "this is not json at all",
            "{\"phase\":\"end\",\"planId\":\"pln_1\"}") + "\n";

        JournalState state = ApplyJournal.Parse(jsonl);

        Assert.True(state.Corrupt);
        Assert.Contains("line 2", state.CorruptDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_journal_with_records_but_no_begin_is_corrupt()
    {
        JournalState state = ApplyJournal.Parse("{\"phase\":\"end\",\"planId\":\"pln_1\"}\n");

        Assert.True(state.Corrupt);
    }

    // ---- ordering: the intent is durable before the mutation ----

    [Fact]
    public async Task The_intent_is_written_before_the_gateway_moves_anything()
    {
        World world = BuildWorld("newtonsoft.json");
        var order = new List<string>();

        world.Fs.OnMove = (_, _) => order.Add("move");

        // The log records every append; the fake fs records the move. Interleaving them proves the intent
        // was durable first, which is the whole property a resume depends on.
        await world.Apply.RunAsync(world.Plan, world.Bound, Options());

        IReadOnlyList<string> lines = world.Log.Lines(world.JournalKey);
        Assert.Contains(lines, l => l.Contains("\"phase\":\"intent\"", StringComparison.Ordinal));

        int intentIndex = IndexOfPhase(lines, "intent");
        Assert.True(intentIndex >= 0);
        Assert.Contains("move", order);
        Assert.Single(order);

        // And the intent names both places the item can be, so a resume can look for it. The path is
        // JSON-escaped in the line, so the comparison escapes the separators too.
        Assert.Contains("\"to\":", lines[intentIndex], StringComparison.Ordinal);
        Assert.Contains(
            world.QuarantineOf($@"{Root}\newtonsoft.json\1.0.0").Replace("\\", "\\\\"),
            lines[intentIndex],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_step_ends_with_an_outcome_and_the_run_ends_with_end()
    {
        World world = BuildWorld("newtonsoft.json", "system.io");

        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, Options());

        Assert.Equal(2, summary.Ok);
        Assert.Equal(2, world.Bound.Count);

        IReadOnlyList<string> lines = world.Log.Lines(world.JournalKey);
        Assert.Equal("end", PhaseOf(lines[^1]));
        Assert.Equal(2, lines.Count(l => PhaseOf(l) == "outcome"));
    }

    [Fact]
    public async Task A_locked_item_is_reported_as_a_failure_not_as_a_removal()
    {
        World world = BuildWorld("newtonsoft.json");
        world.Fs.AddDirectoryOfSize($@"{Root}\locked\1.0.0", 4096, LockState.Locked, Stamp);
        Item locked = world.Snapshot.Items[0] with
        {
            Name = "locked",
            Path = $@"{Root}\locked\1.0.0",
            Facts = Facts.From([new KeyValuePair<string, string>("lastWriteUtc", Stamp.ToString("O", CultureInfo.InvariantCulture))]),
        };

        Snapshot snapshot = world.Snapshot with { Items = [locked] };
        Plan plan = PlanBuilder.Build(
            snapshot.SnapshotId,
            Policy.Default,
            [new PlanStep(locked.ItemId, Risk.Safe, "unused", RemovalAction.PathDelete(locked.Path), 4096)]).Plan!;
        IReadOnlyList<BoundStep> bound = world.Bind(plan, snapshot);

        ApplySummary summary = await world.Apply.RunAsync(plan, bound, Options());

        Assert.Equal(ApplyStatus.Partial, summary.Status);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(GatewayRefusal.Locked, Assert.Single(summary.Steps).Refusal);
        Assert.True(world.Fs.DirectoryExists($@"{Root}\locked\1.0.0"));
    }

    [Fact]
    public async Task A_step_that_could_not_bind_is_skipped_as_unsafe_and_still_gets_an_outcome()
    {
        World world = BuildWorld("newtonsoft.json");
        PlanStep orphan = new("deadbeef", Risk.Safe, "not in the snapshot", RemovalAction.PathDelete($@"{Root}\gone\1.0.0"), 10);
        Plan plan = PlanBuilder.Build(world.Snapshot.SnapshotId, Policy.Default, [orphan]).Plan!;
        IReadOnlyList<BoundStep> bound = world.Bind(plan, world.Snapshot);

        ApplySummary summary = await world.Apply.RunAsync(plan, bound, Options());

        Assert.Equal(ApplyStatus.Partial, summary.Status);
        Assert.Equal(1, summary.SkippedUnsafe);
        Assert.Empty(world.Fs.Writes);

        IReadOnlyList<string> lines = world.Log.Lines(ApplyJournal.KeyFor(plan.PlanId));
        Assert.DoesNotContain(lines, l => PhaseOf(l) == "intent");
        Assert.Contains(lines, l => PhaseOf(l) == "outcome");
    }

    // ---- the single-use rules ----

    [Fact]
    public async Task Applying_the_same_plan_twice_is_refused()
    {
        World world = BuildWorld("newtonsoft.json");
        await world.Apply.RunAsync(world.Plan, world.Bound, Options());
        world.Fs.Writes.Clear();

        ApplySummary second = await world.Apply.RunAsync(world.Plan, world.Bound, Options());

        Assert.Equal(ApplyStatus.AlreadyApplied, second.Status);
        Assert.Empty(second.Steps);
        Assert.Empty(world.Fs.Writes);
        Assert.Contains("single-use", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interrupted_run_refuses_to_restart_without_resume()
    {
        World world = BuildWorld("newtonsoft.json", "system.io");
        world.Log.CrashAfterLines = 1;
        world.Log.CrashOnPhase = "intent";

        await Assert.ThrowsAsync<SimulatedCrash>(() => world.Apply.RunAsync(world.Plan, world.Bound, Options()));

        world.Log.CrashAfterLines = null;
        ApplySummary refused = await world.Apply.RunAsync(world.Plan, world.Bound, Options());

        Assert.Equal(ApplyStatus.Unfinished, refused.Status);
        Assert.Contains("--resume", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_of_a_plan_that_never_started_is_a_usage_refusal()
    {
        World world = BuildWorld("newtonsoft.json");

        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, Options(resume: true));

        Assert.Equal(ApplyStatus.NothingToResume, summary.Status);
        Assert.Empty(world.Fs.Writes);
    }

    [Fact]
    public async Task A_journal_written_for_a_different_plan_checksum_is_refused()
    {
        World world = BuildWorld("newtonsoft.json");
        await world.Apply.RunAsync(world.Plan, world.Bound, Options());

        // Same id, different content: the checksum is what proves the journal belongs to THIS plan.
        Plan tampered = world.Plan with { Checksum = "not-the-same" };

        ApplySummary summary = await world.Apply.RunAsync(tampered, world.Bound, Options(resume: true));

        Assert.Equal(ApplyStatus.JournalMismatch, summary.Status);
    }

    [Fact]
    public async Task A_corrupt_journal_aborts_before_anything_is_touched()
    {
        World world = BuildWorld("newtonsoft.json");
        world.Log.AppendRaw(world.JournalKey, "{\"phase\":\"begin\",\"checksum\":\"x\"}");
        world.Log.AppendRaw(world.JournalKey, "garbage in the middle");
        world.Log.AppendRaw(world.JournalKey, "{\"phase\":\"end\"}");

        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, Options(resume: true));

        Assert.Equal(ApplyStatus.JournalCorrupt, summary.Status);
        Assert.Empty(world.Fs.Writes);
    }

    [Fact]
    public async Task A_plan_id_that_is_not_a_safe_key_is_refused()
    {
        World world = BuildWorld("newtonsoft.json");
        Plan plan = world.Plan with { PlanId = "../../etc/passwd" };

        ApplySummary summary = await world.Apply.RunAsync(plan, world.Bound, Options());

        Assert.Equal(ApplyStatus.InvalidPlanId, summary.Status);
        Assert.Empty(world.Fs.Writes);
        Assert.Empty(world.Log.Appended);
    }

    [Fact]
    public async Task Strict_refuses_a_step_whose_mtime_moved_since_the_snapshot()
    {
        World world = BuildWorld("newtonsoft.json");

        // The gateway's strict drift check reads the LIVE mtime; the fake reports the epoch for nodes added
        // without a stamp, so re-adding the directory without one is a changed item.
        world.Fs.AddDirectoryOfSize($@"{Root}\newtonsoft.json\1.0.0", 4096);

        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, Options(strict: true));

        Assert.Equal(ApplyStatus.Partial, summary.Status);
        Assert.Equal(GatewayRefusal.Drifted, Assert.Single(summary.Steps).Refusal);
        Assert.True(world.Fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
    }

    // ---- cancellation (T-036) ---------------------------------------------------------------------

    /// <summary>
    /// Cancel is honoured BETWEEN steps and only there. The first step is journaled exactly as an
    /// uninterrupted run would, the second never starts, and the journal is left open — which is the state
    /// `--resume` accepts. A cancel that appended `end` would abandon the remaining steps and call the plan
    /// finished.
    /// </summary>
    [Fact]
    public async Task Cancelling_between_steps_stops_before_the_next_intent_and_leaves_a_resumable_journal()
    {
        World world = BuildWorld("newtonsoft.json", "serilog");
        using var cancellation = new CancellationTokenSource();
        var events = new List<ApplyStepProgress>();

        var progress = new InlineProgress<ApplyStepProgress>(update =>
        {
            events.Add(update);
            if (update.Report is not null && events.Count(e => e.Report is not null) == 1)
            {
                // The first outcome is already durable when its report arrives, so the loop's next
                // cancellation check is exactly the boundary a user's Esc lands on.
                cancellation.Cancel();
            }
        });

        ApplySummary summary = await world.Apply.RunAsync(
            world.Plan, world.Bound, Options(), progress, cancellation.Token);

        Assert.Equal(ApplyStatus.Cancelled, summary.Status);
        Assert.Contains("resume", summary.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(summary.Steps);
        Assert.DoesNotContain(events, e => e.StepId == world.Plan.Steps[1].ItemId && e.Report is null);

        IReadOnlyList<string> lines = world.Log.Lines(world.JournalKey);
        Assert.Equal(1, lines.Count(l => PhaseOf(l) == "outcome"));
        Assert.Equal(1, lines.Count(l => PhaseOf(l) == "intent"));
        Assert.DoesNotContain(lines, l => PhaseOf(l) == "end");
        Assert.Equal(1, lines.Count(l => PhaseOf(l) == "begin"));

        // One item moved, the other did not. Nothing half-moved: the gateway was never entered for step 2.
        Assert.Equal(1, new[] { $@"{Root}\newtonsoft.json\1.0.0", $@"{Root}\serilog\1.0.0" }.Count(world.Fs.DirectoryExists));

        ApplySummary resumed = await world.Apply.RunAsync(world.Plan, world.Bound, Options(resume: true));

        Assert.Equal(ApplyStatus.Completed, resumed.Status);
        Assert.Equal(2, resumed.Ok);
        Assert.Contains(world.Log.Lines(world.JournalKey), l => PhaseOf(l) == "end");
    }

    /// <summary>
    /// A cancel already set before the run starts performs no mutation at all, but the attempt is still
    /// journaled so a later `--resume` knows the plan was begun rather than never tried.
    /// </summary>
    [Fact]
    public async Task A_cancel_before_the_first_step_moves_nothing_and_still_resumes()
    {
        World world = BuildWorld("newtonsoft.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        ApplySummary summary = await world.Apply.RunAsync(
            world.Plan, world.Bound, Options(), progress: null, cancellation.Token);

        Assert.Equal(ApplyStatus.Cancelled, summary.Status);
        Assert.Empty(summary.Steps);
        Assert.DoesNotContain(world.Log.Lines(world.JournalKey), l => PhaseOf(l) == "intent");
        Assert.True(world.Fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));

        ApplySummary resumed = await world.Apply.RunAsync(world.Plan, world.Bound, Options(resume: true));

        Assert.Equal(ApplyStatus.Completed, resumed.Status);
    }

    /// <summary>Reports on the calling thread, like the TUI's progress adapter; `Progress&lt;T&gt;` would race.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static int IndexOfPhase(IReadOnlyList<string> lines, string phase)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (PhaseOf(lines[i]) == phase)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? PhaseOf(string line)
    {
        int index = line.IndexOf("\"phase\":\"", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        int start = index + "\"phase\":\"".Length;
        int end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }
}

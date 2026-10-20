using System.Globalization;
using System.Text;
using Coppice.Cli;
using Coppice.Core.Audit;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;
using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Cli;

/// <summary>
/// T-037: the exit-code contract (FR-21, 07-cli-tui) and CI-mode output.
/// <para>
/// Five codes, five meanings, and a test per code that reaches the mapping through the command surface
/// rather than restating the constant. The codes are what a pipeline branches on: 0 did it, 1 broke,
/// 2 asked wrong, 3 refused for safety, 4 did some of it. A contract test that only checked the enum
/// would pass while every command returned 0.
/// </para>
/// <para>
/// `<c>--ci</c>` is FR-21's non-interactive, machine-readable mode. The CLI has never emitted color or
/// spinners, so the observable promise is: it never prompts, and a run's output is byte-stable — pinned
/// here against a golden so "stable" is not merely "the same within one process".
/// </para>
/// </summary>
public sealed class ExitCodeContractTests
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

        public required Plan Plan { get; init; }

        public ApplyCommand Command(FakeApplyConsole console) =>
            new(Fs, new FakeProcessRunner(), Env, State, Log, console, new FakeClock(Stamp));

        public string PlanJson => DomainJson.Serialize(Plan, indented: true);
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
        new SnapshotStore(state, new FakeClock(Stamp)).SaveAsync(snapshot).GetAwaiter().GetResult();
        state.Writes.Clear();

        return new World
        {
            Fs = fs,
            State = state,
            Log = new FakeAppendLog(),
            Env = new FakeEnvironment(Win).With("NUGET_PACKAGES", Root),
            Plan = plan,
        };
    }

    [Fact]
    public void Exit_zero_is_a_completed_apply()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        world.Fs.AddFile(PlanPath, world.PlanJson);
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes", "--ci"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, console.ReadLineCalls);
    }

    [Fact]
    public async Task Exit_one_is_a_runtime_error_from_a_ledger_that_does_not_verify()
    {
        // A chain whose middle line was edited. This is the runtime-error code: the tool ran and the
        // evidence is broken — not a usage mistake and not a refusal that stopped a mutation.
        var log = new FakeAppendLog();
        var audit = new AuditLog(log, new FakeClock(Stamp));
        for (int i = 1; i <= 3; i++)
        {
            await audit.AppendAsync(new AuditRecord
            {
                Action = AuditRecord.QuarantineAction,
                From = $@"C:\cache\pkg{i}\1.0.0",
                To = $@"C:\cache\.coppice\quarantine\pkg{i}",
                Bytes = 100u * (ulong)i,
                Result = AuditRecord.OkResult,
            });
        }

        IReadOnlyList<string> lines = log.Lines(AuditLog.Key);
        var edited = new FakeAppendLog();
        edited.AppendRaw(AuditLog.Key, lines[0]);
        edited.AppendRaw(AuditLog.Key, lines[1].Replace("\"result\":\"ok\"", "\"result\":\"failed\"", StringComparison.Ordinal));
        edited.AppendRaw(AuditLog.Key, lines[2]);

        var console = new FakeApplyConsole(interactive: false);
        int exit = new AuditCommand(new FakeFileSystem(Win), new FakeStateStore(), edited, console).Run(["verify"]);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Contains("does not verify", console.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_two_is_a_usage_error()
    {
        // Two shapes of "asked wrong": an unknown command, and --ci apply without the consent flags it
        // requires. The second must not touch anything, which is asserted in ApplyCommandTests.
        Assert.Equal(ExitCodes.UsageError, CoppiceApp.Run(["bogus"]));

        World world = BuildWorld(("newtonsoft.json", false));
        world.Fs.AddFile(PlanPath, world.PlanJson);
        var console = new FakeApplyConsole(interactive: true);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--ci"]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Equal(0, console.ReadLineCalls);
    }

    [Fact]
    public void Exit_three_is_a_safety_abort()
    {
        World world = BuildWorld(("newtonsoft.json", false));
        Plan tampered = world.Plan with { Checksum = "deadbeefdeadbeef" };
        world.Fs.AddFile(PlanPath, DomainJson.Serialize(tampered, indented: true));
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes", "--ci"]);

        Assert.Equal(ExitCodes.SafetyAbort, exit);
    }

    [Fact]
    public void Exit_four_is_a_partial_apply()
    {
        World world = BuildWorld(("newtonsoft.json", false), ("locked.pkg", true));
        world.Fs.AddFile(PlanPath, world.PlanJson);
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes", "--ci"]);

        Assert.Equal(ExitCodes.PartialApply, exit);
    }

    [Fact]
    public void Ci_output_is_the_same_across_two_fresh_runs_and_matches_the_golden()
    {
        string first = CiOutput(BuildWorld(("newtonsoft.json", false)));
        string second = CiOutput(BuildWorld(("newtonsoft.json", false)));

        Assert.Equal(first, second);
        Assert.DoesNotContain('\u001b', first);
        Assert.StartsWith("{", first, StringComparison.Ordinal);

        AssertGolden("apply-ci.json", first);
    }

    private static string CiOutput(World world)
    {
        world.Fs.AddFile(PlanPath, world.PlanJson);
        var console = new FakeApplyConsole(interactive: false);

        int exit = world.Command(console).Run(["--plan", PlanPath, "--yes", "--ci"]);
        Assert.Equal(ExitCodes.Success, exit);

        return string.Join('\n', console.Out);
    }

    /// <summary>
    /// Same discipline as the scan goldens: auto-create on first run and FAIL, so a golden cannot quietly
    /// become a snapshot of whatever the code happened to emit.
    /// </summary>
    private static void AssertGolden(string name, string actual)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "golden", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail(
                $"No golden file at {name}. It has been recorded. Read it, confirm the output is correct, "
                + "and commit it — a golden file nobody reviewed is worse than none.");
        }

        string expected = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

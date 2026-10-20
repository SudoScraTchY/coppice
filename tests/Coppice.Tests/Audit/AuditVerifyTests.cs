using System.Globalization;
using Coppice.Core.Apply;
using Coppice.Core.Audit;
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

namespace Coppice.Tests.Audit;

/// <summary>
/// T-035's acceptance criteria, end to end: a run that stays inside the gates leaves ZERO unlogged
/// mutations, a deliberately tampered line is detected, and the log, the quarantine index and the
/// filesystem are forced to agree. Everything runs through the real apply path — gateway, journal,
/// restore, prune — with the audit wired exactly as the CLI wires it.
/// </summary>
public sealed class AuditVerifyTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\Users\dev\.nuget\packages";
    private const string GoRoot = @"C:\go";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public required FakeFileSystem Fs { get; init; }

        public required FakeAppendLog Log { get; init; }

        public required QuarantineIndex Index { get; init; }

        public required AuditLog Audit { get; init; }

        public required ApplyRunner Apply { get; init; }

        public required QuarantineRestore Restore { get; init; }

        public required Plan Plan { get; init; }

        public required Snapshot Snapshot { get; init; }

        public required IReadOnlyList<BoundStep> Bound { get; init; }

        public IReadOnlyList<string> AuditAppends => Log.Lines(AuditLog.Key);
    }

    private static Item PackageItem(FakeFileSystem fs, string package)
    {
        string path = $@"{Root}\{package}\1.0.0";
        fs.AddDirectoryOfSize(path, 4096, lastWrite: Stamp);
        return new Item(
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
                Stamp.ToString("O", CultureInfo.InvariantCulture))]));
    }

    private static World BuildWorld()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectory(GoRoot);
        fs.AddDirectoryOfSize($@"{GoRoot}\modcache", 4096, lastWrite: Stamp);

        var items = new List<Item>
        {
            PackageItem(fs, "newtonsoft.json"),
            PackageItem(fs, "nunit"),
            PackageItem(fs, "xunit.core"),
            new Item(
                "go",
                "modcache",
                "go",
                "1.21.0",
                "go-mod-cache",
                $@"{GoRoot}\modcache",
                4096,
                Risk.Safe,
                Facts.From([new KeyValuePair<string, string>(
                    "lastWriteUtc",
                    Stamp.ToString("O", CultureInfo.InvariantCulture))])),
        };

        Snapshot snapshot = Snapshot.Create("snap_1", Stamp, items, [], ["dotnet", "go"]);
        Plan plan = PlanBuilder.Build(
            snapshot.SnapshotId,
            Policy.Default,
            items.Select(item => new PlanStep(
                item.ItemId,
                Risk.Safe,
                "unused",
                item.LocationId == "go-mod-cache" ? RemovalAction.Native("go clean -modcache") : RemovalAction.PathDelete(item.Path),
                item.Size))).Plan!;

        var state = new FakeStateStore();
        var log = new FakeAppendLog();
        var clock = new FakeClock(Stamp);
        var runner = new FakeProcessRunner();
        runner.Respond("go", "cleaned module cache", exitCode: 0);
        var index = new QuarantineIndex(state);
        var audit = new AuditLog(log, clock);
        var gateway = new GatewayEngine(fs, Win, index, clock, runner, audit);

        IReadOnlyList<BindingRoot> roots =
        [
            new BindingRoot("nuget-packages", Root, true, false, "*/*"),
            new BindingRoot("go-mod-cache", GoRoot, true, false, null),
        ];
        IReadOnlyList<BoundStep> bound = PlanBinder.Bind(plan, snapshot, roots, Win, strict: false);

        return new World
        {
            Fs = fs,
            Log = log,
            Index = index,
            Audit = audit,
            Apply = new ApplyRunner(gateway, fs, log, index, clock),
            Restore = new QuarantineRestore(fs, Win, index, audit),
            Plan = plan,
            Snapshot = snapshot,
            Bound = bound,
        };
    }

    private static AuditVerifier Verifier(World world) => new(world.Log, world.Index, world.Fs);

    private static string Findings(AuditVerifyReport report) => string.Join("; ", report.Findings.Select(f => f.Message));

    [Fact]
    public async Task An_apply_that_stays_inside_the_gates_leaves_zero_unlogged_mutations()
    {
        World world = BuildWorld();

        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, new ApplyOptions(Strict: false, Resume: false, Confirmed: true));
        Assert.True(
            summary.Status == ApplyStatus.Completed,
            string.Join("; ", summary.Steps.Select(step => $"{step.StepId}: {step.Result} ({step.Refusal}) {step.Message}")));

        // Three quarantines and one native run: four mutations, four lines. Refusals journal their
        // outcome but never audit — nothing moved.
        Assert.Equal(4, world.AuditAppends.Count);

        AuditVerifyReport report = await Verifier(world).VerifyAsync();
        Assert.True(report.Ok, Findings(report));
        Assert.Empty(report.Findings);

        // A restore is a mutation too, and the log follows it.
        QuarantineEntry entry = (await world.Index.ListAsync())[0];
        RestoreResult restored = await world.Restore.RestoreAsync(entry.EntryId);
        Assert.True(restored.Restored, restored.Message);
        Assert.Equal(5, world.AuditAppends.Count);

        report = await Verifier(world).VerifyAsync();
        Assert.True(report.Ok, Findings(report));
        Assert.Empty(report.Findings);

        // Prune discards the rest — permanently, and on the record.
        IReadOnlyList<QuarantineEntry> pruned = await world.Restore.PruneAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, pruned.Count);
        Assert.Equal(7, world.AuditAppends.Count);

        report = await Verifier(world).VerifyAsync();
        Assert.True(report.Ok, Findings(report));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task A_native_step_without_confirmation_is_refused_and_audits_nothing()
    {
        World world = BuildWorld();

        // The CLI's confirmation gate sits upstream of the runner (ApplyCommand.Confirm), so with
        // Confirmed=false the path steps still run — but the gateway's own asymmetry gate (06) refuses
        // the native step: a tool-owned mutation with no `--yes` behind it does not execute, and does
        // not audit, because nothing was mutated.
        ApplySummary summary = await world.Apply.RunAsync(world.Plan, world.Bound, new ApplyOptions(Strict: false, Resume: false, Confirmed: false));
        Assert.Equal(ApplyStatus.Partial, summary.Status);
        Assert.Contains(summary.Steps, step => step.Refusal == GatewayRefusal.NeedsConfirmation);

        Assert.Equal(3, world.AuditAppends.Count);
        Assert.All(world.AuditAppends, line => Assert.Contains("\"action\":\"quarantine\"", line, StringComparison.Ordinal));

        AuditVerifyReport report = await Verifier(world).VerifyAsync();
        Assert.True(report.Ok, Findings(report));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task A_tampered_line_is_detected()
    {
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

        // Rebuild the log with the middle line's content edited under its original hash — exactly what
        // an editor of a written line produces.
        IReadOnlyList<string> lines = log.Lines(AuditLog.Key);
        var edited = new FakeAppendLog();
        edited.AppendRaw(AuditLog.Key, lines[0]);
        edited.AppendRaw(AuditLog.Key, lines[1].Replace("\"result\":\"ok\"", "\"result\":\"failed\"", StringComparison.Ordinal));
        edited.AppendRaw(AuditLog.Key, lines[2]);

        AuditVerifyReport report = await new AuditVerifier(edited, new QuarantineIndex(new FakeStateStore()), new FakeFileSystem(Win)).VerifyAsync();

        Assert.False(report.Ok);
        Assert.Contains(report.Findings, finding => finding.Code == "chain-hash");
    }

    [Fact]
    public async Task A_dropped_line_is_detected()
    {
        var log = new FakeAppendLog();
        var audit = new AuditLog(log, new FakeClock(Stamp));
        for (int i = 1; i <= 3; i++)
        {
            await audit.AppendAsync(new AuditRecord
            {
                Action = AuditRecord.QuarantineAction,
                From = $@"C:\cache\pkg{i}\1.0.0",
                To = $@"C:\cache\.coppice\quarantine\pkg{i}",
                Result = AuditRecord.OkResult,
            });
        }

        IReadOnlyList<string> lines = log.Lines(AuditLog.Key);
        var edited = new FakeAppendLog();
        edited.AppendRaw(AuditLog.Key, lines[0]);
        edited.AppendRaw(AuditLog.Key, lines[2]);

        AuditVerifyReport report = await new AuditVerifier(edited, new QuarantineIndex(new FakeStateStore()), new FakeFileSystem(Win)).VerifyAsync();

        Assert.False(report.Ok);
        Assert.Contains(report.Findings, finding => finding.Code is "chain-link" or "chain-seq");
    }

    [Fact]
    public async Task An_index_entry_without_its_line_is_an_unlogged_mutation()
    {
        var state = new FakeStateStore();
        var index = new QuarantineIndex(state);
        await index.SaveAsync(new QuarantineEntry
        {
            EntryId = "pln_x_1",
            OriginalPath = @"C:\cache\x\1.0.0",
            QuarantinePath = @"C:\cache\.coppice\quarantine\nuget-packages\x-1.0.0",
            Root = @"C:\cache",
            LocationId = "nuget-packages",
            ItemId = "nuget:x/1.0.0",
            SizeBytes = 4096,
            QuarantinedAtUtc = Stamp,
            ContentHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Ecosystem = "dotnet",
            Kind = "package",
            Name = "x",
            Version = "1.0.0",
        });

        // An empty log: the move happened (the index says so) and nothing recorded it.
        AuditVerifyReport report = await new AuditVerifier(new FakeAppendLog(), index, new FakeFileSystem(Win)).VerifyAsync();

        Assert.False(report.Ok);
        Assert.Contains(report.Findings, finding => finding.Code == "unlogged-mutation");
    }

    [Fact]
    public async Task Quarantined_content_without_an_index_entry_is_flagged_as_an_orphan()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(@"C:\cache\.coppice\quarantine\x");
        var log = new FakeAppendLog();
        await new AuditLog(log, new FakeClock(Stamp)).AppendAsync(new AuditRecord
        {
            Action = AuditRecord.QuarantineAction,
            From = @"C:\cache\x",
            To = @"C:\cache\.coppice\quarantine\x",
            Result = AuditRecord.OkResult,
        });

        // No index entry: the failed-index-write case the gateway warns about in its result.
        AuditVerifyReport report = await new AuditVerifier(log, new QuarantineIndex(new FakeStateStore()), fs).VerifyAsync();

        // A warning, not a fatal: the content is accounted for and restorable by hand.
        Assert.True(report.Ok, Findings(report));
        Assert.Contains(report.Findings, finding => finding.Code == "orphaned-content");
    }

    [Fact]
    public async Task Quarantined_content_that_vanished_without_a_restore_or_prune_is_flagged()
    {
        var log = new FakeAppendLog();
        await new AuditLog(log, new FakeClock(Stamp)).AppendAsync(new AuditRecord
        {
            Action = AuditRecord.QuarantineAction,
            From = @"C:\cache\x",
            To = @"C:\cache\.coppice\quarantine\x",
            Result = AuditRecord.OkResult,
        });

        AuditVerifyReport report = await new AuditVerifier(log, new QuarantineIndex(new FakeStateStore()), new FakeFileSystem(Win)).VerifyAsync();

        Assert.True(report.Ok, Findings(report));
        Assert.Contains(report.Findings, finding => finding.Code == "missing-content");
    }

    [Fact]
    public async Task A_torn_tail_is_reported_but_does_not_fail_verification()
    {
        var log = new FakeAppendLog();
        var audit = new AuditLog(log, new FakeClock(Stamp));
        await audit.AppendAsync(new AuditRecord
        {
            Action = AuditRecord.QuarantineAction,
            From = @"C:\cache\x",
            To = @"C:\cache\.coppice\quarantine\x",
            Result = AuditRecord.OkResult,
        });
        log.AppendRaw(AuditLog.Key, "{\"ts\":\"2026-10-01T12:00:00Z\",\"acti");

        AuditVerifyReport report = await new AuditVerifier(log, new QuarantineIndex(new FakeStateStore()), new FakeFileSystem(Win)).VerifyAsync();

        Assert.True(report.Ok, Findings(report));
        Assert.True(report.TornTail);
        Assert.Contains(report.Findings, finding => finding.Code == "torn-tail");
    }

    [Fact]
    public async Task An_unreadable_line_mid_log_fails_verification()
    {
        var log = new FakeAppendLog();
        var audit = new AuditLog(log, new FakeClock(Stamp));
        await audit.AppendAsync(new AuditRecord
        {
            Action = AuditRecord.QuarantineAction,
            From = @"C:\cache\x",
            To = @"C:\cache\.coppice\quarantine\x",
            Result = AuditRecord.OkResult,
        });
        await audit.AppendAsync(new AuditRecord
        {
            Action = AuditRecord.RestoreAction,
            From = @"C:\cache\.coppice\quarantine\x",
            To = @"C:\cache\x",
            Result = AuditRecord.OkResult,
        });

        // Corrupt the middle with records on both sides and no separator: not a torn write.
        IReadOnlyList<string> lines = log.Lines(AuditLog.Key);
        var edited = new FakeAppendLog();
        edited.AppendRaw(AuditLog.Key, lines[0]);
        edited.AppendRaw(AuditLog.Key, "clearly not a record at all");
        edited.AppendRaw(AuditLog.Key, lines[1]);

        AuditVerifyReport report = await new AuditVerifier(edited, new QuarantineIndex(new FakeStateStore()), new FakeFileSystem(Win)).VerifyAsync();

        Assert.False(report.Ok);
        Assert.Contains(report.Findings, finding => finding.Code == "unreadable-line");
    }
}

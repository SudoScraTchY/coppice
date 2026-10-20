using System.Text;
using Coppice.Cli;
using Coppice.Cli.Tui;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Tests.Support;

/// <summary>One fixed machine for the TUI tests: proposable versions, one Review item, one referenced item.</summary>
internal static class TuiFixtures
{
    public const string Root = @"C:\cache";
    public static readonly DateTimeOffset Stamp = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public static Item Package(string name, string version, ulong size, Risk risk = Risk.Safe, Usage usage = Usage.Unreferenced)
    {
        string path = $@"{Root}\{name}\{version}";
        return new Item(
            "dotnet",
            "package",
            name,
            version,
            "nuget-packages",
            path,
            size,
            risk,
            Facts.From([
                new KeyValuePair<string, string>("usage", usage.ToString()),
                new KeyValuePair<string, string>("lastWriteUtc", Stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ]));
    }

    public static Snapshot Snapshot() => Coppice.Core.Snapshots.Snapshot.Create(
        "snap_20261021T120000Z",
        Stamp,
        [
            Package("newtonsoft.json", "1.0.0", 100),
            Package("newtonsoft.json", "2.0.0", 200),
            Package("newtonsoft.json", "3.0.0", 300),
            Package("serilog", "1.0.0", 400),
            Package("serilog", "2.0.0", 500),
            Package("serilog", "3.0.0", 600),
            Package("dotnet-tool", "1.0.0", 50, Risk.Review),
            Package("polly", "1.0.0", 10, Risk.Safe, Usage.Referenced),
        ],
        [],
        ["dotnet"]);

    public static DoctorReport Roots() => new(
        OperatingSystemKind.Windows,
        [
            new RootReport(
                "nuget-packages",
                new ResolvedRoot($@"{Root}", $@"{Root}", RootRole.Active, ResolvedVia.Tool, "dotnet nuget locals", RootValidity.Ok, "user"),
                null),
            new RootReport(
                "dotnet-tools",
                ResolvedRoot.Reject(@"C:\Program Files\dotnet\tools", RootValidity.Denied, "denylist: Program Files"),
                "this path is on the safety denylist; it will never be cleaned."),
        ],
        [],
        ["dotnet"]);

    public static QuarantineEntry Quarantined() => new()
    {
        EntryId = "q0001",
        OriginalPath = $@"{Root}\polly\0.9.0",
        QuarantinePath = $@"{Root}\.coppice\quarantine\cache\polly\0.9.0",
        Root = Root,
        LocationId = "nuget-packages",
        ItemId = "item0001",
        SizeBytes = 64,
        QuarantinedAtUtc = Stamp,
        ContentHash = "abc123",
        Ecosystem = "dotnet",
        Kind = "package",
        Name = "polly",
        Version = "0.9.0",
    };

    public static TuiData Data() => new(
        Roots(),
        Snapshot(),
        [new TuiProblem("error", "roots", "denied", "dotnet-tools: denied", @"C:\Program Files\dotnet\tools", "on the denylist")],
        [Quarantined()],
        @"C:\Users\dev",
        @"C:\Users\dev\AppData\Roaming\coppice\config.toml");

    public static Plan PreviewPlan() => PlanPreviewService.Preview(Snapshot(), Policy.Default).Plan!;

    public static IReadOnlyList<BoundStep> Bound(Plan plan) =>
        PlanBinder.Bind(
            plan,
            Snapshot(),
            [new BindingRoot("nuget-packages", Root, Cleanable: true, InstallerOwned: false, AllowlistPattern: "*/*")],
            OperatingSystemKind.Windows,
            strict: false);

    public static TuiState State() => new(Data(), PlanPreviewService.Preview);

    public static ConsoleKeyInfo Tab(bool shift = false) => new('\t', ConsoleKey.Tab, shift, false, false);

    public static ConsoleKeyInfo Letter(char letter) =>
        new(letter, (ConsoleKey)char.ToUpperInvariant(letter), false, false, false);

    public static ConsoleKeyInfo Enter() => new('\r', ConsoleKey.Enter, false, false, false);

    public static ConsoleKeyInfo Escape() => new('\u001b', ConsoleKey.Escape, false, false, false);

    public static ConsoleKeyInfo Down() => new('\0', ConsoleKey.DownArrow, false, false, false);

    public static ConsoleKeyInfo Up() => new('\0', ConsoleKey.UpArrow, false, false, false);

    public static ConsoleKeyInfo Space() => new(' ', ConsoleKey.Spacebar, false, false, false);

    public static ConsoleKeyInfo CtrlC() => new('\u0003', ConsoleKey.C, false, false, true);
}

/// <summary>A terminal with no console behind it: keys come from a script, screens go to strings.</summary>
internal sealed class ScriptedTuiTerminal : ITuiTerminal
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();

    public ScriptedTuiTerminal(bool interactive)
    {
        IsInteractive = interactive;
    }

    public bool IsInteractive { get; }

    public StringBuilder Plain { get; } = new();

    public List<TuiFrame> Frames { get; } = [];

    public bool KeyAvailable => _keys.Count > 0;

    public void Enqueue(params ConsoleKeyInfo[] keys)
    {
        foreach (ConsoleKeyInfo key in keys)
        {
            _keys.Enqueue(key);
        }
    }

    public ConsoleKeyInfo ReadKey() =>
        _keys.Count > 0 ? _keys.Dequeue() : throw new InvalidOperationException("The scripted terminal ran out of keys.");

    public void Render(TuiFrame frame) => Frames.Add(frame);

    public void WritePlain(string text) => Plain.Append(text);
}

/// <summary>The backend with nothing behind it. Apply reports progress the way the real adapter does.</summary>
internal sealed class FakeTuiBackend : ITuiBackend
{
    public FakeTuiBackend(TuiData data)
    {
        Data = data;
    }

    public TuiData Data { get; set; }

    public int LoadCount { get; private set; }

    public int ScanCount { get; private set; }

    public int PrepareCount { get; private set; }

    public int ApplyCount { get; private set; }

    public int RestoreCount { get; private set; }

    public string? RestoredEntry { get; private set; }

    public ApplySummary Summary { get; set; } = new(ApplyStatus.Completed, string.Empty, []);

    public string? PrepareError { get; set; }

    public Task<TuiData> LoadAsync()
    {
        LoadCount++;
        return Task.FromResult(Data);
    }

    public Task<TuiData> ScanAsync()
    {
        ScanCount++;
        return Task.FromResult(Data);
    }

    public Task<ApplyPreparation> PrepareApplyAsync(Plan plan)
    {
        PrepareCount++;
        return Task.FromResult(PrepareError is null
            ? new ApplyPreparation(plan, TuiFixtures.Bound(plan), null)
            : new ApplyPreparation(null, [], PrepareError));
    }

    public Task<ApplySummary> ApplyAsync(
        Plan plan,
        IReadOnlyList<BoundStep> bound,
        IProgress<ApplyStepProgress> progress,
        CancellationToken cancellationToken)
    {
        ApplyCount++;

        var reports = new List<ApplyStepReport>();
        for (int i = 0; i < bound.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(new ApplySummary(ApplyStatus.Cancelled, "cancelled", reports));
            }

            progress.Report(new ApplyStepProgress(i, bound.Count, bound[i].PlanStep.ItemId, null));
            var report = new ApplyStepReport(
                bound[i].PlanStep.ItemId, "quarantined", GatewayRefusal.None, "moved", bound[i].PlanStep.ExpectedBytes, ApplyStepClass.Ok);
            reports.Add(report);
            progress.Report(new ApplyStepProgress(i, bound.Count, bound[i].PlanStep.ItemId, report));
        }

        return Task.FromResult(Summary with { Steps = reports });
    }

    public Task<RestoreResult> RestoreAsync(string entryId)
    {
        RestoreCount++;
        RestoredEntry = entryId;
        return Task.FromResult(RestoreResult.Ok(@"C:\cache\restored", "restored"));
    }
}

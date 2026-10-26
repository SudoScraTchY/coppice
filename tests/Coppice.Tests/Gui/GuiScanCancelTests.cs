using System.Diagnostics;
using Coppice.Application;
using Coppice.Gui.Services;
using Coppice.Gui.ViewModels;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Coppice.Tests.Gui;

/// <summary>
/// T-039 acceptance box 2 / T-050: a scan cancel is measured, not asserted from the shape of the code.
/// <para>
/// The slow phase is the scan's own enumeration, which honors the cancellation token between items.
/// The doctor runs on a separate fast fake so this number is the scan-enumeration bound: T-056 now
/// passes the token through <c>DoctorService</c>, but the doctor's own latency is measured there,
/// not here.
/// </para>
/// </summary>
public sealed class GuiScanCancelTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;
    private const string Cache = "/home/dev/.nuget/packages";
    private const string Projects = "/home/dev/code";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public GuiScanCancelTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Cancelling_through_ScanCancelCommand_stops_the_scan_in_under_200_ms()
    {
        var fastFs = new FakeFileSystem(Linux);
        var slowFs = new FakeFileSystem(Linux) { EnumerationDelay = TimeSpan.FromMilliseconds(10) };
        AddSlowPackages(fastFs);
        AddSlowPackages(slowFs);

        var env = new FakeEnvironment(Linux);
        var runner = new FakeProcessRunner();
        var state = new FakeStateStore();
        var log = new FakeAppendLog();
        var clock = new FakeClock(Stamp);

        EcosystemRegistry scanRegistry = EcosystemRegistry.CreateDefault(slowFs);
        EcosystemRegistry doctorRegistry = EcosystemRegistry.CreateDefault(fastFs);
        QuarantineServices quarantine = ApplyWiring.CreateQuarantine(slowFs, env, state, log, clock);

        using GuiServices services = new(
            scanRegistry,
            new DoctorService(fastFs, runner, env, Linux, doctorRegistry),
            new ScanService(slowFs, runner, env, state, clock, scanRegistry),
            new SnapshotLibrary(state, clock),
            new PlanApplyService(slowFs, runner, env, state, log, clock, scanRegistry),
            new QuarantineLibrary(slowFs, quarantine.Index, quarantine.Restore));

        var viewModel = new MainWindowViewModel(services, GuiPreferences.Load(new FakeShellPreferencesStore()))
        {
            ProjectRoots = Projects,
        };

        Task scan = viewModel.ScanCommand.ExecuteAsync(null);

        // Wait until the scan (not the doctor) is enumerating, then let it settle into the package loop.
        await slowFs.FirstEnumeration.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.True(viewModel.IsBusy, "the deliberately slow scan should still be running");

        long started = Stopwatch.GetTimestamp();
        viewModel.ScanCancelCommand.Execute(null);
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        _output.WriteLine($"cancel-request -> IsBusy == false: {elapsed.TotalMilliseconds:F1} ms");

        Assert.False(viewModel.IsBusy, "the view model must leave the busy state after a cancel");
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(200),
            $"cancel-request to IsBusy == false took {elapsed.TotalMilliseconds:F1} ms");
    }

    private static void AddSlowPackages(FakeFileSystem fs)
    {
        fs.AddDirectory(Cache);

        for (int i = 0; i < 200; i++)
        {
            string directory = $"{Cache}/package{i:D3}/1.0.0";
            fs.AddDirectory(directory);
            fs.AddFile($"{directory}/package{i:D3}.1.0.0.nupkg", "payload");
        }
    }
}

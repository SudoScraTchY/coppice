using Coppice.Application;
using Coppice.Core.Quarantine;
using Coppice.Gui.Services;
using Coppice.Gui.ViewModels;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Gui;

/// <summary>
/// T-050 / T-039 acceptance: scan → plan review → apply → quarantine → restore, driven through the real
/// view-model commands over the injected <see cref="GuiServices"/> graph.
/// <para>
/// The application services are real; only the ports underneath them are fakes. This covers the view
/// model's command flow and the shared scan/plan/apply/quarantine wiring. The Avalonia views, bindings
/// and dispatcher are not exercised here; the plan equality test covers the policy, and the headless
/// shell itself is verified by the build.
/// </para>
/// </summary>
public sealed class GuiFlowEndToEndTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;
    private const string Cache = "/home/dev/.nuget/packages";
    private const string Projects = "/home/dev/code";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scan_plan_apply_and_restore_round_trip_through_the_view_model()
    {
        var fs = new FakeFileSystem(Linux);
        AddPackageVersions(fs);
        AddProject(fs);

        var env = new FakeEnvironment(Linux);
        var runner = new FakeProcessRunner();
        var state = new FakeStateStore();
        var log = new FakeAppendLog();
        var clock = new FakeClock(Stamp);
        var preferences = new FakeShellPreferencesStore();

        EcosystemRegistry registry = EcosystemRegistry.CreateDefault(fs);
        QuarantineServices quarantine = ApplyWiring.CreateQuarantine(fs, env, state, log, clock);

        using GuiServices services = new(
            registry,
            new DoctorService(fs, runner, env, Linux, registry),
            new ScanService(fs, runner, env, state, clock, registry),
            new SnapshotLibrary(state, clock),
            new PlanApplyService(fs, runner, env, state, log, clock, registry),
            new QuarantineLibrary(fs, quarantine.Index, quarantine.Restore));

        var viewModel = new MainWindowViewModel(services, GuiPreferences.Load(preferences))
        {
            ProjectRoots = Projects,
            SelectedPolicy = PolicyChoices.KeepNewestOnly,
        };

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSnapshot, viewModel.Status);
        Assert.True(viewModel.HasPlan, viewModel.PlanSummary);
        Assert.Equal(4, viewModel.PlanSteps.Count);
        Assert.Equal(4, viewModel.SelectedStepCount);
        Assert.Contains(viewModel.PlanSteps, step => step.Label == "newtonsoft.json 1.0.0");
        Assert.Equal(new[] { Projects }, preferences.Stored.ProjectRoots);

        string oldVersion = $"{Cache}/newtonsoft.json/1.0.0";
        string oldPackage = $"{oldVersion}/newtonsoft.json.1.0.0.nupkg";
        byte[] original = fs.ReadSmallBytes(oldPackage);

        viewModel.IsApplyConfirmed = true;
        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasApplyOutcome, viewModel.Status);
        Assert.Equal(StatusTone.Success, viewModel.StatusTone);
        Assert.False(viewModel.HasPlan);
        Assert.Equal(4, viewModel.LastAppliedCount);
        Assert.False(fs.DirectoryExists(oldVersion), "apply must move the version directory into quarantine");
        Assert.Equal(4, viewModel.QuarantineCount);
        Assert.All(viewModel.QuarantineEntries, row => Assert.True(row.ContentPresent));

        QuarantineRow row = Assert.Single(
            viewModel.QuarantineEntries,
            entry => entry.Label == "newtonsoft.json 1.0.0");
        Assert.Equal(oldVersion, row.OriginalPath);

        // The content moved, not a copy and not a deletion: the quarantined bytes are the originals.
        QuarantineEntry entry = Assert.Single(
            await quarantine.Index.ListAsync(),
            candidate => candidate.OriginalPath == oldVersion);
        Assert.Equal(original, fs.ReadSmallBytes($"{entry.QuarantinePath}/newtonsoft.json.1.0.0.nupkg"));

        viewModel.SelectedQuarantine = row;
        viewModel.IsRestoreConfirmed = true;
        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.QuarantineCount);
        Assert.Null(viewModel.SelectedQuarantine);
        Assert.Equal(StatusTone.Success, viewModel.StatusTone);
        Assert.True(fs.DirectoryExists(oldVersion), "restore must put the version directory back");
        Assert.Equal(original, fs.ReadSmallBytes(oldPackage));
    }

    private static void AddPackageVersions(FakeFileSystem fs)
    {
        fs.AddDirectory(Cache);

        foreach (string version in new[] { "1.0.0", "2.0.0", "3.0.0", "4.0.0", "5.0.0" })
        {
            string directory = $"{Cache}/newtonsoft.json/{version}";
            fs.AddDirectory(directory);
            fs.AddFile($"{directory}/newtonsoft.json.{version}.nupkg", $"package {version}");
        }
    }

    private static void AddProject(FakeFileSystem fs)
    {
        fs.AddFile($"{Projects}/app/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fs.AddFile($"{Projects}/app/obj/project.assets.json", """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Newtonsoft.Json/5.0.0": { "type": "package" }
                }
              }
            }
            """);
    }
}

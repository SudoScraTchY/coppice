using Coppice.Application;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Snapshots;
using Coppice.Gui.ViewModels;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Gui;

/// <summary>
/// T-039 acceptance: the GUI's "keep newest only" policy is the CLI's <c>--keep-latest 1</c>, and both
/// build the same plan from one snapshot. The GUI deliberately reuses the application preview service;
/// this test is what keeps a frontend from growing a second retention rule.
/// </summary>
public sealed class GuiPlanEqualityTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;
    private const string Cache = "/home/dev/.nuget/packages";
    private const string Projects = "/home/dev/code";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Keep_newest_only_produces_the_same_plan_as_the_cli_keep_latest_one()
    {
        var fs = new FakeFileSystem(Linux);
        fs.AddDirectory(Cache);

        foreach (string version in new[] { "1.0.0", "2.0.0", "3.0.0", "4.0.0", "5.0.0" })
        {
            string directory = $"{Cache}/newtonsoft.json/{version}";
            fs.AddDirectory(directory);
            fs.AddFile($"{directory}/newtonsoft.json.{version}.nupkg", $"package {version}");
        }

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

        var env = new FakeEnvironment(Linux);
        var state = new FakeStateStore();
        var clock = new FakeClock(Stamp);

        Ports.ScanRoot root = new(
            "nuget-packages",
            Cache,
            "package-cache",
            Ports.RootRole.Active,
            new Ports.FingerprintSpec(),
            "user",
            Ports.Risk.Safe);

        ScanReport scan = await new ScanService(fs, new FakeProcessRunner(), env, state, clock)
            .RunAsync([root], [Projects]);

        var planCommand = new PlanCommand(state, new FakePlanFileWriter(), new FakeApplyConsole(interactive: false), clock);
        Assert.Equal(ExitCodes.Success, planCommand.Run(["--keep-latest", "1"]));

        string key = Assert.Single(state.ListKeys("plans/"));
        string planId = key["plans/".Length..^".json".Length];
        Plan cliPlan = (await new PlanStore(state).LoadAsync(planId)).Plan!;
        Snapshot snapshot = (await new SnapshotStore(state, clock).LoadAsync(scan.SnapshotId)).Snapshot!;

        Plan guiPlan = PlanPreviewService.Preview(snapshot, PolicyChoices.KeepNewestOnly.Policy).Plan!;

        Assert.Equal(cliPlan.PlanId, guiPlan.PlanId);
        Assert.Equal(cliPlan.ExpectedReclaimBytes, guiPlan.ExpectedReclaimBytes);
        Assert.Equal(
            cliPlan.Steps.Select(step => step.ItemId).Order(StringComparer.Ordinal),
            guiPlan.Steps.Select(step => step.ItemId).Order(StringComparer.Ordinal));
        Assert.Equal(4, guiPlan.Steps.Count);
        Assert.DoesNotContain(
            guiPlan.Steps,
            step => snapshot.Items.Single(item => item.ItemId == step.ItemId).Version == "5.0.0");
    }
}

using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Scanning;

/// <summary>
/// T-045: project roots are what turn a cache full of Unknown items into evidence. A scan without them is
/// still safe — everything stays Unknown and nothing is planned — but it cannot justify a plan. These tests
/// drive the real ScanService with a fake machine, then feed the stored snapshot to the real PlanCommand.
/// </summary>
public sealed class ProjectRootScanTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;
    private const string Cache = "/home/dev/.nuget/packages";
    private const string Projects = "/home/dev/code";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Assets = """
        {
          "version": 3,
          "targets": {
            "net10.0": {
              "Newtonsoft.Json/13.0.3": { "type": "package" }
            }
          }
        }
        """;

    private sealed record World(
        FakeFileSystem Fs,
        FakeEnvironment Env,
        FakeProcessRunner Runner,
        FakeStateStore State,
        FakeClock Clock);

    private static World BuildWorld(
        IReadOnlyList<string>? versions = null,
        string referencedVersion = "13.0.3")
    {
        var fs = new FakeFileSystem(Linux);
        fs.AddDirectory(Cache);

        foreach (string version in versions ?? ["11.0.1", "12.0.1", "13.0.3"])
        {
            string directory = $"{Cache}/Newtonsoft.Json/{version}";
            fs.AddDirectory(directory);
            fs.AddFile($"{directory}/newtonsoft.json.{version}.nupkg", $"package {version}");
        }

        string assets = Assets.Replace("13.0.3", referencedVersion, StringComparison.Ordinal);
        fs.AddFile($"{Projects}/app/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fs.AddFile($"{Projects}/app/obj/project.assets.json", assets);

        return new World(
            fs,
            new FakeEnvironment(Linux),
            new FakeProcessRunner(),
            new FakeStateStore(),
            new FakeClock(Stamp));
    }

    private static Ports.ScanRoot CacheRoot() => new(
        "nuget-packages",
        Cache,
        "package-cache",
        Ports.RootRole.Active,
        new Ports.FingerprintSpec(),
        "user",
        Ports.Risk.Safe);

    private static ScanService Service(World world) =>
        new(world.Fs, world.Runner, world.Env, world.State, world.Clock);

    [Fact]
    public async Task A_project_root_makes_referenced_and_unreferenced_versions_provable_and_plan_uses_them()
    {
        World world = BuildWorld();

        ScanReport report = await Service(world).RunAsync([CacheRoot()], [Projects]);

        Assert.Equal(1, report.ProjectCount);

        Item current = Assert.Single(report.Items, item => item.Version == "13.0.3");
        Item oldest = Assert.Single(report.Items, item => item.Version == "11.0.1");
        Item middle = Assert.Single(report.Items, item => item.Version == "12.0.1");

        Assert.Equal("Referenced", current.Facts["usage"]);
        Assert.Equal("Unreferenced", oldest.Facts["usage"]);
        Assert.Equal("Unreferenced", middle.Facts["usage"]);

        // The default policy keeps the newest two, so the oldest of three is the only proposal.
        var console = new FakeApplyConsole(interactive: false);
        var plan = new PlanCommand(world.State, new FakePlanFileWriter(), console, world.Clock);

        Assert.Equal(ExitCodes.Success, plan.Run([]));

        string key = Assert.Single(world.State.ListKeys("plans/"));
        PlanLoadResult loaded = await new PlanStore(world.State).LoadAsync(key["plans/".Length..^".json".Length]);

        Assert.True(loaded.IsSuccess, loaded.Message);
        Assert.Equal(oldest.ItemId, Assert.Single(loaded.Plan!.Steps).ItemId);
    }

    [Fact]
    public async Task Five_versions_keep_the_newest_and_propose_the_other_four()
    {
        string[] versions = ["1.0.0", "2.0.0", "3.0.0", "4.0.0", "5.0.0"];
        World world = BuildWorld(versions, referencedVersion: "5.0.0");

        ScanReport report = await Service(world).RunAsync([CacheRoot()], [Projects]);

        Assert.Equal(5, report.Items.Count);
        Item newest = Assert.Single(report.Items, item => item.Version == "5.0.0");
        Assert.Equal("Referenced", newest.Facts["usage"]);
        Assert.All(
            report.Items.Where(item => item.Version != "5.0.0"),
            item => Assert.Equal("Unreferenced", item.Facts["usage"]));

        var console = new FakeApplyConsole(interactive: false);
        var plan = new PlanCommand(world.State, new FakePlanFileWriter(), console, world.Clock);

        Assert.Equal(ExitCodes.Success, plan.Run(["--keep-latest", "1"]));

        string key = Assert.Single(world.State.ListKeys("plans/"));
        PlanLoadResult loaded = await new PlanStore(world.State).LoadAsync(key["plans/".Length..^".json".Length]);

        Assert.True(loaded.IsSuccess, loaded.Message);
        Plan stored = loaded.Plan!;
        Assert.Equal(1, stored.Policy.KeepLatestN);
        Assert.Equal(4, stored.Steps.Count);
        Assert.DoesNotContain(stored.Steps, step => step.ItemId == newest.ItemId);

        Dictionary<string, string> versionByItem = report.Items.ToDictionary(item => item.ItemId, item => item.Version, StringComparer.Ordinal);
        Assert.Equal(
            versions[..4].Order(StringComparer.Ordinal),
            stored.Steps.Select(step => versionByItem[step.ItemId]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Four_part_versions_keep_the_true_newest()
    {
        string[] versions = ["1.0.0.1", "1.0.0.2", "1.0.0.9", "1.0.0.10"];
        World world = BuildWorld(versions, referencedVersion: "1.0.0.10");

        ScanReport report = await Service(world).RunAsync([CacheRoot()], [Projects]);
        Item newest = Assert.Single(report.Items, item => item.Version == "1.0.0.10");

        var console = new FakeApplyConsole(interactive: false);
        var plan = new PlanCommand(world.State, new FakePlanFileWriter(), console, world.Clock);

        Assert.Equal(ExitCodes.Success, plan.Run(["--keep-latest", "1"]));

        string key = Assert.Single(world.State.ListKeys("plans/"));
        PlanLoadResult loaded = await new PlanStore(world.State).LoadAsync(key["plans/".Length..^".json".Length]);

        Assert.True(loaded.IsSuccess, loaded.Message);
        Plan stored = loaded.Plan!;
        Assert.Equal(3, stored.Steps.Count);
        Assert.DoesNotContain(stored.Steps, step => step.ItemId == newest.ItemId);

        Dictionary<string, string> versionByItem = report.Items.ToDictionary(item => item.ItemId, item => item.Version, StringComparer.Ordinal);
        Assert.Equal(
            ["1.0.0.1", "1.0.0.2", "1.0.0.9"],
            stored.Steps.Select(step => versionByItem[step.ItemId]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Without_project_roots_every_item_is_unknown_and_nothing_is_planned()
    {
        World world = BuildWorld();

        ScanReport report = await Service(world).RunAsync([CacheRoot()], []);

        Assert.Equal(0, report.ProjectCount);
        Assert.All(report.Items, item => Assert.Equal("Unknown", item.Facts["usage"]));
        Assert.Contains(
            report.OnboardingHints,
            hint => hint.Contains("--projects", StringComparison.Ordinal));

        var console = new FakeApplyConsole(interactive: false);
        var plan = new PlanCommand(world.State, new FakePlanFileWriter(), console, world.Clock);

        Assert.Equal(ExitCodes.Success, plan.Run([]));
        Assert.Empty(world.State.ListKeys("plans/"));
        Assert.Contains("Nothing to propose", console.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_no_items_gets_no_onboarding_hint()
    {
        // T-056: a hint is advice about evidence the scan actually saw. With nothing scanned there
        // is no package to call Unknown and no reason to send the user looking for --projects, so
        // the report stays silent instead of printing a misleading instruction.
        var fs = new FakeFileSystem(Linux);
        var world = new World(fs, new FakeEnvironment(Linux), new FakeProcessRunner(), new FakeStateStore(), new FakeClock(Stamp));

        ScanReport report = await Service(world).RunAsync([], []);

        Assert.Empty(report.Items);
        Assert.Empty(report.OnboardingHints);
    }
}

using System.Text;
using System.Text.Json;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Ports;
using Coppice.Tests.Conformance;
using Xunit;
using Problem = Coppice.Core.Domain.Problem;

namespace Coppice.Tests.Fixtures;

/// <summary>
/// Golden report snapshots (T-017, 11-testing "Golden reports": scan fixture → byte-identical).
/// <para>
/// NFR-06 says two scans of the same machine state must produce identical output. That is a
/// claim about BYTES, so it is tested against bytes: the rendered console output and the JSON are
/// compared to a committed file, not to a previous run held in memory.
/// </para>
/// <para>
/// The golden files are what make the determinism claim falsifiable. Without them, "two runs agree"
/// is only ever checked against the same implementation that produced both, which is circular.
/// </para>
/// </summary>
public sealed class GoldenReportTests
{
    /// <summary>One seed per OS flavor. Changing a seed means re-recording — deliberately annoying.</summary>
    private const int Seed = 20_260_902;

    public static TheoryData<OperatingSystemKind> OperatingSystems =>
        [OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS];

    // ---- the scan under test ----

    /// <summary>
    /// Runs the real scan pipeline over a generated image.
    /// <para>
    /// The clock is fixed, so the snapshot id and timestamp in the output are stable. Without that,
    /// every run would differ in the first two lines and the golden file would be worthless.
    /// </para>
    /// </summary>
    private static async Task<ScanReport> ScanAsync(OperatingSystemKind os, int packageCount = 12)
    {
        MachineImage image = FixtureGenerator.Generate(
            os,
            seed: Seed,
            packageCount: packageCount,
            sdkCount: 3,
            toolCount: 4,
            representativeSdks: true);

        char separator = os == OperatingSystemKind.Windows ? '\\' : '/';
        var env = new Coppice.Tests.Support.FakeEnvironment(os) { HomeDirectory = image.HomeDirectory };

        // Joined with the OS's own separator. Hardcoding a forward slash here put a mixed-separator
        // entry ('C:\Users\dev/.dotnet/tools') into every Windows golden file, which is not a shape
        // Windows ever produces and would hide a real separator bug behind an expected oddity.
        env.WithPath($"{image.HomeDirectory}{separator}.dotnet{separator}tools");
        env.With("DOTNET_ROOT", FixtureGenerator.PathsFor(os, image.HomeDirectory).DotnetRoot);

        var roots = new List<Ports.ScanRoot>
        {
            new(
                "nuget-packages",
                FixtureGenerator.PathsFor(os, image.HomeDirectory).NuGetPackages,
                "package-cache",
                Ports.RootRole.Active,
                ConformanceFixture.NuGetFingerprint,
                "user",
                Ports.Risk.Safe),
            new(
                "dotnet-tools",
                FixtureGenerator.PathsFor(os, image.HomeDirectory).Tools,
                "global-tools",
                Ports.RootRole.Active,
                new Ports.FingerprintSpec(),
                "user",
                Ports.Risk.Review),
            new(
                "dotnet-root",
                FixtureGenerator.PathsFor(os, image.HomeDirectory).DotnetRoot,
                "toolchain",
                Ports.RootRole.Active,
                new Ports.FingerprintSpec(),
                "installer",
                Ports.Risk.Manual),
        };

        var service = new ScanService(
            image.FileSystem,
            FixtureGenerator.RunnerFor(image.ToolOutputs),
            env,
            new Coppice.Tests.Support.FakeStateStore(),
            new Coppice.Tests.Support.FakeClock(DateTimeOffset.UnixEpoch));

        return await service.RunAsync(roots, projectRoots: []);
    }

    // ---- golden comparison ----

    private static string GoldenPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "golden", name);

    private static string RenderConsole(ScanReport report)
    {
        var sb = new StringBuilder();
        sb.Append("coppice scan\n\n");
        sb.Append($"snapshot: {report.SnapshotId}\n");
        sb.Append($"os: {report.OS}\n");
        sb.Append($"projects: {report.ProjectCount}\n");
        sb.Append($"ecosystems: {string.Join(", ", report.Ecosystems)}\n\n");
        sb.Append(TextReport.UsageHeader()).Append('\n');
        sb.Append(new string('-', 60)).Append('\n');
        sb.Append(TextReport.UsageRow(report.Usage)).Append('\n');
        sb.Append('\n');
        sb.Append(TextReport.ItemHeader()).Append('\n');
        sb.Append(new string('-', 120)).Append('\n');

        foreach (Item item in report.Items)
        {
            sb.Append(TextReport.ItemRow(item)).Append('\n');
        }

        sb.Append('\n');
        sb.Append($"{report.Items.Count} item(s) in {report.Ecosystems.Count} ecosystem(s).").Append('\n');

        foreach (string hint in report.OnboardingHints)
        {
            sb.Append('\n').Append($"hint: {hint}").Append('\n');
        }

        if (report.Problems.Count > 0)
        {
            sb.Append('\n').Append("HEALTH PROBLEMS:\n");
            sb.Append(TextReport.ProblemHeader()).Append('\n');
            sb.Append(new string('-', 120)).Append('\n');
            foreach (Problem problem in report.Problems)
            {
                sb.Append(TextReport.ProblemRow(problem)).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compares against the committed golden file, creating it on first run.
    /// <para>
    /// Auto-creating is a deliberate hazard that has to be balanced by a second test: this one proves
    /// the render is STABLE, and <c>GoldenFilesAreCommitted</c> below proves the file is actually in
    /// the repository. Together, a golden file cannot quietly become a fresh snapshot of whatever the
    /// code currently does.
    /// </para>
    /// </summary>
    private static void AssertGolden(string name, string actual)
    {
        string path = GoldenPath(name);
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

    /// <summary>
    /// Line endings are not part of what these tests are about, and a repository checked out on
    /// Windows vs Linux would otherwise fail for reasons that have nothing to do with the output.
    /// Everything else — spacing, ordering, content — is compared exactly.
    /// </summary>
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- the tests ----

    [Theory]
    [MemberData(nameof(OperatingSystems))]
    public async Task The_console_report_matches_the_golden_file(OperatingSystemKind os)
    {
        ScanReport report = await ScanAsync(os);
        AssertGolden($"scan-{os}.txt", RenderConsole(report));
    }

    [Theory]
    [MemberData(nameof(OperatingSystems))]
    public async Task The_json_report_matches_the_golden_file(OperatingSystemKind os)
    {
        ScanReport report = await ScanAsync(os);

        // The machine-readable form is what tooling consumes, so it is golden in its own right rather
        // than derived from the console one: they can diverge, and only one of them may.
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        AssertGolden($"scan-{os}.json", json);
    }

    [Theory]
    [MemberData(nameof(OperatingSystems))]
    public async Task Two_runs_of_the_same_fixture_produce_identical_bytes(OperatingSystemKind os)
    {
        // The in-memory half of NFR-06, kept separate from the file comparison: one proves stability
        // across runs, the other proves the bytes are the agreed ones.
        ScanReport first = await ScanAsync(os);
        ScanReport second = await ScanAsync(os);

        Assert.Equal(RenderConsole(first), RenderConsole(second));
        Assert.Equal(
            first.Items.Select(i => i.ItemId),
            second.Items.Select(i => i.ItemId));
    }

    [Theory]
    [MemberData(nameof(OperatingSystems))]
    public async Task Item_order_is_total_and_does_not_depend_on_the_filesystem(OperatingSystemKind os)
    {
        ScanReport report = await ScanAsync(os);

        // Sorted by name then version, so two packages that differ only in version cannot swap places
        // between runs. Ordinal, never culture-aware: a Turkish locale must not reorder 'I' and 'i'.
        var expected = report.Items
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Version, StringComparer.Ordinal)
            .Select(i => $"{i.Name}/{i.Version}")
            .ToList();

        Assert.Equal(expected, report.Items.Select(i => $"{i.Name}/{i.Version}").ToList());
    }

    [Theory]
    [MemberData(nameof(OperatingSystems))]
    public async Task Every_item_id_is_unique_within_the_scan(OperatingSystemKind os)
    {
        ScanReport report = await ScanAsync(os);

        // ItemId is the plan step key. A duplicate would mean two cache entries share one key, and a
        // plan built from it would silently act on only one of them.
        Assert.Equal(report.Items.Count, report.Items.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(OperatingSystems))]
    public async Task Health_problems_are_ordered_most_severe_first(OperatingSystemKind os)
    {
        ScanReport report = await ScanAsync(os);

        for (int i = 1; i < report.Problems.Count; i++)
        {
            Assert.True(
                report.Problems[i - 1].Severity >= report.Problems[i].Severity,
                $"{report.Problems[i - 1].Severity} {report.Problems[i - 1].Code} sorted before "
                + $"{report.Problems[i].Severity} {report.Problems[i].Code}.");
        }
    }

    /// <summary>
    /// The source tree, NOT the test output directory. The csproj copies golden/ next to the binary,
    /// so checking the copy would be circular: the build would recreate whatever was missing and the
    /// guard would always pass. The question is whether the files are in the REPOSITORY.
    /// </summary>
    private static string SourceGoldenDirectory
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coppice.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir is not null, "could not locate the repository root.");

            return Path.Combine(dir.FullName, "tests", "Coppice.Tests", "golden");
        }
    }

    [Fact]
    public void Every_golden_file_is_committed_to_the_repository()
    {
        // The balance for AssertGolden's auto-create: without this, "no golden file" and "golden file
        // that was regenerated to match a regression" look identical in CI.
        string directory = SourceGoldenDirectory;
        Assert.True(Directory.Exists(directory), $"no golden files are pinned at {directory}.");

        string[] expected =
        [
            "scan-Linux.txt", "scan-Linux.json",
            "scan-Windows.txt", "scan-Windows.json",
            "scan-MacOS.txt", "scan-MacOS.json",
        ];

        foreach (string name in expected)
        {
            Assert.True(
                File.Exists(Path.Combine(directory, name)),
                $"{name} is missing. A golden report that is not pinned proves nothing.");
        }
    }

    [Theory]
    [InlineData("scan-Linux.txt")]
    [InlineData("scan-Windows.txt")]
    [InlineData("scan-MacOS.txt")]
    public void Console_golden_files_look_like_a_real_report(string name)
    {
        // A golden file nobody can read is indistinguishable from a corrupted one. These are the
        // landmarks a reviewer needs to tell at a glance whether a diff means anything.
        string path = Path.Combine(SourceGoldenDirectory, name);
        Assert.True(File.Exists(path), $"{name} is not pinned.");

        string content = File.ReadAllText(path, Encoding.UTF8);
        Assert.StartsWith("coppice scan", content, StringComparison.Ordinal);
        Assert.Contains("ecosystems:", content, StringComparison.Ordinal);
        Assert.Contains("USAGE", content, StringComparison.Ordinal);
        Assert.Contains("item(s) in", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scan-Linux.json")]
    [InlineData("scan-Windows.json")]
    [InlineData("scan-MacOS.json")]
    public void Json_golden_files_parse_and_carry_the_fields_the_cli_guarantees(string name)
    {
        string path = Path.Combine(SourceGoldenDirectory, name);
        Assert.True(File.Exists(path), $"{name} is not pinned.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        JsonElement root = document.RootElement;

        foreach (string field in new[] { "SnapshotId", "OS", "Items", "Usage", "Problems" })
        {
            Assert.True(root.TryGetProperty(field, out _), $"{name} is missing the '{field}' field.");
        }
    }
}

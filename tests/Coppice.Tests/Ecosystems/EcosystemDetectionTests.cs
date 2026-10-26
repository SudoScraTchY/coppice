using System.Diagnostics;

using Coppice.Application;
using Coppice.Manifests;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Coppice.Tests.Ecosystems;

/// <summary>
/// FR-01/T-051: every ecosystem declares how to detect itself, and the coordinator reports the answer
/// and its source without knowing a single ecosystem name.
/// <para>
/// These drive the descriptor through the real registry and the real detector with the fake ports, so
/// the cases that matter — installed, missing, erroring, marker-only — are exercised the same way a
/// scan exercises them.
/// </para>
/// </summary>
public sealed class EcosystemDetectionTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;

    private readonly ITestOutputHelper _output;

    public EcosystemDetectionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_declared_tool_reports_present_with_its_source()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);
        var runner = new FakeProcessRunner().Respond("dotnet", "10.0.100\n");

        DoctorReport report = new DoctorService(fs, runner, env, Linux).Inspect();

        // Every shipped ecosystem is reported, present or not; doctor never silently omits one.
        Assert.Equal(
            ["dotnet", "go", "node", "rust"],
            report.Detection.Select(d => d.EcosystemId));

        EcosystemDetectionReport dotnet = report.Detection.Single(d => d.EcosystemId == "dotnet");
        Assert.True(dotnet.Present);
        Assert.Equal("tool", dotnet.Source);
        Assert.Contains("dotnet --version -> 10.0.100", dotnet.Detail, StringComparison.Ordinal);

        // The machine-readable surface carries the same evidence.
        string json = DoctorService.ToJson(report);
        Assert.Contains("\"source\": \"tool\"", json, StringComparison.Ordinal);
        Assert.Contains("\"present\": true", json, StringComparison.Ordinal);

        // And so does the console surface.
        string text = Coppice.Cli.DoctorText.ToText(report, env.HomeDirectory);
        Assert.Contains("Ecosystems:", text, StringComparison.Ordinal);
        Assert.Contains("dotnet", text, StringComparison.Ordinal);
        Assert.Contains("present", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_tool_is_absent_not_a_crash()
    {
        // FakeProcessRunner answers an unknown command with exit 127, the port's "could not start".
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);

        DoctorReport report = new DoctorService(fs, new FakeProcessRunner(), env, Linux).Inspect();

        EcosystemDetectionReport go = report.Detection.Single(d => d.EcosystemId == "go");
        Assert.False(go.Present);
        Assert.Equal("none", go.Source);
        Assert.Contains("go version was not found", go.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_erroring_tool_is_absent_with_the_error_as_the_detail()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);
        var runner = new FakeProcessRunner()
            .Fail("rustc", 1, "error: rustup could not choose a version of rustc to run");

        DoctorReport report = new DoctorService(fs, runner, env, Linux).Inspect();

        EcosystemDetectionReport rust = report.Detection.Single(d => d.EcosystemId == "rust");

        // The tool ran and said no. That is an answer, not a missing binary, so it is reported with the
        // tool as the source and the error text kept for the user.
        Assert.False(rust.Present);
        Assert.Equal("tool", rust.Source);
        Assert.Contains("rustc --version exited 1", rust.Detail, StringComparison.Ordinal);
        Assert.Contains("rustup could not choose a version", rust.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_marker_answers_when_the_tool_is_missing()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);
        fs.AddDirectory("/home/dev/.rustup/toolchains");

        DoctorReport report = new DoctorService(fs, new FakeProcessRunner(), env, Linux).Inspect();

        EcosystemDetectionReport rust = report.Detection.Single(d => d.EcosystemId == "rust");
        Assert.True(rust.Present);
        Assert.Equal("marker", rust.Source);
        Assert.Equal("/home/dev/.rustup/toolchains", rust.Detail);
    }

    /// <summary>
    /// Detection is declared data: every shipped profile has one, and a manifest can add one without
    /// the registry or the detector learning a new name.
    /// </summary>
    [Fact]
    public void Every_shipped_profile_declares_detection_and_a_manifest_may_add_its_own()
    {
        EcosystemRegistry shipped = EcosystemRegistry.CreateDefault(new FakeFileSystem(Linux));

        Assert.All(shipped.Ecosystems, e =>
            Assert.True(e.Detection is { IsDeclared: true }, $"{e.Id} declares no detection probe"));

        Assert.Equal(
            "dotnet --version",
            shipped.FindEcosystem("dotnet")!.Detection!.Tool);
        Assert.Equal(
            "{home}/.rustup/toolchains",
            shipped.FindEcosystem("rust")!.Detection!.Marker);

        const string manifest = """
            schema = 1
            id = "fifth"

            [detection]
            tool = "fifth --version"
            marker = "{home}/.fifth"

            [[location]]
            id = "fifth-cache"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "{home}/.fifth/cache" }]
            """;

        ManifestValidationResult loaded = new ManifestLoader(new FakeFileSystem(Linux))
            .ValidateText(manifest, ManifestOrigin.User, "fifth.toml");

        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors.Select(e => e.ToDisplayString())));
        Assert.Equal("fifth --version", loaded.Detection.Tool);
        Assert.Equal("{home}/.fifth", loaded.Detection.Marker);

        // An unknown detection key is refused rather than ignored: a probe the tool never runs is
        // worse than no probe, because the report would say "absent" with confidence.
        ManifestValidationResult unknownKey = new ManifestLoader(new FakeFileSystem(Linux))
            .ValidateText(
                manifest.Replace("tool =", "command =", StringComparison.Ordinal),
                ManifestOrigin.User,
                "fifth.toml");

        Assert.False(unknownKey.IsValid);
        Assert.Contains(unknownKey.Errors, e => e.Field == "detection.command");
    }

    /// <summary>
    /// The scan coordinator asks the registry, so an ecosystem it has never heard of is detected from
    /// its own descriptor. Nothing in <see cref="ScanService"/> names it.
    /// </summary>
    [Fact]
    public async Task The_scan_coordinator_reports_detection_from_registry_data()
    {
        var fs = new FakeFileSystem(Linux);
        fs.AddDirectory("/fifth/bin");
        var env = new FakeEnvironment(Linux);
        var plugin = new ProbeEcosystem("fifth");
        var registry = new EcosystemRegistry(
        [
            new EcosystemPlugin(
                "fifth",
                "Fifth",
                [],
                plugin,
                plugin.Versions,
                new EcosystemDetection(Marker: "/fifth/bin")),
        ]);

        var service = new ScanService(
            fs,
            new FakeProcessRunner(),
            env,
            new FakeStateStore(),
            clock: null,
            registry: registry);
        ScanReport report = await service.RunAsync([], projectRoots: []);

        EcosystemDetectionReport fifth = Assert.Single(report.Detection);
        Assert.Equal("fifth", fifth.EcosystemId);
        Assert.True(fifth.Present);
        Assert.Equal("marker", fifth.Source);

        // The coordinator delegates; it does not branch. Reading the source is the direct assertion:
        // the detector is constructed from the registry, and no shipped id appears in the file.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Coppice.Application", "ScanService.cs"));
        Assert.Contains("EcosystemDetector", source, StringComparison.Ordinal);

        foreach (string id in EcosystemRegistry.CreateDefault(new FakeFileSystem(Linux)).Ecosystems.Select(e => e.Id))
        {
            Assert.DoesNotContain($"\"{id}\"", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Detection is bounded and cancellable like every other probe: a tool that never answers must not
    /// outlive the verb. The runner blocks until it observes the token, so this cannot pass unless the
    /// token reaches the probe.
    /// </summary>
    [Fact]
    public async Task Cancelling_detection_aborts_a_blocked_probe()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);
        var plugin = new ProbeEcosystem("probe");
        var registry = new EcosystemRegistry(
        [
            new EcosystemPlugin(
                "probe",
                "Probe",
                [],
                plugin,
                plugin.Versions,
                new EcosystemDetection(Tool: "probe --version")),
        ]);

        var runner = new BlockingProcessRunner();
        var detector = new EcosystemDetector(fs, runner, env);

        using var cts = new CancellationTokenSource();
        Task<IReadOnlyList<EcosystemDetectionReport>> detect = Task.Run(
            () => detector.Detect(registry.Ecosystems, cts.Token));

        ProcessRequest request = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), request.Timeout);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detect);
        stopwatch.Stop();

        _output.WriteLine($"detection cancellation observed after {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"cancellation took {stopwatch.Elapsed.TotalMilliseconds:F0} ms; expected under 1000 ms");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coppice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class BlockingProcessRunner : IProcessRunner
    {
        public TaskCompletionSource<ProcessRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Started.TrySetResult(request);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ProbeEcosystem : IEcosystem
    {
        public ProbeEcosystem(string id) => Id = id;

        public string Id { get; }

        public IVersionOrdering Versions { get; } = new ProbeOrdering();

        public bool IsPresent(Coppice.Ports.ScanContext ctx) => false;
    }

    private sealed class ProbeOrdering : IVersionOrdering
    {
        public VersionComponents? TryParse(string version) => new(1, 0, 0, 0, null, null);

        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
    }
}

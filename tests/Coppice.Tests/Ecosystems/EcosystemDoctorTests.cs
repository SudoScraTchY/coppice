using System.Diagnostics;

using Coppice.Application;
using Coppice.Core.Resolution;
using Coppice.Manifests;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Xunit.Abstractions;
using ResolvedVia = Coppice.Core.Domain.ResolvedVia;

namespace Coppice.Tests.Ecosystems;

/// <summary>
/// T-048: doctor sees every ecosystem's locations, and the roots it resolves are the roots scan reads.
/// <para>
/// Manifest-backed profiles go through the same <see cref="Coppice.Core.Resolution.ResolutionEngine"/>,
/// denylist and fingerprint gate as .NET; this test proves the wiring, not the plugin logic.
/// </para>
/// </summary>
public sealed class EcosystemDoctorTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;

    private readonly ITestOutputHelper _output;

    public EcosystemDoctorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Doctor_resolves_go_rust_and_node_roots_and_scan_wiring_carries_them()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);

        // Go: a mod cache with the marker directory Go always creates.
        fs.AddDirectory("/home/dev/go/pkg/mod/cache/download");
        fs.AddDirectory("/home/dev/go/pkg/mod/github.com/pkg/errors@v0.9.1");

        // Rust: the real extracted-crate shape, including the registry index segment.
        fs.AddDirectory("/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/serde-1.0.197");

        // Node: the npm cache root with its content-addressed store.
        fs.AddDirectory("/home/dev/.npm/_cacache/content-v2");

        var doctor = new DoctorService(fs, new FakeProcessRunner(), env, Linux);
        DoctorReport report = doctor.Inspect();

        Assert.Contains(report.Roots, r => r.LocationId == "go-mod-cache" && r.Root.Validity == RootValidity.Ok);
        Assert.Contains(report.Roots, r => r.LocationId == "cargo-registry-src" && r.Root.Validity == RootValidity.Ok);
        Assert.Contains(report.Roots, r => r.LocationId == "npm-cache" && r.Root.Validity == RootValidity.Ok);

        // Only ecosystems with a real root are reported present; the empty .NET defaults are NotFound.
        Assert.Equal(["go", "node", "rust"], report.Ecosystems);

        IReadOnlyList<Ports.ScanRoot> roots = ScanWiring.FromDoctor(report, doctor.Registry);

        Assert.Contains(roots, r => r.LocationId == "go-mod-cache" && r.Tier == Ports.Risk.Review);
        Assert.Contains(roots, r => r.LocationId == "cargo-registry-src" && r.Tier == Ports.Risk.Review);
        Assert.Contains(roots, r => r.LocationId == "npm-cache" && r.Tier == Ports.Risk.Safe);

        // The gateway's shape check is declared next to the location, so a manifest-backed item can
        // bind a path delete. Go's module paths nest arbitrarily, so it deliberately has no pattern:
        // its native route is `go clean -modcache`, not a path delete.
        IReadOnlyList<Coppice.Core.Apply.BindingRoot> bindings = ApplyWiring.BindingRoots(report, doctor.Registry);
        Assert.Equal("*/*", bindings.Single(b => b.LocationId == "cargo-registry-src").AllowlistPattern);
        Assert.Equal("*", bindings.Single(b => b.LocationId == "npm-cache").AllowlistPattern);
        Assert.Null(bindings.Single(b => b.LocationId == "go-mod-cache").AllowlistPattern);
    }

    [Fact]
    public void A_path_delete_allowlist_requires_an_explicitly_listed_location()
    {
        // Explicitly approved shapes keep their allowlists.
        Assert.Equal("*/*", EcosystemCatalog.FindLocation("nuget-packages")!.AllowlistPattern);
        Assert.Equal("*/*", EcosystemCatalog.FindLocation("cargo-registry-src")!.AllowlistPattern);
        Assert.Equal("*", EcosystemCatalog.FindLocation("rustup-toolchains")!.AllowlistPattern);
        Assert.Equal("*", EcosystemCatalog.FindLocation("npm-cache")!.AllowlistPattern);
        Assert.Equal("*", EcosystemCatalog.FindLocation("npm-global")!.AllowlistPattern);

        // Go's module cache is listed with an empty pattern: explicitly not deletable by path.
        Assert.Null(EcosystemCatalog.FindLocation("go-mod-cache")!.AllowlistPattern);

        // Fail-closed: these locations are NOT in their definition's allowlist table. Some have a
        // layout that would previously have derived a pattern ("{name}/{version}" for cargo-registry),
        // and one is the whole-root unit. A changed or unknown layout must never grant a delete.
        Assert.Null(EcosystemCatalog.FindLocation("cargo-registry")!.AllowlistPattern);
        Assert.Null(EcosystemCatalog.FindLocation("cargo-registry-cache")!.AllowlistPattern);
        Assert.Null(EcosystemCatalog.FindLocation("cargo-git")!.AllowlistPattern);
        Assert.Null(EcosystemCatalog.FindLocation("go-build-cache")!.AllowlistPattern);
        Assert.Null(EcosystemCatalog.FindLocation("go-bin")!.AllowlistPattern);
        Assert.Null(EcosystemCatalog.FindLocation("dotnet-tools")!.AllowlistPattern);
        Assert.Null(EcosystemCatalog.FindLocation("dotnet-workloads")!.AllowlistPattern);
    }

    [Fact]
    public async Task A_scan_driven_entirely_by_doctor_roots_finds_all_three_ecosystems()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);

        fs.AddDirectory("/home/dev/go/pkg/mod/cache/download");
        fs.AddDirectory("/home/dev/go/pkg/mod/github.com/pkg/errors@v0.9.1");
        fs.AddDirectory("/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/serde-1.0.197");
        fs.AddDirectory("/home/dev/.npm/_cacache/content-v2/sha512");

        var runner = new FakeProcessRunner();
        var doctor = new DoctorService(fs, runner, env, Linux);
        DoctorReport report = doctor.Inspect();

        var service = new ScanService(fs, runner, env, new FakeStateStore());
        ScanReport scan = await service.RunAsync(ScanWiring.FromDoctor(report, doctor.Registry), projectRoots: []);

        Assert.Equal(["go", "node", "rust"], scan.Ecosystems);
        Assert.Contains(scan.Items, i => i.Ecosystem == "go" && i.Name == "github.com/pkg/errors");
        Assert.Contains(scan.Items, i => i.Ecosystem == "rust" && i.Name == "serde");
        Assert.Contains(scan.Items, i => i.Ecosystem == "node" && i.Name == "npm-cache");

        // With no project roots every item is Unknown, and the node/rust safe items were floored to
        // Review by the shared usage floor.
        Assert.All(scan.Items, item =>
        {
            Assert.Equal("Unknown", item.Facts["usage"]);
            Assert.NotEqual(Risk.Safe, item.Risk);
        });
    }

    [Fact]
    public async Task The_snapshot_stores_every_scanned_ecosystem_and_the_report_lists_them()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);
        var state = new FakeStateStore();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);

        fs.AddDirectory("/go/mod/cache");
        fs.AddDirectory("/go/mod/github.com/pkg/errors@v0.9.1");
        fs.AddDirectory("/cargo/src/index.crates.io-1949cf8c6b5b557f/serde-1.0.197");
        fs.AddDirectory("/npm-global/typescript");
        fs.AddFile("/npm-global/typescript/package.json", """{"name":"typescript","version":"5.4.5"}""");

        var roots = new List<Ports.ScanRoot>
        {
            new("go-mod-cache", "/go/mod", "package-cache", Ports.RootRole.Active, new Ports.FingerprintSpec(), "user", Ports.Risk.Safe),
            new("cargo-registry-src", "/cargo/src", "package-cache", Ports.RootRole.Active, new Ports.FingerprintSpec(), "user", Ports.Risk.Safe),
            new("npm-global", "/npm-global", "tool-cache", Ports.RootRole.Active, new Ports.FingerprintSpec(), "user", Ports.Risk.Review),
        };

        ScanReport report = await new ScanService(fs, new FakeProcessRunner(), env, state, clock)
            .RunAsync(roots, projectRoots: []);

        Assert.Equal(["go", "node", "rust"], report.Ecosystems);

        var loaded = await new Coppice.Core.Snapshots.SnapshotStore(state, clock).LoadAsync(report.SnapshotId);
        Assert.True(loaded.Ok, loaded.Message);
        Assert.Equal(["go", "node", "rust"], loaded.Snapshot!.Ecosystems);
    }

    /// <summary>
    /// T-056: a doctor probe that never returns is cut off by the caller's token. The runner blocks
    /// until it observes cancellation, so this test cannot finish unless Inspect actually passes
    /// the token through. The measured bound is wall time from Cancel() to the observed
    /// OperationCanceledException; it must be far below the 2 s probe timeout.
    /// </summary>
    [Fact]
    public async Task Cancelling_doctor_aborts_a_blocked_probe_promptly()
    {
        var fs = new FakeFileSystem(Linux);
        var env = new FakeEnvironment(Linux);

        var location = new EcosystemLocation(
            "probe",
            "probe-cache",
            "package-cache",
            ResolveMode.First,
            new Ports.FingerprintSpec(),
            Risk.Safe,
            "user",
            InstallerOwned: false,
            AllowlistPattern: null,
            ConfidencePenalty: 0,
            Sources: [new LocationSource(ResolvedVia.Tool, Run: "probe --where", Parse: ManifestParserKind.Line)],
            DefaultPaths: new Dictionary<OperatingSystemKind, string> { [Linux] = "/probe/cache" },
            SupportedOperatingSystems: [Linux]);

        var registry = new EcosystemRegistry(
            [new EcosystemPlugin("probe", "Probe", [location], new ProbeEcosystem(), new ProbeOrdering())]);
        var runner = new BlockingProcessRunner();
        var doctor = new DoctorService(fs, runner, env, Linux, registry);

        using var cts = new CancellationTokenSource();
        Task<DoctorReport> inspect = Task.Run(() => doctor.Inspect(cts.Token));

        try
        {
            ProcessRequest request = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Bounded independently of cancellation; the exact value is the contract this test pins.
            Assert.Equal(TimeSpan.FromSeconds(2), request.Timeout);
            Assert.Equal("probe", request.FileName);

            var stopwatch = Stopwatch.StartNew();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspect);
            stopwatch.Stop();

            _output.WriteLine($"cancellation observed after {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"cancellation took {stopwatch.Elapsed.TotalMilliseconds:F0} ms; expected under 1000 ms");
        }
        finally
        {
            // Keep a failed assertion from leaking a task blocked forever on an infinite delay.
            cts.Cancel();
        }
    }

    /// <summary>Blocks until the token is cancelled, and records the probe it was asked to run.</summary>
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
        public string Id => "probe";

        public IVersionOrdering Versions { get; } = new ProbeOrdering();

        public bool IsPresent(Coppice.Ports.ScanContext ctx) => false;
    }

    private sealed class ProbeOrdering : IVersionOrdering
    {
        public VersionComponents? TryParse(string version) => new(1, 0, 0, 0, null, null);

        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
    }
}

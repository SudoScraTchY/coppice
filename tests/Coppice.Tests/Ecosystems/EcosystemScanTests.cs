using Coppice.Application;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Ecosystems;

/// <summary>
/// T-048: Go, Rust and Node are wired into the REAL scan and plan path, not just their plugins.
/// <para>
/// These tests drive <see cref="ScanService"/> and the CLI's plan verb against the fake machine, so a
/// plugin that passes conformance but is never asked to scan fails here.
/// </para>
/// </summary>
public sealed class EcosystemScanTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;
    private static readonly DateTimeOffset Stamp = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed record World(
        FakeFileSystem Fs,
        FakeEnvironment Env,
        FakeProcessRunner Runner,
        FakeStateStore State,
        FakeClock Clock);

    private static World EmptyWorld()
    {
        var fs = new FakeFileSystem(Linux);
        return new World(fs, new FakeEnvironment(Linux), new FakeProcessRunner(), new FakeStateStore(), new FakeClock(Stamp));
    }

    private static ScanService Service(World world, EcosystemRegistry? registry = null) =>
        new(world.Fs, world.Runner, world.Env, world.State, world.Clock, registry);

    private static PlanCommand Planner(World world) =>
        new(world.State, new FakePlanFileWriter(), new FakeApplyConsole(interactive: false), world.Clock);

    private static Plan LoadPlan(World world)
    {
        string key = Assert.Single(world.State.ListKeys("plans/"));
        PlanLoadResult loaded = new PlanStore(world.State)
            .LoadAsync(key["plans/".Length..^".json".Length])
            .GetAwaiter()
            .GetResult();

        Assert.True(loaded.IsSuccess, loaded.Message);
        return loaded.Plan!;
    }

    private static Ports.ScanRoot Root(string locationId, string path, Ports.Risk risk = Ports.Risk.Safe) =>
        new(locationId, path, "package-cache", Ports.RootRole.Active, new Ports.FingerprintSpec(), "user", risk);

    // ---- Go: module versions, go.sum references, keep-latest --------------------------------

    private const string GoCache = "/home/dev/go/pkg/mod";
    private const string GoProject = "/home/dev/src/go-app";

    private static World GoWorld()
    {
        World world = EmptyWorld();

        world.Fs
            .AddFile($"{GoCache}/cache/download/github.com/pkg/errors/@v/v0.9.1.zip", "zip")
            .AddDirectory($"{GoCache}/github.com/pkg/errors@v0.9.0")
            .AddFile($"{GoCache}/github.com/pkg/errors@v0.9.0/errors.go", "package errors")
            .AddDirectory($"{GoCache}/github.com/pkg/errors@v0.9.1")
            .AddFile($"{GoCache}/github.com/pkg/errors@v0.9.1/errors.go", "package errors");

        world.Fs
            .AddFile($"{GoProject}/go.mod", "module example.com/app\n\ngo 1.22\n")
            .AddFile($"{GoProject}/go.sum", "github.com/pkg/errors v0.9.1 h1:abcdefghijklmnop=\n");

        return world;
    }

    [Fact]
    public async Task A_go_mod_cache_is_scanned_and_the_referenced_version_is_marked()
    {
        World world = GoWorld();

        ScanReport report = await Service(world).RunAsync([Root("go-mod-cache", GoCache)], [GoProject]);

        Assert.Equal(["go"], report.Ecosystems);
        Assert.Equal(2, report.Items.Count);

        Item referenced = Assert.Single(report.Items, i => i.Version == "v0.9.1");
        Item old = Assert.Single(report.Items, i => i.Version == "v0.9.0");

        Assert.Equal("go", referenced.Ecosystem);
        Assert.Equal("github.com/pkg/errors", referenced.Name);
        Assert.Equal("Referenced", referenced.Facts["usage"]);
        Assert.Equal("Unreferenced", old.Facts["usage"]);
        Assert.True(referenced.Facts.ContainsKey("lastWriteUtc"), "strict apply needs the scan-time mtime");
    }

    [Fact]
    public async Task Keep_latest_proposes_the_older_go_module_the_project_does_not_use()
    {
        World world = GoWorld();

        ScanReport report = await Service(world).RunAsync([Root("go-mod-cache", GoCache)], [GoProject]);
        Item old = Assert.Single(report.Items, i => i.Version == "v0.9.0");

        Assert.Equal(ExitCodes.Success, Planner(world).Run(["--keep-latest", "1"]));

        Plan plan = LoadPlan(world);
        Assert.Equal(1, plan.Policy.KeepLatestN);
        Assert.Equal(old.ItemId, Assert.Single(plan.Steps).ItemId);
    }

    // ---- Rust: Cargo.lock references and crate-name grouping ---------------------------------

    private const string CargoSrc = "/home/dev/.cargo/registry/src";
    private const string CargoIndex = CargoSrc + "/index.crates.io-1949cf8c6b5b557f";
    private const string CargoProject = "/home/dev/src/rust-app";

    private static World RustWorld()
    {
        World world = EmptyWorld();

        world.Fs
            .AddDirectory($"{CargoIndex}/serde-1.0.150")
            .AddFile($"{CargoIndex}/serde-1.0.150/lib.rs", "// serde")
            .AddDirectory($"{CargoIndex}/serde-1.0.197")
            .AddFile($"{CargoIndex}/serde-1.0.197/lib.rs", "// serde");

        world.Fs
            .AddFile($"{CargoProject}/Cargo.toml", "[package]\nname = \"app\"\n")
            .AddFile(
                $"{CargoProject}/Cargo.lock",
                "version = 3\n\n[[package]]\nname = \"serde\"\nversion = \"1.0.197\"\n");

        return world;
    }

    [Fact]
    public async Task A_cargo_registry_is_scanned_and_Cargo_lock_decides_the_version()
    {
        World world = RustWorld();

        ScanReport report = await Service(world).RunAsync([Root("cargo-registry-src", CargoSrc)], [CargoProject]);

        Assert.Equal(["rust"], report.Ecosystems);
        Assert.Equal(2, report.Items.Count);

        // The directory is `serde-1.0.197`, but the reference resolver and keep-latest both speak of
        // the CRATE `serde`. The application adapter uses the plugin's referenceName fact.
        Item referenced = Assert.Single(report.Items, i => i.Version == "1.0.197");
        Item old = Assert.Single(report.Items, i => i.Version == "1.0.150");

        Assert.Equal("serde", referenced.Name);
        Assert.Equal("Referenced", referenced.Facts["usage"]);
        Assert.Equal("Unreferenced", old.Facts["usage"]);
    }

    [Fact]
    public async Task Keep_latest_groups_rust_versions_by_crate_name()
    {
        World world = RustWorld();

        ScanReport report = await Service(world).RunAsync([Root("cargo-registry-src", CargoSrc)], [CargoProject]);
        Item old = Assert.Single(report.Items, i => i.Version == "1.0.150");

        Assert.Equal(ExitCodes.Success, Planner(world).Run(["--keep-latest", "1"]));

        Assert.Equal(old.ItemId, Assert.Single(LoadPlan(world).Steps).ItemId);
    }

    [Fact]
    public async Task A_rust_path_delete_applies_through_the_gateway()
    {
        World world = RustWorld();

        await Service(world).RunAsync([Root("cargo-registry-src", CargoSrc)], [CargoProject]);
        Assert.Equal(ExitCodes.Success, Planner(world).Run(["--keep-latest", "1"]));
        Plan plan = LoadPlan(world);

        var apply = new PlanApplyService(
            world.Fs,
            world.Runner,
            world.Env,
            world.State,
            new FakeAppendLog(),
            world.Clock);

        PlanApplyOutcome outcome = await apply.ApplyAsync(plan, strict: false, resume: false, confirmed: true);

        Assert.True(outcome.Ran, outcome.Message);
        Assert.Equal(1, outcome.Summary!.Ok);
        Assert.False(world.Fs.DirectoryExists($"{CargoIndex}/serde-1.0.150"));
        Assert.True(world.Fs.DirectoryExists($"{CargoIndex}/serde-1.0.197"));
    }

    // ---- Node: npm-global reference path -----------------------------------------------------

    private const string NpmGlobal = "/usr/lib/node_modules";
    private const string NodeProject = "/home/dev/src/node-app";

    [Fact]
    public async Task An_npm_global_package_is_scanned_and_matched_against_package_lock()
    {
        World world = EmptyWorld();

        world.Fs
            .AddFile($"{NpmGlobal}/typescript/package.json", """{"name":"typescript","version":"5.4.5"}""")
            .AddFile($"{NodeProject}/package.json", """{"name":"app","version":"1.0.0"}""")
            .AddFile(
                $"{NodeProject}/package-lock.json",
                """{"lockfileVersion":3,"packages":{"node_modules/typescript":{"version":"5.4.5"}}}""");

        ScanReport report = await Service(world)
            .RunAsync([Root("npm-global", NpmGlobal, Ports.Risk.Review)], [NodeProject]);

        Assert.Equal(["node"], report.Ecosystems);
        Item typescript = Assert.Single(report.Items);

        Assert.Equal("typescript", typescript.Name);
        Assert.Equal("5.4.5", typescript.Version);
        Assert.Equal("Referenced", typescript.Facts["usage"]);
    }

    // ---- determinism and the no-project-roots floor ------------------------------------------

    [Fact]
    public async Task Two_go_scans_of_the_same_fixture_are_byte_identical()
    {
        World world = GoWorld();

        ScanReport first = await Service(world).RunAsync([Root("go-mod-cache", GoCache)], [GoProject]);
        ScanReport second = await Service(world).RunAsync([Root("go-mod-cache", GoCache)], [GoProject]);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.Items.Select(i => i.ItemId), second.Items.Select(i => i.ItemId));
        Assert.Equal(
            DomainJson.ComputeChecksum(first.Items),
            DomainJson.ComputeChecksum(second.Items));
    }

    [Fact]
    public async Task Without_project_roots_a_go_cache_stays_Unknown_and_plans_nothing()
    {
        World world = GoWorld();

        ScanReport report = await Service(world).RunAsync([Root("go-mod-cache", GoCache)], []);

        Assert.All(report.Items, item =>
        {
            Assert.Equal("Unknown", item.Facts["usage"]);
            Assert.NotEqual(Risk.Safe, item.Risk);
        });

        Assert.Contains(report.OnboardingHints, h => h.Contains("--projects", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Success, Planner(world).Run([]));
        Assert.Empty(world.State.ListKeys("plans/"));
    }

    // ---- registration is data-driven ---------------------------------------------------------

    /// <summary>
    /// A registry entry is all it takes: no ScanService edit, no core switch. The registry is built
    /// with <see cref="EcosystemRegistry.CreateDefault"/> — the SAME factory every production
    /// composition root uses — and every downstream step (doctor, scan wiring, scan, plan preview,
    /// apply binding) receives that one instance. A test-only bypass would not prove the extension
    /// point production needs.
    /// </summary>
    [Fact]
    public async Task A_fifth_ecosystem_scans_and_plans_through_the_production_wiring()
    {
        World world = EmptyWorld();
        world.Fs.AddDirectory("/fake/cache/pkg/1.0.0");
        world.Fs.AddFile("/fake/cache/pkg/1.0.0/payload.bin", "payload");

        var location = new EcosystemLocation(
            "fake",
            "fake-cache",
            "package-cache",
            Coppice.Core.Resolution.ResolveMode.First,
            new Ports.FingerprintSpec(),
            Risk.Safe,
            "user",
            InstallerOwned: false,
            AllowlistPattern: "*/*",
            ConfidencePenalty: 0,
            Sources: [],
            DefaultPaths: new Dictionary<OperatingSystemKind, string> { [Linux] = "/fake/cache" },
            SupportedOperatingSystems: [Linux]);

        var fake = new FakeEcosystem();
        EcosystemRegistry registry = EcosystemRegistry.CreateDefault(
            world.Fs,
            [new EcosystemPlugin("fake", "Fake", [location], fake, fake.Versions)]);

        // Doctor resolves the fifth ecosystem from the same registry production builds.
        var doctor = new DoctorService(world.Fs, world.Runner, world.Env, Linux, registry);
        DoctorReport roots = doctor.Inspect();
        Assert.Contains(roots.Roots, r => r.LocationId == "fake-cache" && r.Root.Validity == RootValidity.Ok);

        // Scan reads exactly the roots doctor produced, with the same registry.
        var scan = new ScanService(world.Fs, world.Runner, world.Env, world.State, world.Clock, registry);
        ScanReport report = await scan.RunAsync(ScanWiring.FromDoctor(roots, registry), []);

        Item item = Assert.Single(report.Items);
        Assert.Equal("fake", item.Ecosystem);
        Assert.Equal("pkg", item.Name);
        Assert.Equal(["fake"], report.Ecosystems);

        // The plan preview sees the fifth ecosystem's ordering through the registry, not a test-only
        // ordering passed beside it.
        SnapshotLoadResult loaded = await new SnapshotStore(world.State, world.Clock).LoadAsync(report.SnapshotId);
        Assert.True(loaded.Ok, loaded.Message);
        Coppice.Core.Domain.Policy planEveryUnreferenced =
            Coppice.Core.Domain.Policy.Default with { PresetName = "fake-test", KeepLatestN = 0 };
        PlanPreview preview = PlanPreviewService.Preview(loaded.Snapshot!, planEveryUnreferenced, registry);
        Assert.Equal(item.ItemId, Assert.Single(preview.Plan!.Steps).ItemId);

        // Apply binding gets the location's allowlist (and installer flag) from that same registry.
        IReadOnlyList<Coppice.Core.Apply.BindingRoot> bindings = ApplyWiring.BindingRoots(roots, registry);
        Assert.Equal("*/*", bindings.Single(b => b.LocationId == "fake-cache").AllowlistPattern);
    }

    private sealed class FakeEcosystem : IEcosystem, IInventoryProvider, IReferenceResolver
    {
        public string Id => "fake";

        public IVersionOrdering Versions { get; } = new FakeOrdering();

        public bool IsPresent(Coppice.Ports.ScanContext ctx) => ctx.Roots.Count > 0;

        public async IAsyncEnumerable<PortableItem> Discover(Coppice.Ports.ScanContext ctx)
        {
            foreach (Ports.ScanRoot root in ctx.Roots)
            {
                foreach (FileEntry package in ctx.FileSystem.EnumerateEntries(root.ResolvedPath, new EnumerationRequest { MaxDepth = 1 }))
                {
                    yield return new PortableItem(
                        "fake-id",
                        "fake",
                        "package",
                        package.Name,
                        "1.0.0",
                        package.Path,
                        Ports.Risk.Safe,
                        new Dictionary<string, string>());
                }
            }

            await Task.CompletedTask;
        }

        public Ports.ReferenceVerdict Resolve(string itemName, string itemVersion, Ports.ProjectSet projects) =>
            new() { Usage = Ports.Usage.Unreferenced, Reason = "fake fixture" };
    }

    private sealed class FakeOrdering : IVersionOrdering
    {
        public VersionComponents? TryParse(string version) => new(1, 0, 0, 0, null, null);

        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
    }
}

using Coppice.Application;
using Coppice.Cli;
using Coppice.Core.Resolution;
using Coppice.Manifests;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using ResolvedVia = Coppice.Core.Domain.ResolvedVia;

namespace Coppice.Tests.Ecosystems;

/// <summary>
/// FR-02/T-051: <c>coppice roots</c> shows every candidate the resolver considered, with its size,
/// and only an Ok, active root can be cleaned.
/// <para>
/// The fixture is deliberately a bad machine: an ambiguous location, a denylisted home directory, an
/// inactive old cache and a missing default all in one registry. Those are the candidates a report
/// that only printed winners would hide, and the ones a plan must never touch.
/// </para>
/// </summary>
public sealed class RootCandidateTests
{
    private const OperatingSystemKind Linux = OperatingSystemKind.Linux;

    private sealed record World(FakeFileSystem Fs, FakeEnvironment Env, FakeProcessRunner Runner, EcosystemRegistry Registry);

    private static World Build()
    {
        var fs = new FakeFileSystem(Linux);

        // Two live sources disagree, and both paths exist: LR-4 ambiguity. Each cache has a
        // version-shaped child so the fingerprint gate sees a cache rather than an empty directory.
        Cache(fs, "/cache/ambig/tool", 1000);
        Cache(fs, "/cache/ambig/env", 2000);

        // A denylisted home directory that a default points at. It is big and it exists, and it must
        // still never be walked to size it.
        fs.AddDirectory("/home/dev");
        fs.AddFileOfSize("/home/dev/private.bin", 4096);

        // An old cache beside the live one: Ok, but not in use.
        Cache(fs, "/cache/new", 512);
        Cache(fs, "/cache/old", 256);

        var env = new FakeEnvironment(Linux)
            .With("AMBIG", "/cache/ambig/env")
            .With("NEW_CACHE", "/cache/new");

        var runner = new FakeProcessRunner().Respond("probe", request =>
        {
            string path = request.Arguments.FirstOrDefault() switch
            {
                "--old" => "/cache/old",
                _ => "/cache/ambig/tool",
            };

            return new ProcessResult { ExitCode = 0, StandardOutput = path, ResolvedFileName = "probe" };
        });

        var locations = new List<EcosystemLocation>
        {
            Location(
                "ambiguous-cache",
                sources:
                [
                    new LocationSource(ResolvedVia.Tool, Run: "probe --ambig", Parse: ManifestParserKind.Line),
                    new LocationSource(ResolvedVia.Env, Variable: "AMBIG"),
                ]),
            Location("denied-cache", defaults: "/home/dev"),
            Location(
                "inactive-cache",
                sources:
                [
                    new LocationSource(ResolvedVia.Env, Variable: "NEW_CACHE"),
                    // The old cache is a tool-reported location that is known but not in use. Distinct
                    // rung from the live env source, because a location holds one answer per rung.
                    new LocationSource(
                        ResolvedVia.Tool,
                        Run: "probe --old",
                        Parse: ManifestParserKind.Line,
                        Inactive: true),
                ]),
            Location("missing-cache", defaults: "/cache/missing"),
        };

        var plugin = new ProbeEcosystem();
        var registry = new EcosystemRegistry(
            [new EcosystemPlugin("probe", "Probe", locations, plugin, plugin.Versions)]);

        return new World(fs, env, runner, registry);
    }

    private static void Cache(FakeFileSystem fs, string path, int sizeInBytes)
    {
        fs.AddDirectory(path + "/pkg/1.0.0");
        fs.AddFileOfSize(path + "/pkg/1.0.0/payload.bin", sizeInBytes);
    }

    private static EcosystemLocation Location(
        string id,
        IReadOnlyList<LocationSource>? sources = null,
        string? defaults = null) =>
        new(
            "probe",
            id,
            "package-cache",
            ResolveMode.First,
            new FingerprintSpec(),
            Risk.Safe,
            "user",
            InstallerOwned: false,
            AllowlistPattern: null,
            ConfidencePenalty: 0,
            Sources: sources ?? [],
            DefaultPaths: defaults is null
                ? new Dictionary<OperatingSystemKind, string>()
                : new Dictionary<OperatingSystemKind, string> { [Linux] = defaults },
            SupportedOperatingSystems: [Linux]);

    [Fact]
    public void Every_candidate_is_reported_with_its_validity_and_a_size()
    {
        World world = Build();
        var doctor = new DoctorService(world.Fs, world.Runner, world.Env, Linux, world.Registry);

        DoctorReport report = doctor.InspectWithSizes();

        // Ambiguous: both live candidates are reported, both flagged, neither cleanable.
        RootReport[] ambiguous = [.. report.Roots.Where(r => r.LocationId == "ambiguous-cache")];
        Assert.Equal(2, ambiguous.Length);
        Assert.All(ambiguous, r =>
        {
            Assert.Equal(RootValidity.Ambiguous, r.Root.Validity);
            Assert.True(r.Ambiguous);
            Assert.False(r.Root.IsCleanable);
        });
        Assert.Equal(new ulong?[] { 1000, 2000 }, ambiguous.Select(r => r.Size).Order());

        // Denied: the home directory is reported, never measured, and its note says why.
        RootReport denied = Assert.Single(report.Roots, r => r.LocationId == "denied-cache");
        Assert.Equal(RootValidity.Denied, denied.Root.Validity);
        Assert.Null(denied.Size);
        Assert.Contains("denied", denied.SizeNote, StringComparison.OrdinalIgnoreCase);

        // Inactive: the old cache is valid and measured, and the role is what keeps it out of a plan.
        RootReport inactive = Assert.Single(
            report.Roots,
            r => r.LocationId == "inactive-cache" && r.Root.Role == RootRole.Inactive);
        Assert.Equal(RootValidity.Ok, inactive.Root.Validity);
        Assert.Equal((ulong?)256, inactive.Size);
        Assert.False(inactive.Root.IsCleanable);

        // Missing: a default that does not exist is a candidate, not silence.
        RootReport missing = Assert.Single(report.Roots, r => r.LocationId == "missing-cache");
        Assert.Equal(RootValidity.NotFound, missing.Root.Validity);
        Assert.Null(missing.Size);
        Assert.Contains("not a directory", missing.SizeNote, StringComparison.OrdinalIgnoreCase);

        // The floor: exactly one candidate on this machine may be cleaned.
        RootReport cleanable = Assert.Single(report.Roots, r => r.Root.IsCleanable);
        Assert.Equal("inactive-cache", cleanable.LocationId);
        Assert.Equal("/cache/new", cleanable.Root.RealPath);
        Assert.Equal(RootRole.Active, cleanable.Root.Role);
    }

    [Fact]
    public void The_roots_table_prints_every_candidate_including_the_rejected_ones()
    {
        World world = Build();
        DoctorReport report = new DoctorService(world.Fs, world.Runner, world.Env, Linux, world.Registry)
            .InspectWithSizes();

        string text = RootsText.ToText(report, world.Env.HomeDirectory);

        foreach (string id in new[] { "ambiguous-cache", "denied-cache", "inactive-cache", "missing-cache" })
        {
            Assert.Contains(id, text, StringComparison.Ordinal);
        }

        Assert.Contains("ambiguous", text, StringComparison.Ordinal);
        Assert.Contains("denied", text, StringComparison.Ordinal);
        Assert.Contains("not-found", text, StringComparison.Ordinal);
        Assert.Contains("inactive", text, StringComparison.Ordinal);
        Assert.Contains("ambiguous: two sources disagree", text, StringComparison.Ordinal);
        Assert.Contains("declared:", text, StringComparison.Ordinal);
        Assert.Contains("real:", text, StringComparison.Ordinal);
        Assert.Contains("SIZE", text, StringComparison.Ordinal);
        Assert.Contains("2.0 KB", text, StringComparison.Ordinal);

        // The count line agrees with the report rather than with the winners.
        Assert.Contains($"{report.Roots.Count} candidate(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_surface_carries_owner_paths_and_sizes()
    {
        World world = Build();
        DoctorReport report = new DoctorService(world.Fs, world.Runner, world.Env, Linux, world.Registry)
            .InspectWithSizes();

        string json = DoctorService.ToJson(report);

        Assert.Contains("\"sizesMeasured\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"ambiguous\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"owner\": \"user\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sizeBytes\": 256", json, StringComparison.Ordinal);
        Assert.Contains("\"sizeNote\": \"not measured: a denied root is never followed\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_does_not_pay_for_sizes()
    {
        World world = Build();
        DoctorReport report = new DoctorService(world.Fs, world.Runner, world.Env, Linux, world.Registry).Inspect();

        Assert.False(report.SizesMeasured);
        Assert.All(report.Roots, r => Assert.Null(r.Size));
    }

    [Fact]
    public void Only_ok_active_roots_can_become_a_scan_root_or_a_plan_binding()
    {
        World world = Build();
        DoctorReport report = new DoctorService(world.Fs, world.Runner, world.Env, Linux, world.Registry)
            .InspectWithSizes();

        IReadOnlyList<Coppice.Ports.ScanRoot> scanRoots = ScanWiring.FromDoctor(report, world.Registry);

        Coppice.Ports.ScanRoot scanned = Assert.Single(scanRoots);
        Assert.Equal("inactive-cache", scanned.LocationId);
        Assert.Equal("/cache/new", scanned.ResolvedPath);

        // No ambiguous, denied, missing or inactive candidate survived the wiring.
        Assert.DoesNotContain(scanRoots, r => r.LocationId is "ambiguous-cache" or "denied-cache" or "missing-cache");
        Assert.DoesNotContain(scanRoots, r => r.Role == Coppice.Ports.RootRole.Inactive);

        // The apply binder is told the same thing: only the active, validated candidate is cleanable.
        IReadOnlyList<Coppice.Core.Apply.BindingRoot> bindings = ApplyWiring.BindingRoots(report, world.Registry);
        Assert.Equal(
            ["inactive-cache"],
            bindings.Where(b => b.Cleanable).Select(b => b.LocationId));
        Assert.All(bindings.Where(b => b.LocationId == "ambiguous-cache"), b => Assert.False(b.Cleanable));
        Assert.False(bindings.Single(b => b.LocationId == "missing-cache").Cleanable);
        Assert.False(bindings.Single(b => b.LocationId == "denied-cache").Cleanable);
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

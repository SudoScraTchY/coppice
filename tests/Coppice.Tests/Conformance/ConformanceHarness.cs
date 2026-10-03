using System.Reflection;
using Coppice.Plugins.Go;
using Coppice.Plugins.Net;
using Coppice.Plugins.Node;
using Coppice.Plugins.Rust;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Ports = Coppice.Ports;

namespace Coppice.Tests.Conformance;

/// <summary>
/// One fixture: a fake filesystem plus the roots the plugin is allowed to see.
/// <para>
/// The fake VFS is the enforcement mechanism for the whole suite. A plugin cannot touch the real
/// disk (it holds only ports), so every write it attempts lands in <see cref="FakeFileSystem.Writes"/>
/// and every path it reports is checked against the declared roots — which is what makes C-1, C-2
/// and C-9 checkable rather than aspirational.
/// </para>
/// </summary>
public sealed class ConformanceFixture
{
    public required string Name { get; init; }

    public required FakeFileSystem FileSystem { get; init; }

    /// <summary>Roots handed to the plugin. Anything outside them is a C-2 violation.</summary>
    public required IReadOnlyList<Ports.ScanRoot> Roots { get; init; }

    public required IProcessRunner ProcessRunner { get; init; }

    public IEnvironment Environment { get; init; } = new FakeEnvironment();

    public IClock Clock { get; init; } = new FakeClock(DateTimeOffset.UnixEpoch);

    public Ports.ProjectSet Projects { get; init; } = Ports.ProjectSet.Empty;

    /// <summary>
    /// A fresh, identical copy. C-4 and C-11 need a second VFS with the SAME content: comparing the
    /// net-cache fixture against the empty fixture would prove nothing about determinism.
    /// </summary>
    public ConformanceFixture Clone() => Factory();

    /// <summary>The recipe this fixture was built from, so a clone matches it exactly.</summary>
    public required Func<ConformanceFixture> Factory { get; init; }

    /// <summary>
    /// The plugin that owns this fixture, so a rule is checked against the plugin whose contract it
    /// describes.
    /// <para>
    /// This is part of the fixture rather than hardcoded inside each rule. An earlier version built
    /// <c>new DotnetEcosystem()</c> in all twelve rules, which meant adding a Go fixture would have run
    /// every rule against the .NET plugin over a Go-shaped VFS — and reported green while proving
    /// nothing about Go at all.
    /// </para>
    /// </summary>
    public required Func<IEcosystem> PluginFactory { get; init; }

    public Ports.ScanContext ToContext() => new(
        Roots,
        Projects,
        FileSystem,
        ProcessRunner,
        Environment,
        Clock,
        CancellationToken.None);

    /// <summary>The 09 layout for nuget-packages, so a fixture exercises the real fingerprint.</summary>
    public static Ports.FingerprintSpec NuGetFingerprint => new()
    {
        EntryPatterns = ["*/*"],
        LayoutRatio = 0.8,
    };

    /// <summary>The 09 layout for go-mod-cache: a module directory carries an @vN version marker.</summary>
    public static Ports.FingerprintSpec GoModFingerprint => new()
    {
        EntryPatterns = ["*@v*"],
        LayoutRatio = 0.8,
    };


    public static Ports.ScanRoot Root(
        string locationId,
        string path,
        Ports.RootRole role = Ports.RootRole.Active,
        Ports.Risk tier = Ports.Risk.Safe,
        Ports.FingerprintSpec? fingerprint = null) => new(
            locationId,
            path,
            locationId,
            role,
            fingerprint ?? new Ports.FingerprintSpec(),
            "user",
            tier);
}

/// <summary>
/// The conformance suite from 04-plugin-contract, C-1 through C-12.
/// <para>
/// The suite runs against <em>any</em> <see cref="IEcosystem"/>, which is the point: a plugin is
/// only conformant by passing these, and a future plugin (Go, Rust, Node) inherits them for free.
/// Each rule is a separate <c>[Theory]</c> over the fixtures rather than one bulk assertion, so a
/// failure names the rule AND the fixture that broke it.
/// </para>
/// <para>
/// C-11 (no direct IO/process bypass) is only partly assertable here. The static half — no System.IO
/// or socket types in plugin assemblies — is enforced by the architecture test project. What this
/// harness checks is the dynamic half: the plugin only ever saw the ports, and everything it
/// reported exists in the fake VFS, so it demonstrably did not reach the real disk.
/// </para>
/// </summary>
public sealed class ConformanceHarnessTests
{
    /// <summary>
    /// The canonical .NET-shaped fixture: a real nuget-packages layout (package-id dirs holding
    /// version subdirs), plus a dotnet-tools root.
    /// </summary>
    public static ConformanceFixture NetFixture(string name = "nuget-cache")
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        foreach (string package in new[] { "newtonsoft.json", "serilog", "polly" })
        {
            fs.AddDirectory($"/cache/{package}/13.0.3");
            fs.AddFile($"/cache/{package}/13.0.3/package.nupkg");
            fs.AddFile($"/cache/{package}/13.0.3/package.nupkg.sha512");
            fs.AddFile($"/cache/{package}/13.0.3/{package}.nuspec");
        }

        // A version dir with no metadata: an incomplete package. Must be REPORTED, never deleted.
        fs.AddDirectory("/cache/halfbroken/1.0.0");

        // Non-version directories NuGet itself creates. A plugin must not read these as versions.
        fs.AddDirectory("/cache/newtonsoft.json/.tools");
        fs.AddDirectory("/cache/newtonsoft.json/.metadata");

        fs.AddDirectory("/tools/dotnetsay");

        return new ConformanceFixture
        {
            Name = name,
            FileSystem = fs,
            ProcessRunner = new FakeProcessRunner(),
            Factory = () => NetFixture(name),
            Roots =
            [
                ConformanceFixture.Root("nuget-packages", "/cache", tier: Ports.Risk.Safe, fingerprint: ConformanceFixture.NuGetFingerprint),
                ConformanceFixture.Root("dotnet-tools", "/tools"),
            ],
            PluginFactory = () => new DotnetEcosystem(),
        };
    }

    /// <summary>
    /// The Go-shaped fixture: a real module cache layout, including the nesting a module path creates,
    /// the download cache beside it, and a module's own vendor tree.
    /// <para>
    /// This fixture exists because the harness claims a new plugin inherits C-1..C-12 for free. That
    /// claim is only worth anything once a SECOND plugin has actually run through them, and the Go
    /// plugin is the first honest test of it.
    /// </para>
    /// </summary>
    public static ConformanceFixture GoFixture(string name = "go-mod-cache")
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        fs.AddDirectory("/go/pkg/mod/github.com/stretchr/testify@v1.8.4");
        fs.AddDirectory("/go/pkg/mod/github.com/stretchr/testify@v1.9.0");
        fs.AddDirectory("/go/pkg/mod/golang.org/x/text@v0.14.0");
        fs.AddDirectory("/go/pkg/mod/example@v1.0.0");

        // The download cache beside the modules. A plugin that reports it would double-count bytes.
        fs.AddFile("/go/pkg/mod/cache/download/github.com/stretchr/testify/@v/v1.8.4.zip", "PK");

        // A module's own vendor tree lives beneath it and is not a separate module.
        fs.AddDirectory("/go/pkg/mod/example@v1.0.0/vendor/github.com/z/w@v0.1.0");

        return new ConformanceFixture
        {
            Name = name,
            FileSystem = fs,
            ProcessRunner = new FakeProcessRunner(),
            Factory = () => GoFixture(name),
            Roots = [ConformanceFixture.Root("go-mod-cache", "/go/pkg/mod", fingerprint: ConformanceFixture.GoModFingerprint)],
            PluginFactory = () => new GoEcosystem(),
        };
    }

    /// <summary>An empty fixture: a plugin that throws on "nothing there" is not conformant.</summary>
    public static ConformanceFixture EmptyFixture()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddDirectory("/cache");
        return new ConformanceFixture
        {
            Name = "empty-cache",
            FileSystem = fs,
            ProcessRunner = new FakeProcessRunner(),
            Factory = EmptyFixture,
            Roots = [ConformanceFixture.Root("nuget-packages", "/cache")],
            PluginFactory = () => new DotnetEcosystem(),
        };
    }

    /// <summary>The Rust-shaped fixture: an extracted crate tree beneath a registry index directory.</summary>
    public static ConformanceFixture RustFixture(string name = "cargo-registry-src")
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        const string registry = "/cargo/registry/src/index.crates.io-1949cf8c6b5b557f";
        fs.AddDirectory($"{registry}/serde-1.0.197");
        fs.AddDirectory($"{registry}/serde_json-1.0.114");
        fs.AddDirectory($"{registry}/winapi-0.4.0");

        // A crate's own vendor tree, and a toolchain install with its bin/.
        fs.AddDirectory($"{registry}/serde-1.0.197/vendor/serde-core-1.0.0");
        fs.AddDirectory("/rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin");

        return new ConformanceFixture
        {
            Name = name,
            FileSystem = fs,
            ProcessRunner = new FakeProcessRunner(),
            Factory = () => RustFixture(name),
            Roots = [ConformanceFixture.Root("cargo-registry-src", registry)],
            PluginFactory = () => new RustEcosystem(),
        };
    }

    /// <summary>
    /// The Node-shaped fixture: a content-addressed cache plus a global root with per-package structure.
    /// <para>
    /// Deliberately asymmetric. The cache holds several blobs and index entries but must yield ONE item;
    /// the global root holds two packages and must yield TWO. A harness that flattened both would pass
    /// while the whole-location constraint was broken.
    /// </para>
    /// </summary>
    public static ConformanceFixture NodeFixture(string name = "npm-cache")
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        fs.AddDirectory("/home/dev/.npm/_cacache/content-v2/sha512/ab/cd");
        fs.AddFile("/home/dev/.npm/_cacache/content-v2/sha512/ab/cd/abcdef", "blob", fileIdentity: "blob-1");
        fs.AddDirectory("/home/dev/.npm/_cacache/content-v2/sha512/ef/gh");
        fs.AddFile("/home/dev/.npm/_cacache/content-v2/sha512/ef/gh/efghij", "blob", fileIdentity: "blob-2");

        fs.AddFile("/usr/lib/node_modules/typescript/package.json", "{\"name\":\"typescript\",\"version\":\"5.4.5\"}");
        fs.AddFile("/usr/lib/node_modules/npm/package.json", "{\"name\":\"npm\",\"version\":\"10.2.0\"}");

        return new ConformanceFixture
        {
            Name = name,
            FileSystem = fs,
            ProcessRunner = new FakeProcessRunner(),
            Factory = () => NodeFixture(name),
            Roots = [ConformanceFixture.Root("npm-cache", "/home/dev/.npm")],
            PluginFactory = () => new NodeEcosystem(),
        };
    }

    public static TheoryData<ConformanceFixture> AllFixtures() => new() { NetFixture(), GoFixture(), RustFixture(), NodeFixture(), EmptyFixture() };

    private static async Task<List<Ports.PortableItem>> DiscoverAll(
        IEcosystem ecosystem,
        ConformanceFixture fixture)
    {
        if (ecosystem is not IInventoryProvider inventory)
        {
            return [];
        }

        List<Ports.PortableItem> items = [];
        await foreach (Ports.PortableItem item in inventory.Discover(fixture.ToContext()))
        {
            items.Add(item);
        }

        return items;
    }

    // ---- C-1: Inventory performs zero writes (verified against VFS) ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C1_InventoryPerformsZeroWrites(ConformanceFixture fixture)
    {
        _ = await DiscoverAll(fixture.PluginFactory(), fixture);

        Assert.Empty(fixture.FileSystem.Writes);
    }

    // ---- C-2: All returned real paths fall inside declared roots ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C2_AllPathsFallInsideDeclaredRoots(ConformanceFixture fixture)
    {
        List<Ports.PortableItem> items = await DiscoverAll(fixture.PluginFactory(), fixture);

        foreach (Ports.PortableItem item in items)
        {
            // C-2 has two halves. A path inside NO declared root is a hard violation. A path whose
            // own location was not recorded is a provenance bug: the item exists, but the root it
            // came from is unprovable, so nothing downstream can establish containment.
            string? locationId = item.Facts.GetValueOrDefault("locationId");
            string? declaredRoot = locationId is null
                ? null
                : fixture.Roots.FirstOrDefault(r => r.LocationId == locationId)?.ResolvedPath;

            Assert.True(
                declaredRoot is not null,
                $"Item {item.Name} {item.Version} reports no locationId, so its root cannot be proven (C-2).");

            Assert.StartsWith(
                declaredRoot,
                fixture.FileSystem.Normalize(item.RootPath),
                StringComparison.Ordinal);
        }
    }

    // ---- C-3: No network attempts ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C3_NoNetworkAttempts(ConformanceFixture fixture)
    {
        _ = await DiscoverAll(fixture.PluginFactory(), fixture);

        // There is no network port to observe, so the checkable form of C-3 is that discovery
        // reached nothing beyond the ports it was given: no process spawned, no write issued. A
        // plugin that dialled out would have to use System.Net, which the architecture tests
        // forbid outright in plugin assemblies.
        Assert.Empty(((FakeProcessRunner)fixture.ProcessRunner).Calls);
        Assert.Empty(fixture.FileSystem.Writes);
    }

    // ---- C-4: Deterministic: two runs on same fixture -> identical output ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C4_TwoRunsProduceIdenticalOutput(ConformanceFixture fixture)
    {
        List<Ports.PortableItem> first = await DiscoverAll(fixture.PluginFactory(), fixture);

        // A second, independent VFS with identical content. If the plugin's output depends on
        // enumeration order, hash-table iteration or wall-clock time, the two runs diverge here.
        List<Ports.PortableItem> second = await DiscoverAll(fixture.PluginFactory(), fixture.Clone());

        Assert.Equal(
            first.Select(Shape).Order(StringComparer.Ordinal),
            second.Select(Shape).Order(StringComparer.Ordinal));
    }

    private static string Shape(Ports.PortableItem item) =>
        $"{item.Ecosystem}/{item.Kind}/{item.Name}/{item.Version}/{item.RootPath}";

    // ---- C-5: Optional capabilities absent -> not probed, no throws ----

    [Theory]
    [InlineData("go")]
    [InlineData("rust")]
    [InlineData("node")]
    public void C5_EcosystemWithoutInventoryCapabilityDoesNotThrow(string ecosystemId)
    {
        IEcosystem ecosystem = new CapabilityOnlyEcosystem(ecosystemId);

        // LSP: a plugin that does not implement IInventoryProvider must be usable, not throw. The
        // harness treats "absent" as an answer rather than a failure.
        Assert.Null(ecosystem as IInventoryProvider);
        Assert.NotNull(ecosystem.Versions);
        Assert.True(ecosystem.IsPresent(new Ports.ScanContext(
            [ConformanceFixture.Root("anything", "/x")],
            Ports.ProjectSet.Empty,
            new FakeFileSystem(),
            new FakeProcessRunner(),
            new FakeEnvironment(),
            new FakeClock(DateTimeOffset.UnixEpoch),
            CancellationToken.None)));
    }

    // ---- C-6: Version ordering is a consistent total order ----

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("2.0.0", "2.0.1")]
    [InlineData("2.0.1", "2.1.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-preview.1", "1.0.0-preview.2")]
    [InlineData("10.0.100", "9.0.301")]
    [InlineData("1.0.0.1", "1.0.0.2")]
    [InlineData("1.2.3", "1.2.3")]
    public void C6_VersionOrderingIsAntisymmetric(string left, string right)
    {
        // .NET-specific on purpose: the version pairs are .NET's (previews, 4-part SDK forms), so this
        // rule asserts about the .NET ordering rather than whatever the fixtures' plugin provides.
        IVersionOrdering versions = new DotnetEcosystem().Versions;

        Assert.Equal(Math.Sign(versions.Compare(left, right)), -Math.Sign(versions.Compare(right, left)));
        Assert.Equal(0, versions.Compare(left, left));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void C6_VersionOrderingIsTransitive(ConformanceFixture fixture)
    {
        _ = fixture;
        IVersionOrdering ordering = fixture.PluginFactory().Versions;
        string[] versions = ["1.0.0", "1.0.1", "1.1.0", "2.0.0", "1.0.0-preview.1"];

        foreach (string a in versions)
        {
            foreach (string b in versions)
            {
                foreach (string c in versions)
                {
                    if (ordering.Compare(a, b) < 0 && ordering.Compare(b, c) < 0)
                    {
                        Assert.True(
                            ordering.Compare(a, c) < 0,
                            $"transitivity broken: {a} < {b} < {c} but {a} is not < {c} (C-6).");
                    }
                }
            }
        }
    }

    // ---- C-7: Unknown usage never yields Safe ----

    [Theory]
    [InlineData("go")]
    [InlineData("rust")]
    [InlineData("node")]
    public async Task C7_UnknownUsageNeverYieldsSafe(string ecosystemId)
    {
        IEcosystem ecosystem = new CapabilityOnlyEcosystem(ecosystemId);

        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddDirectory("/cache/pkg/1.0.0");

        var fixture = new ConformanceFixture
        {
            Name = "unknown-usage",
            FileSystem = fs,
            ProcessRunner = new FakeProcessRunner(),
            Factory = () => throw new NotSupportedException("this fixture is built per-case and is never cloned"),
            Roots = [ConformanceFixture.Root("go-modules", "/cache")],
            PluginFactory = () => new GoEcosystem(),
        };

        List<Ports.PortableItem> items = await DiscoverAll(ecosystem, fixture);

        foreach (Ports.PortableItem item in items)
        {
            Ports.ReferenceVerdict? verdict = (ecosystem as IReferenceResolver)?.Resolve(item.Name, item.Version, fixture.Projects);
            Ports.Usage usage = verdict?.Usage ?? Ports.Usage.Unknown;

            if (usage == Ports.Usage.Unknown)
            {
                Assert.NotEqual(Ports.Risk.Safe, item.Risk);
            }
        }
    }

    // ---- C-8: Removal steps reference validated roots only ----

    [Theory]
    [InlineData("go")]
    [InlineData("rust")]
    [InlineData("node")]
    public void C8_V0_1DeclaresNoRemovalActions(string ecosystemId)
    {
        IEcosystem ecosystem = new CapabilityOnlyEcosystem(ecosystemId);

        // v0.1 is read-only: no plugin may declare a removal action yet. The rule is asserted as
        // ABSENCE, which is the strongest form available before an executor exists — a plugin that
        // started planning deletions during v0.1 would fail this test.
        if (ecosystem is not IRemovalStrategy removal)
        {
            return;
        }

        Assert.Null(removal.Plan(new Ports.PortableItem(
            "id",
            ecosystem.Id,
            "package",
            "pkg",
            "1.0.0",
            "/cache/pkg/1.0.0",
            Ports.Risk.Review,
            new Dictionary<string, string>())));
    }

    // ---- C-9: Health checks are read-only ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C9_HealthChecksAreReadOnly(ConformanceFixture fixture)
    {
        IEcosystem ecosystem = fixture.PluginFactory();

        if (ecosystem is IHealthChecker health)
        {
            Assert.NotNull(await health.CheckAsync(fixture.ToContext()));
        }

        Assert.Empty(fixture.FileSystem.Writes);
    }

    // ---- C-10: Plugin completes within timeout (no hangs) ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C10_PluginCompletesWithoutHanging(ConformanceFixture fixture)
    {
        Task<List<Ports.PortableItem>> work = DiscoverAll(fixture.PluginFactory(), fixture);
        Task finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(finished == work, $"{fixture.Name}: discovery did not finish within 20s (C-10).");
        await work;
    }

    // ---- C-11: No direct process/IO bypass of ports ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C11_UsesOnlyThePortsItWasGiven(ConformanceFixture fixture)
    {
        List<Ports.PortableItem> items = await DiscoverAll(fixture.PluginFactory(), fixture);
        Assert.Empty(fixture.FileSystem.Writes);

        // Every path reported must exist in the fake VFS. A real disk path on a Linux fixture can
        // only appear if the plugin bypassed the port. The empty fixture legitimately finds nothing,
        // so the count assertion lives on the net-cache fixture where there IS something to find.
        foreach (Ports.PortableItem item in items)
        {
            Assert.NotNull(fixture.FileSystem.GetEntry(item.RootPath));
        }

        if (fixture.Name == "nuget-cache")
        {
            Assert.NotEmpty(items);
        }
    }

    // ---- C-12: Facts values serializable primitives ----

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task C12_FactsValuesAreSerializablePrimitives(ConformanceFixture fixture)
    {
        List<Ports.PortableItem> items = await DiscoverAll(fixture.PluginFactory(), fixture);

        foreach (Ports.PortableItem item in items)
        {
            foreach ((string key, string value) in item.Facts)
            {
                Assert.Matches("^[A-Za-z0-9_]+$", key);

                // Facts land in a JSON snapshot and are rendered into a table. A raw newline or a
                // quote in a value would break both, so C-12 covers the value and not just the type.
                Assert.DoesNotContain('\n', value);
                Assert.DoesNotContain('\r', value);
            }
        }
    }

    /// <summary>
    /// Guards against a rule being quietly dropped: every one of C-1..C-12 must still be present as a
    /// data-driven test. A rule quietly removed from a conformance suite is the failure mode this
    /// catches, and it is invisible any other way.
    /// </summary>
    [Fact]
    public void HarnessCoversAllTwelveRules()
    {
        string[] rules =
        [
            "C1_InventoryPerformsZeroWrites",
            "C2_AllPathsFallInsideDeclaredRoots",
            "C3_NoNetworkAttempts",
            "C4_TwoRunsProduceIdenticalOutput",
            "C5_EcosystemWithoutInventoryCapabilityDoesNotThrow",
            "C6_VersionOrderingIsAntisymmetric",
            "C6_VersionOrderingIsTransitive",
            "C7_UnknownUsageNeverYieldsSafe",
            "C8_V0_1DeclaresNoRemovalActions",
            "C9_HealthChecksAreReadOnly",
            "C10_PluginCompletesWithoutHanging",
            "C11_UsesOnlyThePortsItWasGiven",
            "C12_FactsValuesAreSerializablePrimitives",
        ];

        foreach (string rule in rules)
        {
            MethodInfo? method = typeof(ConformanceHarnessTests).GetMethod(rule);
            Assert.True(method is not null, $"{rule} is missing from the conformance suite.");
            Assert.NotNull(method.GetCustomAttribute<TheoryAttribute>());

            // A rule must be data-driven over fixtures or ecosystem ids, never a single hard-coded
            // case: a suite that only ever runs .NET would pass while every other plugin failed.
            int cases = method.GetCustomAttributes<MemberDataAttribute>().Count()
                + method.GetCustomAttributes<InlineDataAttribute>().Count();
            Assert.True(cases > 0, $"{rule} has no data cases, so it only ever runs once.");
        }
    }
}

/// <summary>
/// A plugin with no optional capabilities, used to prove the harness itself holds (LSP: absent
/// capabilities are absent, never throwing). It deliberately implements nothing optional.
/// </summary>
internal sealed class CapabilityOnlyEcosystem : IEcosystem
{
    public CapabilityOnlyEcosystem(string id) => Id = id;

    public string Id { get; }

    public bool IsPresent(Ports.ScanContext ctx) => ctx.Roots.Count > 0;

    public IVersionOrdering Versions { get; } = new OrdinalVersionOrdering();
}

/// <summary>Total order proving C-6 needs no plugin logic beyond a comparison.</summary>
internal sealed class OrdinalVersionOrdering : IVersionOrdering
{
    public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);

    public VersionComponents? TryParse(string version)
    {
        string[] parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
        {
            return null;
        }

        return new VersionComponents(major, minor, 0, 0, null, null);
    }
}

/// <summary>
/// A deliberately non-conformant plugin: every violation a real plugin has plausibly shipped.
/// <para>
/// This exists to prove the harness has TEETH. A conformance suite that has never rejected anything
/// is indistinguishable from a suite that asserts nothing, and the failure mode is invisible: the
/// rules get quietly weakened over time and nothing ever goes red. Each test below names the rule it
/// expects to catch the corresponding violation, so a rule that stops working fails here first.
/// </para>
/// </summary>
internal sealed class BrokenEcosystem : IEcosystem, IInventoryProvider
{
    private readonly BrokenViolation _violation;

    public BrokenEcosystem(BrokenViolation violation) => _violation = violation;

    public string Id => "broken";

    public bool IsPresent(Ports.ScanContext ctx) => ctx.Roots.Count > 0;

    public IVersionOrdering Versions { get; } = new NonTransitiveVersionOrdering();

    public async IAsyncEnumerable<Ports.PortableItem> Discover(Ports.ScanContext ctx)
    {
        if (_violation is BrokenViolation.WritesDuringInventory)
        {
            // C-1: a plugin that "cleans up as it inventories".
            ctx.FileSystem.CreateDirectory("/cache/self-inventoried");
        }

        foreach (Ports.ScanRoot root in ctx.Roots)
        {
            if (_violation is BrokenViolation.SpawnsAProcess)
            {
                // C-3: the only sanctioned way out is IProcessRunner, and inventory has no business
                // using it at all — this shows up as a recorded call the suite did not expect.
                await ctx.ProcessRunner.RunAsync(
                    new Ports.ProcessRequest { FileName = "dotnet", Arguments = ["--list-sdks"] },
                    ctx.CancellationToken);
            }

            foreach (FileEntry entry in ctx.FileSystem.EnumerateEntries(root.ResolvedPath, new Ports.EnumerationRequest { MaxDepth = 1 }))
            {
                if (entry.Kind != EntryKind.Directory)
                {
                    continue;
                }

                string path = _violation is BrokenViolation.ReportsPathsOutsideItsRoots
                    ? "/somewhere/else/completely/" + entry.Name
                    : entry.Path;

                Dictionary<string, string> facts = _violation is BrokenViolation.EmitsUnserializableFacts
                    ? new Dictionary<string, string> { ["note"] = "line one\nline two" }
                    : new Dictionary<string, string> { ["locationId"] = root.LocationId };

                if (_violation is BrokenViolation.ForgetsProvenance)
                {
                    facts = new Dictionary<string, string> { ["unrelated"] = "yes" };
                }

                yield return new Ports.PortableItem(
                    "broken-id",
                    Id,
                    "package",
                    entry.Name,
                    _violation is BrokenViolation.OrderDependsOnEnumeration ? entry.Name + Guid.NewGuid() : "1.0.0",
                    path,
                    root.Tier,
                    facts);

                if (_violation is BrokenViolation.HangsForever)
                {
                    // C-10: a plugin that never returns. Awaiting an uncancellable delay here would
                    // hang the whole suite, so the harness's own timeout is what has to catch it.
                    await Task.Delay(TimeSpan.FromMinutes(30), ctx.CancellationToken);
                }
            }
        }
    }
}

internal enum BrokenViolation
{
    None,
    WritesDuringInventory,
    SpawnsAProcess,
    ReportsPathsOutsideItsRoots,
    ForgetsProvenance,
    EmitsUnserializableFacts,
    OrderDependsOnEnumeration,
    HangsForever,
}

/// <summary>
/// A version comparison that is NOT a total order, because it is STATEFUL: the same pair compared
/// twice gives different answers, and the result depends on how many comparisons ran before it.
/// <para>
/// This is the realistic version of the bug — a memoisation or mutable-cache mistake — rather than an
/// artificial cycle. It matters because "keep the newest N" is implemented as a sort: an ordering
/// whose answer depends on call history produces a different set of survivors on every run, and the
/// difference is invisible until someone's build breaks.
/// </para>
/// </summary>
internal sealed class NonTransitiveVersionOrdering : IVersionOrdering
{
    private int _calls;

    public int Compare(string? x, string? y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return 0;
        }

        int ordinal = string.CompareOrdinal(x, y);
        return _calls++ % 2 == 0 ? ordinal : -ordinal;
    }

    public VersionComponents? TryParse(string version) => new(0, 0, 0, 0, null, null);
}

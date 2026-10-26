using Coppice.Application;
using Coppice.Core.Config;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Manifests;

/// <summary>
/// FR-23 / T-057: a manifest dropped into the user ecosystem directory is opt-in data, not code. When
/// trusted it is validated, registered, and scanned through the same registry as the built-ins; when
/// disabled or invalid it changes nothing.
/// </summary>
public sealed class UserManifestTests
{
    private const string Root = "/home/dev/.custom-cache";
    private const string ManifestDirectory = "/home/dev/.config/coppice/ecosystems";

    private const string Manifest = """
        schema = 1
        id = "custom"
        display_name = "Custom"

        [detection]
        marker = "{home}/.custom-cache/ready"

        [[location]]
        id = "custom-cache"
        kind = "package-cache"
        layout = "{name}/{version}"
        resolve = "first"
        sources = [
          { via = "default", path = "{home}/.custom-cache" },
        ]
        fingerprint = { required_markers = ["pkg-a"] }
        risk_ceiling = "review"
        """;

    [Fact]
    public void The_ecosystem_config_is_opt_in()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            version = 1

            [ecosystems]
            trust_user_manifests = true
            directory = "/tmp/ecosystems"
            """);

        Assert.True(result.IsSuccess);
        Assert.True(result.Config!.Ecosystems.TrustUserManifests);
        Assert.Equal("/tmp/ecosystems", result.Config.Ecosystems.Directory);
    }

    [Fact]
    public async Task A_trusted_drop_in_manifest_is_registered_and_scanned_without_a_rebuild()
    {
        FakeFileSystem fs = Fixture(Manifest);
        ConfigLoadResult config = ConfigLoader.Load("""
            version = 1

            [ecosystems]
            trust_user_manifests = true
            """);

        EcosystemRegistry registry = EcosystemRegistry.CreateForConfig(
            fs,
            new FakeEnvironment(OperatingSystemKind.Linux),
            config.Config!);

        Assert.NotNull(registry.FindEcosystem("custom"));
        Assert.Empty(registry.ManifestErrors);

        var doctor = new DoctorService(
            fs,
            new FakeProcessRunner(),
            new FakeEnvironment(OperatingSystemKind.Linux),
            OperatingSystemKind.Linux,
            registry);

        DoctorReport report = doctor.Inspect();
        Assert.Contains(report.Roots, root => root.LocationId == "custom-cache" && root.Root.Validity == RootValidity.Ok);

        var service = new ScanService(
            fs,
            new FakeProcessRunner(),
            new FakeEnvironment(OperatingSystemKind.Linux),
            new FakeStateStore(),
            new FakeClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)),
            registry);

        ScanReport scan = await service.RunAsync(
            ScanWiring.FromDoctor(report, registry),
            [],
            CancellationToken.None,
            report.Detection);

        Assert.Equal(2, scan.Items.Count);
        Assert.All(scan.Items, item => Assert.Equal("custom", item.Ecosystem));
        Assert.All(scan.Items, item => Assert.Equal(Coppice.Core.Domain.Risk.Review, item.Risk));
    }

    [Fact]
    public void The_default_is_disabled_even_when_a_manifest_exists()
    {
        FakeFileSystem fs = Fixture(Manifest);

        EcosystemRegistry registry = EcosystemRegistry.CreateForConfig(
            fs,
            new FakeEnvironment(OperatingSystemKind.Linux),
            CoppiceConfig.Defaults);

        Assert.Null(registry.FindEcosystem("custom"));
        Assert.Empty(registry.ManifestErrors);
    }

    [Fact]
    public void An_invalid_drop_in_is_refused_with_diagnostics_not_silently_ignored()
    {
        FakeFileSystem fs = Fixture("""
            schema = 999
            id = "bad"
            display_name = "Bad"

            [[location]]
            id = "bad-cache"
            kind = "package-cache"
            layout = "{name}"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp/bad" }]
            """);

        ConfigLoadResult config = ConfigLoader.Load("""
            version = 1

            [ecosystems]
            trust_user_manifests = true
            """);

        EcosystemRegistry registry = EcosystemRegistry.CreateForConfig(
            fs,
            new FakeEnvironment(OperatingSystemKind.Linux),
            config.Config!);

        Assert.Null(registry.FindEcosystem("bad"));
        Assert.NotEmpty(registry.ManifestErrors);
        Assert.Contains(registry.ManifestErrors, error => error.Contains("schema", StringComparison.OrdinalIgnoreCase));
    }

    private static FakeFileSystem Fixture(string manifest)
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile($"{ManifestDirectory}/custom.toml", manifest);
        fs.AddDirectory(Root);
        fs.AddFile($"{Root}/index", string.Empty);
        fs.AddFile($"{Root}/ready", string.Empty);
        fs.AddDirectory($"{Root}/pkg-a/1.0.0");
        fs.AddDirectory($"{Root}/pkg-a/2.0.0");
        return fs;
    }
}

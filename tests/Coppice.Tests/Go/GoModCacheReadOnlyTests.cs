using Coppice.Manifests;
using Xunit;
using GoPlugin = Coppice.Plugins.Go.GoEcosystem;
using Support = Coppice.Tests.Support;

namespace Coppice.Tests.Go;

/// <summary>
/// T-022 acceptance detail: Go sets module-cache files read-only BY DESIGN, so a mod cache is the case
/// where a path delete is the wrong route (09-ecosystems, go section).
/// <para>
/// These tests pin where the native route comes from. It is declared in <c>go.toml</c> and carried
/// through the manifest, because v0.3 reads it there — a plugin-invented command would be a plugin that
/// can run anything.
/// </para>
/// </summary>
public class GoModCacheReadOnlyTests
{
    private const string GoTomlWithNativeRoute = """
        schema = 1
        id = "go"
        [[location]]
        id = "go-mod-cache"
        kind = "package-cache"
        resolve = "first"
        sources = [{ via = "tool", run = "go env GOMODCACHE", parse = "label-value" }]
        fingerprint = { layout_ratio = 0.8, entry_patterns = ["*@v*"] }
        remove = { command = "go clean -modcache" }
        """;

    [Fact]
    public async Task A_mod_cache_of_read_only_modules_is_discovered_without_any_write()
    {
        var fs = new Support.FakeFileSystem(Coppice.Ports.OperatingSystemKind.Linux);
        fs.AddDirectory("/go/pkg/mod/github.com/x/y@v1.0.0");
        fs.AddFile("/go/pkg/mod/github.com/x/y@v1.0.0/go.mod", "module github.com/x/y\n", readOnly: true);
        fs.AddFile("/go/pkg/mod/github.com/x/y@v1.0.0/y.go", "package y\n", readOnly: true);

        var ctx = new Coppice.Ports.ScanContext(
            [Conformance.ConformanceFixture.Root("go-mod-cache", "/go/pkg/mod")],
            Coppice.Ports.ProjectSet.Empty,
            fs,
            new Support.FakeProcessRunner(),
            new Support.FakeEnvironment(),
            new Support.FakeClock(DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        var items = new List<Coppice.Ports.PortableItem>();
        await foreach (Coppice.Ports.PortableItem item in new GoPlugin(fs).Discover(ctx))
        {
            items.Add(item);
        }

        Assert.Equal("github.com/x/y", Assert.Single(items).Name);

        // The read-only attribute is what a path delete trips over, so the read path must never touch it.
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void The_plugin_plans_no_removal_and_invents_no_command()
    {
        GoPlugin plugin = new(new Support.FakeFileSystem(Coppice.Ports.OperatingSystemKind.Linux));

        Assert.Null(plugin.Plan(new Coppice.Ports.PortableItem(
            "id",
            "go",
            "module",
            "github.com/x/y",
            "v1.0.0",
            "/go/pkg/mod/github.com/x/y@v1.0.0",
            Coppice.Ports.Risk.Safe,
            new Dictionary<string, string>())));
    }

    [Fact]
    public void The_manifest_carries_the_native_route_for_the_mod_cache()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            GoTomlWithNativeRoute,
            ManifestOrigin.User,
            "go.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));

        Coppice.Manifests.LocationRecord location = Assert.Single(result.Locations);
        Assert.Equal("go clean -modcache", location.RemoveCommand);

        // The fingerprint is what makes the location eligible for delete rights at all (06-safety-model),
        // so the native route and the fingerprint have to arrive together.
        Assert.True(location.Fingerprint.LayoutRatio > 0);
        Assert.Equal(["*@v*"], location.Fingerprint.EntryPatterns);
    }

    [Fact]
    public void A_manifest_that_pairs_the_native_route_with_no_fingerprint_is_still_loadable_but_unprivileged()
    {
        // The validator does not refuse this, and should not: a location with no fingerprint simply never
        // earns delete rights. The test documents that so the interaction is intentional rather than
        // accidental — if someone later adds a rule here, this test says which behaviour it replaced.
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            """
            schema = 1
            id = "go"
            [[location]]
            id = "go-mod-cache"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/go/pkg/mod" }]
            remove = { command = "go clean -modcache" }
            """,
            ManifestOrigin.User,
            "go.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
        Assert.Equal(0, Assert.Single(result.Locations).Fingerprint.LayoutRatio);
    }
}

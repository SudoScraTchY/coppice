using Xunit;

namespace Coppice.Tests.Go;

using Coppice.Ports;
using Fixture = Coppice.Tests.Conformance.ConformanceFixture;
using Go = Coppice.Plugins.Go.GoEcosystem;
using GoVersionOrdering = Coppice.Plugins.Go.GoVersionOrdering;
using Support = Coppice.Tests.Support;

/// <summary>
/// T-022: the Go ecosystem plugin (FR-23, 09-ecosystems "go").
/// <para>
/// Three properties carry the weight here: the module layout is read correctly including Go's
/// <c>!</c> escaping, reference resolution is three-state, and a mod cache's read-only files make the
/// NATIVE route the default rather than a path delete.
/// </para>
/// </summary>
public class GoEcosystemTests
{
    private static Support.FakeFileSystem ModCache(params string[] moduleVersionDirs)
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);

        foreach (string dir in moduleVersionDirs)
        {
            fs.AddDirectory($"/go/pkg/mod/{dir}");
        }

        return fs;
    }

    /// <summary>
    /// Builds a real <see cref="ScanContext"/> through the conformance fixture's own helper, so these
    /// tests cannot drift from the contract shape. An earlier draft hand-constructed ScanRoot with the
    /// wrong argument order and order of magnitude — a compile error, luckily, but a silent version would
    /// have tested a root the scanner never produces.
    /// </summary>
    private static ScanContext ContextFor(
        Support.FakeFileSystem fs,
        string locationId = "go-mod-cache",
        string path = "/go/pkg/mod") =>
        new(
            [Fixture.Root(locationId, path)],
            ProjectSet.Empty,
            fs,
            new Support.FakeProcessRunner(),
            new Support.FakeEnvironment(),
            new Support.FakeClock(DateTimeOffset.UnixEpoch),
            CancellationToken.None);

    private static async Task<List<PortableItem>> DiscoverAsync(Go plugin, Support.FakeFileSystem fs) =>
        await Collect(plugin, ContextFor(fs));

    private static async Task<List<PortableItem>> Collect(Go plugin, ScanContext ctx)
    {
        var items = new List<PortableItem>();

        await foreach (PortableItem item in plugin.Discover(ctx))
        {
            items.Add(item);
        }

        return items;
    }

    // ---- layout ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Modules_are_discovered_at_module_at_version()
    {
        Support.FakeFileSystem fs = ModCache(
            "github.com/stretchr/testify@v1.8.4",
            "golang.org/x/text@v0.14.0");

        List<PortableItem> items = await DiscoverAsync(new Go(fs), fs);

        Assert.Equal(2, items.Count);

        PortableItem testify = Assert.Single(items, i => i.Name == "github.com/stretchr/testify");
        Assert.Equal("v1.8.4", testify.Version);
        Assert.Equal("/go/pkg/mod/github.com/stretchr/testify@v1.8.4", testify.RootPath);
        Assert.Equal("module", testify.Kind);
        Assert.Equal("go", testify.Ecosystem);
    }

    [Fact]
    public async Task An_escaped_module_name_is_the_module_name()
    {
        // Go escapes uppercase letters in module paths as !lowercase, because the module cache has to
        // work on case-insensitive filesystems. The DIRECTORY is the escaped form; the module IS the
        // escaped form too, and reporting it unescaped would name a path that does not exist on disk.
        Support.FakeFileSystem fs = ModCache("github.com/!azure/!s!d!k@v1.2.3");

        List<PortableItem> items = await DiscoverAsync(new Go(fs), fs);

        PortableItem module = Assert.Single(items);
        Assert.Equal("github.com/!azure/!s!d!k", module.Name);
        Assert.Equal("v1.2.3", module.Version);
    }

    [Fact]
    public async Task A_module_path_three_segments_deep_is_found()
    {
        // REGRESSION. The first implementation walked two levels, copying the .NET plugin's
        // package-id/version shape. A Go module lives at `host/org/name@vX.Y.Z` — three levels — so
        // that walk found nothing and reported a Go cache with zero modules. That looks exactly like a
        // machine that has never run `go get`, so nothing downstream would have questioned it.
        Support.FakeFileSystem fs = ModCache("github.com/stretchr/testify@v1.8.4");

        List<PortableItem> items = await DiscoverAsync(new Go(fs), fs);

        Assert.Single(items);
        Assert.Equal("github.com/stretchr/testify", items[0].Name);
    }

    [Fact]
    public async Task A_single_segment_module_path_is_also_found()
    {
        // `golang.org/x/text@v0.14.0` is three segments, but a bare `example@v1.0.0` is one. The walk
        // must not assume a shape.
        Support.FakeFileSystem fs = ModCache("example@v1.0.0");

        List<PortableItem> items = await DiscoverAsync(new Go(fs), fs);

        Assert.Equal("example", Assert.Single(items).Name);
    }

    [Fact]
    public async Task A_modules_own_subdirectories_are_not_reported_as_separate_modules()
    {
        // Go writes vendor/ and internal test packages BENEATH a module directory. Those belong to the
        // module; reporting them as their own items would double-count the module's bytes.
        Support.FakeFileSystem fs = ModCache(
            "github.com/x/y@v1.0.0",
            "github.com/x/y@v1.0.0/vendor/github.com/z/w@v0.1.0");

        List<PortableItem> items = await DiscoverAsync(new Go(fs), fs);

        Assert.Equal("github.com/x/y", Assert.Single(items).Name);
    }

    [Fact]
    public async Task The_download_cache_beside_the_modules_is_not_reported()
    {
        // cache/download holds the .zip and .info archives. Reporting both would count every module's
        // bytes twice and offer two "items" that are really one.
        Support.FakeFileSystem fs = ModCache(
            "github.com/stretchr/testify@v1.8.4",
            "cache/download/github.com/stretchr/testify/@v/v1.8.4.zip");

        List<PortableItem> items = await DiscoverAsync(new Go(fs), fs);

        Assert.Equal("github.com/stretchr/testify", Assert.Single(items).Name);
    }

    [Theory]
    // A module directory REQUIRES the @v marker: a bare path is a parent segment of the module path,
    // not a module. Without a version there is nothing to compare or propose for removal.
    [InlineData("github.com/foo/bar@v1.0.0", "v1.0.0", true)]
    [InlineData("github.com/foo/bar@v1.0.0-beta.1", "v1.0.0-beta.1", true)]
    [InlineData("github.com/foo/bar@v2.0.0+incompatible", "v2.0.0+incompatible", true)]
    // A version without Go's 'v' prefix is not a released module.
    // A branch checkout (@master) is not a version.
    [InlineData("github.com/foo/bar@master", "", false)]
    // No version marker at all.
    [InlineData("github.com/foo/bar", "", false)]
    public void The_version_marker_is_required_and_must_be_go_shaped(string directory, string version, bool expected)
    {
        bool parsed = Go.TrySplitModuleVersion(directory, out string module, out string actualVersion);

        Assert.Equal(expected, parsed);

        if (expected)
        {
            Assert.Equal("github.com/foo/bar", module);
            Assert.Equal(version, actualVersion);
        }
    }

    [Fact]
    public void A_vanity_import_path_containing_an_at_sign_splits_at_the_last_one()
    {
        // "gopkg.in/yaml.v2@v2.4.0" and "example.com/user@v1" — the LAST '@' is the separator, because
        // a version can never contain one and a vanity path can.
        Assert.True(Go.TrySplitModuleVersion("gopkg.in/yaml.v2@v2.4.0", out string module, out string version));
        Assert.Equal("gopkg.in/yaml.v2", module);
        Assert.Equal("v2.4.0", version);

        Assert.True(Go.TrySplitModuleVersion("example.com/user@v1.0.0", out string vanity, out string vanityVersion));
        Assert.Equal("example.com/user", vanity);
        Assert.Equal("v1.0.0", vanityVersion);
    }

    [Fact]
    public async Task Discovery_writes_nothing()
    {
        Support.FakeFileSystem fs = ModCache("github.com/stretchr/testify@v1.8.4");

        await DiscoverAsync(new Go(fs), fs);

        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Discovery_is_deterministic_across_runs()
    {
        Support.FakeFileSystem fs = ModCache(
            "github.com/a/one@v1.0.0",
            "github.com/b/two@v2.0.0",
            "github.com/c/three@v3.0.0");

        List<PortableItem> first = await DiscoverAsync(new Go(fs), fs);
        List<PortableItem> second = await DiscoverAsync(new Go(fs), fs);

        Assert.Equal(first.Select(i => i.ItemId), second.Select(i => i.ItemId));
    }

    [Fact]
    public async Task The_item_id_is_stable_for_the_same_module_and_version()
    {
        // NFR-06: the id is what two scans diff against, so it must not depend on enumeration order or
        // on the path the cache happens to live at.
        Support.FakeFileSystem one = ModCache("github.com/stretchr/testify@v1.8.4");
        Support.FakeFileSystem two = ModCache("github.com/stretchr/testify@v1.8.4");

        List<PortableItem> a = await DiscoverAsync(new Go(one), one);
        List<PortableItem> b = await DiscoverAsync(new Go(two), two);

        Assert.Equal(Assert.Single(a).ItemId, Assert.Single(b).ItemId);
    }

    [Fact]
    public async Task An_empty_or_missing_cache_yields_nothing_and_does_not_throw()
    {
        var empty = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        empty.AddDirectory("/go/pkg/mod");

        Assert.Empty(await DiscoverAsync(new Go(empty), empty));

        // A root that does not exist at all is a normal answer, not an error: a machine with Go
        // configured but never used has an empty cache, not a missing one.
        var missing = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        Assert.Empty(await DiscoverAsync(new Go(missing), missing));
    }

    [Fact]
    public void Go_is_absent_when_the_scan_resolved_no_go_locations()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        Assert.False(new Go(fs).IsPresent(ContextFor(fs, "nuget-packages", "/cache")));
    }

    [Fact]
    public void Go_is_present_when_a_go_location_resolved()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        Assert.True(new Go(fs).IsPresent(ContextFor(fs)));
    }

    [Fact]
    public void v0_1_never_plans_a_removal()
    {
        Support.FakeFileSystem fs = ModCache("github.com/x/y@v1.0.0");
        Assert.Null(new Go(fs).Plan(new PortableItem("id", "go", "module", "x", "v1", "/p", Risk.Safe, new Dictionary<string, string>())));
    }

    // ---- references: three states, from go.sum ---------------------------------------------------------

    /// <summary>
    /// Builds a plugin and a project set over the SAME fake filesystem.
    /// <para>
    /// The plugin reads through the port it was constructed with, so a helper that wrote the go.sum
    /// into a throwaway filesystem and returned only the project set would leave the plugin looking at
    /// a machine with no go.sum at all. The first draft did exactly that, and every reference test came
    /// back Unknown — correctly, from the plugin's point of view.
    /// </para>
    /// </summary>
    private static (Go Plugin, ProjectSet Projects) WithGoSum(string root, params (string Module, string Version)[] entries)
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);

        string content = string.Join(
            "\n",
            entries.Select(e => $"{e.Module} {e.Version} h1:abcdefghijklmnopq="));

        fs.AddFile($"{root}/go.sum", content + "\n");

        return (new Go(fs), new ProjectSet([new Project(root, ["go.mod"], ["go"])], ["go"], []));
    }

    [Fact]
    public void A_module_listed_in_go_sum_is_Referenced()
    {
        (Go plugin, ProjectSet projects) =
            WithGoSum("/src/app", ("github.com/stretchr/testify", "v1.8.4"));

        Ports.ReferenceVerdict verdict = plugin.Resolve("github.com/stretchr/testify", "v1.8.4", projects);

        Assert.Equal(Usage.Referenced, verdict.Usage);
    }

    [Fact]
    public void A_module_absent_from_go_sum_is_Unreferenced_when_every_project_read()
    {
        (Go plugin, ProjectSet projects) =
            WithGoSum("/src/app", ("github.com/stretchr/testify", "v1.8.4"));

        Ports.ReferenceVerdict verdict = plugin.Resolve("github.com/gone/away", "v1.0.0", projects);

        Assert.Equal(Usage.Unreferenced, verdict.Usage);
        Assert.Contains("go.sum", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cached_version_below_the_selected_one_is_Unreferenced()
    {
        // go.sum carries v1.8.4; the cache holds v1.7.0. Minimal version selection builds the HIGHEST
        // requirement, so v1.8.4 is what gets compiled and the cached v1.7.0 is the copy nothing uses.
        //
        // This is the assertion that a module cache can shrink at all. The first implementation
        // declared the older version Referenced because go.sum mentioned something newer, which is the
        // exact inversion — and it made every superseded module in every cache permanently unreclaimable.
        (Go plugin, ProjectSet projects) =
            WithGoSum("/src/app", ("github.com/stretchr/testify", "v1.8.4"));

        Ports.ReferenceVerdict verdict = plugin.Resolve("github.com/stretchr/testify", "v1.7.0", projects);

        Assert.Equal(Usage.Unreferenced, verdict.Usage);
    }

    [Fact]
    public void The_selected_version_is_Referenced_while_the_older_one_is_not()
    {
        // Both directions at once, over one go.sum. Asserting them separately let the inverted rule pass
        // one test at a time; asserting the PAIR is what pins the actual relationship.
        (Go plugin, ProjectSet projects) =
            WithGoSum("/src/app", ("github.com/stretchr/testify", "v1.8.4"));

        Assert.Equal(Usage.Referenced, plugin.Resolve("github.com/stretchr/testify", "v1.8.4", projects).Usage);
        Assert.Equal(Usage.Unreferenced, plugin.Resolve("github.com/stretchr/testify", "v1.7.0", projects).Usage);
        Assert.Equal(Usage.Unreferenced, plugin.Resolve("github.com/stretchr/testify", "v1.9.0", projects).Usage);
    }

    [Fact]
    public void A_go_mod_only_line_does_NOT_count_as_evidence_that_source_is_needed()
    {
        // go.sum carries TWO lines per version: the CONTENT hash, for the version the build selected,
        // and the "/go.mod" hash, for EVERY version in the module graph. The second means the graph
        // mentions this version — not that its source was ever fetched. Counting it as a reference
        // would make every version in the graph look like it needs its module contents on disk.
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/src/app/go.sum", "github.com/x/y v1.0.0/go.mod h1:abc=\n");

        Ports.ReferenceVerdict verdict = new Go(fs).Resolve("github.com/x/y", "v1.0.0", new ProjectSet(
            [new Project("/src/app", ["go.mod"], ["go"])], ["go"], []));

        Assert.Equal(Usage.Unreferenced, verdict.Usage);
    }

    [Fact]
    public void Both_line_kinds_together_still_resolve_to_Referenced()
    {
        // The realistic go.sum: a content line for the selected version plus go.mod lines for the rest
        // of the graph. The selected version must come back Referenced.
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile(
            "/src/app/go.sum",
            "github.com/x/y v1.0.0 h1:content=\ngithub.com/x/y v1.0.0/go.mod h1:mod=\ngithub.com/x/y v1.1.0/go.mod h1:mod2=\n");

        Ports.ReferenceVerdict verdict = new Go(fs).Resolve("github.com/x/y", "v1.0.0", new ProjectSet(
            [new Project("/src/app", ["go.mod"], ["go"])], ["go"], []));

        Assert.Equal(Usage.Referenced, verdict.Usage);
    }

    [Fact]
    public void Zero_projects_is_Unknown_never_Unreferenced()
    {
        // The single most dangerous thing this tool could do is tell a user who never configured
        // project roots that their entire module cache is unused.
        Ports.ReferenceVerdict verdict = new Go(new Support.FakeFileSystem(OperatingSystemKind.Linux))
            .Resolve("github.com/x/y", "v1.0.0", ProjectSet.Empty);

        Assert.Equal(Usage.Unknown, verdict.Usage);
        Assert.Contains("no projects", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_project_with_no_go_sum_is_Unknown_not_Unreferenced()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/src/app/go.mod", "module app\n");

        Ports.ReferenceVerdict verdict = new Go(fs).Resolve(
            "github.com/x/y", "v1.0.0",
            new ProjectSet([new Project("/src/app", ["go.mod"], ["go"])], ["go"], []));

        Assert.Equal(Usage.Unknown, verdict.Usage);
        Assert.Contains("go.sum", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_unreadable_project_makes_every_module_Unknown_even_when_others_read()
    {
        // Partial evidence does not prove absence: the unreadable project could require anything.
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/src/good/go.sum", "github.com/x/y v1.0.0 h1:abc=\n");
        fs.AddFile("/src/bad/go.mod", "module bad\n");

        ProjectSet projects = new(
            [new Project("/src/good", ["go.sum"], ["go"]), new Project("/src/bad", ["go.mod"], ["go"])],
            ["go"],
            []);

        Ports.ReferenceVerdict verdict = new Go(fs).Resolve("github.com/other/module", "v1.0.0", projects);

        Assert.Equal(Usage.Unknown, verdict.Usage);
        Assert.Contains("1 project", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_filesystem_port_means_Unknown_not_Unreferenced()
    {
        Ports.ReferenceVerdict verdict = new Go().Resolve(
            "github.com/x/y", "v1.0.0",
            new ProjectSet([new Project("/src/app", ["go.mod"], ["go"])], ["go"], []));

        Assert.Equal(Usage.Unknown, verdict.Usage);
    }

    [Fact]
    public void An_empty_go_sum_is_readable_and_proves_nothing_is_required()
    {
        // Distinguish "the file says this project needs nothing" (Unreferenced) from "there was no
        // file" (Unknown). A present-but-empty go.sum is the former.
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/src/app/go.sum", "\n");

        Ports.ReferenceVerdict verdict = new Go(fs).Resolve(
            "github.com/x/y", "v1.0.0",
            new ProjectSet([new Project("/src/app", ["go.sum"], ["go"])], ["go"], []));

        Assert.Equal(Usage.Unreferenced, verdict.Usage);
    }

    // ---- version ordering ------------------------------------------------------------------------------

    [Theory]
    [InlineData("v1.0.0", "v1.0.1", -1)]
    [InlineData("v1.0.0", "v1.1.0", -1)]
    [InlineData("v1.9.0", "v1.10.0", -1)]  // numeric, not lexical: "9" > "10" as strings
    [InlineData("v1.0.0", "v2.0.0", -1)]
    [InlineData("v1.0.0", "v1.0.0", 0)]
    [InlineData("v1.0.0-rc.1", "v1.0.0", -1)]  // a release beats a prerelease of the same number
    [InlineData("v1.0.0-rc.1", "v1.0.0-rc.2", -1)]
    public void Go_versions_compare_by_semver_not_by_string(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(new GoVersionOrdering().Compare(left, right)));
    }

    [Fact]
    public void A_go_version_parses_with_or_without_its_v_prefix()
    {
        var ordering = new Ordering();

        Assert.NotNull(ordering.TryParse("v1.2.3"));
        Assert.NotNull(ordering.TryParse("1.2.3"));

        // A pseudo-version's timestamp-commit tail is not part of the comparable numbers.
        VersionComponents pseudo = ordering.TryParse("v0.0.0-20191109021931-daa7c04131f5")!.Value;
        Assert.Equal(0, pseudo.Major);
        Assert.Equal("20191109021931-daa7c04131f5", pseudo.Prerelease);
    }

    [Fact]
    public void A_non_version_is_not_parsed_rather_than_guessed()
    {
        Assert.Null(new Ordering().TryParse("not-a-version"));
        Assert.Null(new Ordering().TryParse(""));
    }

    /// <summary>The plugin's own ordering, so the tests check the one that will actually be used.</summary>
    private sealed class Ordering : IVersionOrdering
    {
        private readonly GoVersionOrdering _inner = new();

        public VersionComponents? TryParse(string version) => _inner.TryParse(version);

        public int Compare(string? x, string? y) => _inner.Compare(x, y);
    }
}

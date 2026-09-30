using Coppice.Core.Projects;
using Coppice.Ports;
using Coppice.Tests.Support;

namespace Coppice.Tests.Projects;

/// <summary>
/// Card T-008 acceptance (FR-06). Runs on the fake VFS, so nothing here reads a real project tree.
/// </summary>
public sealed class ProjectDiscoveryTests
{
    /// <summary>
    /// Asserts some project satisfies the predicate. A helper rather than Assert.Contains directly:
    /// xUnit's Contains overloads for async enumerables confuse inference on IReadOnlyList here,
    /// and a failing Assert with a predicate reports far better than a collection dump.
    /// </summary>
    private static void AssertHasProject(ProjectSet projects, string expectedRootPath) =>
        AssertHasProject(projects.Projects, expectedRootPath);

    private static void AssertHasProject(IReadOnlyList<Project> projects, string expectedRootPath)
    {
        Assert.True(
            projects.Any(p => p.RootPath == expectedRootPath),
            $"expected a project at '{expectedRootPath}'. Found: "
                + string.Join(", ", projects.Select(p => p.RootPath)));
    }

    private static void AssertNoProjectUnder(ProjectSet projects, string fragment) =>
        AssertNoProjectUnder(projects.Projects, fragment);

    private static void AssertNoProjectUnder(IReadOnlyList<Project> projects, string fragment) =>
        Assert.DoesNotContain(projects, p => p.RootPath.Contains(fragment, StringComparison.Ordinal));

    /// <summary>
    /// A mixed tree with all four marker types named in the v0.1 scope, plus a monorepo service
    /// with its own marker, plus vendored trees that must NOT count.
    /// </summary>
    private static FakeFileSystem Fixture() => new FakeFileSystem(OperatingSystemKind.Linux)
        .AddFile("/work/global.json", "{}")
        .AddFile("/work/Directory.Build.props", "")
        // .NET
        .AddFile("/work/src/App/App.csproj", "<Project/>")
        .AddFile("/work/src/Lib/Lib.fsproj", "<Project/>")
        // Go
        .AddFile("/work/services/api/go.mod", "module api")
        .AddFile("/work/services/api/go.sum", "")
        // Rust
        .AddFile("/work/crates/thing/Cargo.toml", "[package]")
        // Node
        .AddFile("/work/web/package.json", "{}")
        // Monorepo: the root itself has no marker, a nested service does.
        .AddFile("/work/mono/tools/cli/go.mod", "module cli")
        // Vendored trees that must be ignored.
        .AddFile("/work/web/node_modules/left-pad/package.json", "{}")
        .AddFile("/work/crates/thing/vendor/somecrate/Cargo.toml", "[package]")
        .AddFile("/work/.git/config", "")
        .AddFile("/work/.coppice/quarantine/pkg/package.json", "{}");

    [Fact]
    public void Finds_all_four_marker_types_in_fixtures()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work"]);

        AssertHasProject(projects.For("dotnet"), "/work/src/App");
        AssertHasProject(projects.For("dotnet"), "/work/src/Lib");
        AssertHasProject(projects.For("go"), "/work/services/api");
        AssertHasProject(projects.For("rust"), "/work/crates/thing");
        AssertHasProject(projects.For("node"), "/work/web");
    }

    [Fact]
    public void Finds_a_project_at_the_scanned_root_itself()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work"]);

        // global.json makes /work itself a .NET project root.
        AssertHasProject(projects, "/work");
        AssertHasProject(projects.For("dotnet"), "/work");
    }

    [Fact]
    public void Finds_a_nested_project_whose_parent_has_no_marker()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work"]);

        AssertHasProject(projects, "/work/mono/tools/cli");
    }

    [Fact]
    public void Markers_inside_node_modules_and_vendor_are_ignored()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work"]);

        AssertNoProjectUnder(projects, "node_modules");
        AssertNoProjectUnder(projects, "vendor");
        AssertNoProjectUnder(projects, "left-pad");
        AssertNoProjectUnder(projects, "somecrate");
    }

    [Fact]
    public void The_git_directory_and_quarantine_directory_are_skipped()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work"]);

        // A quarantined package is a RECOVERABLE COPY of something already removed. Treating it as
        // a live project would make a resolver report it as referenced and keep it forever.
        AssertNoProjectUnder(projects, ".coppice");
        AssertNoProjectUnder(projects, ".git");
    }

    [Fact]
    public void The_result_is_a_projectset_usable_by_resolvers()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work"]);

        // Every ecosystem touched is recorded, so a resolver can distinguish "scanned, found none"
        // from "never scanned" (FR-07's three states).
        Assert.Equal(["dotnet", "go", "node", "rust"], projects.Ecosystems);
        Assert.All(projects.Projects, p =>
        {
            Assert.NotEmpty(p.MarkerIds);
            Assert.NotEmpty(p.MarkerFiles);
            Assert.All(p.MarkerFiles, f => Assert.StartsWith(p.RootPath, f, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void An_ecosystem_with_no_projects_yields_an_empty_but_valid_set()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux).AddFile("/solo/go.mod", "module solo");
        var service = new ProjectDiscoveryService(fs);

        ProjectSet projects = service.Discover(["/solo"]);

        Assert.Equal(["go"], projects.Ecosystems);
        Assert.Empty(projects.For("rust"));
    }

    [Fact]
    public void A_missing_project_root_is_reported_not_fatal()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work", "/does/not/exist"]);

        // The rest of the scan still happened.
        Assert.NotEmpty(projects.Projects);
        Assert.Contains(projects.Issues, i => i.Code == "PROJECT-ROOT-MISSING");
    }

    [Fact]
    public void Discovery_performs_zero_filesystem_writes()
    {
        // C-1.
        FakeFileSystem fs = Fixture();
        new ProjectDiscoveryService(fs).Discover(["/work"]);

        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Discovery_is_deterministic()
    {
        var service = new ProjectDiscoveryService(Fixture());

        string first = string.Join("\n", service.Discover(["/work"]).Projects.Select(p => p.RootPath));
        string second = string.Join("\n", service.Discover(["/work"]).Projects.Select(p => p.RootPath));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Scanning_the_same_root_twice_does_not_duplicate_projects()
    {
        var service = new ProjectDiscoveryService(Fixture());
        ProjectSet projects = service.Discover(["/work", "/work"]);

        Assert.Equal(
            projects.Projects.Count,
            projects.Projects.Select(p => p.RootPath).Distinct().Count());
    }

    [Fact]
    public void A_plugin_can_register_a_marker_without_any_core_change()
    {
        // The OCP claim (04-plugin-contract): adding an ecosystem is a registration, not an edit.
        var registry = ProjectMarkerRegistry.CreateDefault()
            .Register(new ProjectMarker
            {
                Id = "python-package",
                Ecosystem = "python",
                ExactNames = ["pyproject.toml"],
            });

        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddFile("/work/pyproject.toml", "[project]")
            .AddFile("/work/requirements.txt", "");

        ProjectSet projects = new ProjectDiscoveryService(fs, registry).Discover(["/work"]);

        AssertHasProject(projects.For("python"), "/work");
        // A non-marker file is still not a project.
        AssertNoProjectUnder(projects, "requirements");
    }

    [Fact]
    public void A_custom_skip_list_replaces_the_default()
    {
        var registry = ProjectMarkerRegistry.CreateDefault();
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddFile("/work/keep/go.mod", "module keep")
            .AddFile("/work/skipme/go.mod", "module skipme");

        ProjectSet projects = new ProjectDiscoveryService(fs, registry, ["skipme"]).Discover(["/work"]);

        AssertHasProject(projects, "/work/keep");
        AssertNoProjectUnder(projects, "skipme");
    }

    [Fact]
    public void Deeply_nested_projects_are_found_up_to_the_depth_limit()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        for (int i = 0; i < 8; i++)
        {
            fs.AddFile($"/work/d{i}/go.mod", $"module d{i}");
        }

        ProjectSet projects = new ProjectDiscoveryService(fs).Discover(["/work"]);
        Assert.Equal(8, projects.Projects.Count);
    }

    [Theory]
    [InlineData("App.csproj", "dotnet-project")]
    [InlineData("Lib.fsproj", "dotnet-project")]
    [InlineData("go.mod", "go-module")]
    [InlineData("go.work", "go-module")]
    [InlineData("Cargo.toml", "cargo-package")]
    [InlineData("package.json", "npm-package")]
    [InlineData("global.json", "dotnet-project")]
    public void Known_marker_files_are_matched(string fileName, string expectedMarker)
    {
        ProjectMarkerRegistry registry = ProjectMarkerRegistry.CreateDefault();
        Assert.Contains(registry.Match(fileName), m => m.Id == expectedMarker);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    [InlineData("csproj")]          // the extension alone is not a file name
    [InlineData(".csproj")]         // a dotfile, not a project
    [InlineData("app.csproj.bak")]  // must not match on a suffix
    public void Non_marker_files_are_not_matched(string fileName)
    {
        ProjectMarkerRegistry registry = ProjectMarkerRegistry.CreateDefault();
        Assert.Empty(registry.Match(fileName));
    }

    [Fact]
    public void Marker_matching_is_case_insensitive_like_the_filesystems_it_targets()
    {
        ProjectMarkerRegistry registry = ProjectMarkerRegistry.CreateDefault();
        Assert.Contains(registry.Match("App.CSPROJ"), m => m.Id == "dotnet-project");
        Assert.Contains(registry.Match("GO.MOD"), m => m.Id == "go-module");
    }
}

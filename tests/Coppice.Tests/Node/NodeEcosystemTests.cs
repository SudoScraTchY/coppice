using Xunit;

namespace Coppice.Tests.Node;

using Coppice.Manifests;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;
using LocationRecord = Coppice.Manifests.LocationRecord;
using ManifestOrigin = Coppice.Manifests.ManifestOrigin;
using ManifestParserKind = Coppice.Manifests.ManifestParserKind;
using ManifestSource = Coppice.Manifests.ManifestSource;
using ManifestValidationResult = Coppice.Manifests.ManifestValidationResult;
using NodePlugin = Coppice.Plugins.Node.NodeEcosystem;
using SemVerOrdering = Coppice.Plugins.Node.SemVerVersionOrdering;
using Support = Coppice.Tests.Support;

/// <summary>
/// T-024: the Node profile (FR-23, 09-ecosystems "node").
/// <para>
/// The card's hard constraint is that <c>npm-cache</c> items are WHOLE-LOCATION only. That is not a
/// formatting choice: <c>_cacache</c> stores blobs under a hash of their CONTENT, so there is no
/// per-package path to name and two packages can share one blob. These tests prove the constraint holds
/// against a realistic cache rather than asserting it in a comment.
/// </para>
/// </summary>
public class NodeEcosystemTests
{
    private static Support.FakeFileSystem NpmCache()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);

        // A realistic _cacache: content-addressed blobs, named by hash, sharing content between packages.
        fs.AddDirectory("/home/dev/.npm/_cacache/content-v2/sha512/ab/cd");
        fs.AddFile("/home/dev/.npm/_cacache/content-v2/sha512/ab/cd/abcdef", "blob-1", fileIdentity: "blob-1");
        fs.AddDirectory("/home/dev/.npm/_cacache/content-v2/sha512/ef/gh");
        fs.AddFile("/home/dev/.npm/_cacache/content-v2/sha512/ef/gh/efghij", "blob-2", fileIdentity: "blob-2");

        // The index maps URLs to digests. This is the only place a package NAME appears — and it maps to
        // a digest, not to a removable path.
        fs.AddFile("/home/dev/.npm/_cacache/index-v5/aa/bb/hash1", """{"key":"make-fetch-happen:request-cache:https://registry.npmjs.org/left-pad/-/left-pad-1.3.0.tgz","integrity":"sha512-abcdef"}""");
        fs.AddFile("/home/dev/.npm/_cacache/index-v5/aa/bb/hash2", """{"key":"make-fetch-happen:request-cache:https://registry.npmjs.org/right-pad/-/right-pad-2.0.0.tgz","integrity":"sha512-efghij"}""");

        // npm's own logs and config sit in the same tree. They are NOT cache and must not be swept in.
        fs.AddFile("/home/dev/.npm/_logs/2026-01-01-debug.log", "log");
        fs.AddFile("/home/dev/.npmrc", "//registry:_authToken=redacted\n");

        return fs;
    }

    private static ScanContext ContextFor(
        Support.FakeFileSystem fs,
        string locationId = "npm-cache",
        string path = "/home/dev/.npm")
    {
        Conformance.ConformanceFixture fixture = new()
        {
            Name = "node",
            FileSystem = fs,
            ProcessRunner = new Support.FakeProcessRunner(),
            Factory = () => throw new NotSupportedException("built per-case; never cloned"),
            Roots = [Conformance.ConformanceFixture.Root(locationId, path)],
            PluginFactory = () => new NodePlugin(fs),
        };

        return fixture.ToContext();
    }

    private static async Task<List<PortableItem>> DiscoverAsync(
        NodePlugin plugin,
        Support.FakeFileSystem fs,
        string locationId = "npm-cache",
        string path = "/home/dev/.npm")
    {
        var items = new List<PortableItem>();

        await foreach (PortableItem item in plugin.Discover(ContextFor(fs, locationId, path)))
        {
            items.Add(item);
        }

        return items;
    }

    // ---- the whole-location constraint, which is the card's acceptance ----------------------------------

    [Fact]
    public async Task A_content_addressed_cache_yields_EXACTLY_ONE_item()
    {
        Support.FakeFileSystem fs = NpmCache();

        List<PortableItem> items = await DiscoverAsync(new NodePlugin(fs), fs);

        // Four content blobs, two index entries, two packages named in the index. Still ONE item.
        //
        // This is the property 09 states and the card tests. A per-package enumeration here would produce
        // paths named by content hash — real directories, but not addresses of "left-pad", and a plan step
        // built from one would delete a blob that another package may also be using.
        PortableItem cache = Assert.Single(items);

        Assert.Equal("npm-cache", cache.Name);
        Assert.Equal("cache", cache.Kind);
        Assert.Equal("whole-location", cache.Facts["granularity"]);
    }

    [Fact]
    public async Task The_cache_item_is_not_per_package_even_when_the_index_names_packages()
    {
        Support.FakeFileSystem fs = NpmCache();

        List<PortableItem> items = await DiscoverAsync(new NodePlugin(fs), fs);

        // The names DO appear in the cache, in the index. What must not happen is them becoming items.
        string payload = string.Join("\n", items.Select(i => i.Name + i.Version));
        Assert.DoesNotContain("left-pad", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("right-pad", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_item_points_into_the_content_store_by_hash()
    {
        Support.FakeFileSystem fs = NpmCache();

        List<PortableItem> items = await DiscoverAsync(new NodePlugin(fs), fs);

        // The one item points at the STORE as a whole, not at a blob. A plan step targeting a single
        // sha512 blob is the failure this guards against.
        foreach (PortableItem item in items)
        {
            Assert.DoesNotContain("/content-v2/sha512/", item.RootPath, StringComparison.Ordinal);
        }

        Assert.EndsWith("_cacache", Assert.Single(items).RootPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_named_dot_npm_but_with_no_cacache_yields_nothing()
    {
        // ~/.npm also holds .npmrc and the auth token. Reporting "the cache" for a directory that has no
        // _cacache would let a policy propose removing a user's registry credentials.
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/home/dev/.npmrc", "//registry:_authToken=redacted\n");
        fs.AddDirectory("/home/dev/.npm/_logs");

        List<PortableItem> items = await DiscoverAsync(new NodePlugin(fs), fs);

        Assert.Empty(items);
    }

    [Fact]
    public async Task The_cache_is_safe_because_every_byte_is_redownloadable()
    {
        Support.FakeFileSystem fs = NpmCache();

        PortableItem cache = Assert.Single(await DiscoverAsync(new NodePlugin(fs), fs));

        // Safe is about the BYTES. The granularity fact is what constrains the plan to one step. Conflating
        // the two — treating "safe" as "individually addressable" — is the bug this pair of values avoids.
        Assert.Equal(Coppice.Ports.Risk.Safe, cache.Risk);
        Assert.Equal("1", cache.Facts["itemCount"]);
    }

    [Fact]
    public async Task Discovery_writes_nothing()
    {
        Support.FakeFileSystem fs = NpmCache();
        await DiscoverAsync(new NodePlugin(fs), fs);

        Assert.Empty(fs.Writes);
    }

    // ---- npm-global: the other half of the asymmetry ---------------------------------------------------

    [Fact]
    public async Task Global_packages_yield_one_item_each()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/usr/lib/node_modules/typescript/package.json", """{"name":"typescript","version":"5.4.5"}""");
        fs.AddFile("/usr/lib/node_modules/npm/package.json", """{"name":"npm","version":"10.2.0"}""");

        // A directory with no package.json is a partial install, not a package.
        fs.AddDirectory("/usr/lib/node_modules/.bin");
        fs.AddDirectory("/usr/lib/node_modules/half-installed");

        List<PortableItem> items = await DiscoverAsync(
            new NodePlugin(fs), fs, "npm-global", "/usr/lib/node_modules");

        Assert.Equal(["npm", "typescript"], items.Select(i => i.Name).Order(StringComparer.Ordinal));

        PortableItem typescript = Assert.Single(items, i => i.Name == "typescript");
        Assert.Equal("5.4.5", typescript.Version);
        Assert.Equal("per-package", typescript.Facts["granularity"]);

        // A globally installed package is real code, capped at review — "just install it again" is not
        // true of a private or paid package.
        Assert.All(items, i => Assert.Equal(Coppice.Ports.Risk.Review, i.Risk));
    }

    [Fact]
    public async Task A_global_package_with_an_unreadable_manifest_reports_no_version_rather_than_a_guess()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/usr/lib/node_modules/broken/package.json", "{ this is not json");

        List<PortableItem> items = await DiscoverAsync(
            new NodePlugin(fs), fs, "npm-global", "/usr/lib/node_modules");

        PortableItem broken = Assert.Single(items);
        Assert.Equal("broken", broken.Name);

        // An empty version keeps it out of any "newest N" decision rather than putting a fabricated entry
        // at the top of an ordering.
        Assert.Equal(string.Empty, broken.Version);
    }

    [Fact]
    public async Task An_empty_or_absent_root_yields_nothing_without_throwing()
    {
        var empty = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        empty.AddDirectory("/home/dev/.npm");

        Assert.Empty(await DiscoverAsync(new NodePlugin(empty), empty));

        var missing = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        Assert.Empty(await DiscoverAsync(new NodePlugin(missing), missing));
    }

    // ---- references ------------------------------------------------------------------------------------

    private const string LockFile = """
        {
          "name": "myapp",
          "version": "1.0.0",
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "myapp", "version": "1.0.0" },
            "node_modules/left-pad": {
              "version": "1.3.0",
              "resolved": "https://registry.npmjs.org/left-pad/-/left-pad-1.3.0.tgz",
              "integrity": "sha512-abc"
            },
            "node_modules/express": {
              "version": "4.18.2",
              "resolved": "https://registry.npmjs.org/express/-/express-4.18.2.tgz"
            }
          }
        }
        """;

    private static (NodePlugin Plugin, Coppice.Ports.ProjectSet Projects) WithLock(string root, string? contents = null)
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile($"{root}/package-lock.json", contents ?? LockFile);

        return (
            new NodePlugin(fs),
            new Coppice.Ports.ProjectSet(
                [new Coppice.Ports.Project(root, ["package.json"], ["node"])],
                ["node"],
                []));
    }

    [Fact]
    public void A_locked_package_is_Referenced()
    {
        (NodePlugin plugin, Coppice.Ports.ProjectSet projects) = WithLock("/src/app");
        Assert.Equal(
            Coppice.Ports.Usage.Referenced,
            plugin.Resolve("left-pad", "1.3.0", projects).Usage);
    }

    [Fact]
    public void A_package_absent_from_the_lock_is_Unreferenced_when_every_project_read()
    {
        (NodePlugin plugin, Coppice.Ports.ProjectSet projects) = WithLock("/src/app");
        Assert.Equal(
            Coppice.Ports.Usage.Unreferenced,
            plugin.Resolve("right-pad", "2.0.0", projects).Usage);
    }

    [Fact]
    public void A_lock_entry_without_a_name_is_not_a_dependency()
    {
        // lockfileVersion 2 writes `"packages"` keyed by path with the name sometimes absent, and the
        // ROOT entry has no name either. Treating a nameless entry as a package would invent one.
        (NodePlugin plugin, Coppice.Ports.ProjectSet projects) = WithLock("/src/app");

        Assert.Equal(
            Coppice.Ports.Usage.Unreferenced,
            plugin.Resolve("", "1.0.0", projects).Usage);
    }

    [Fact]
    public void Package_names_match_case_insensitively()
    {
        // npm lower-cases names on publish, but a lockfile or a global install can carry a capital.
        (NodePlugin plugin, Coppice.Ports.ProjectSet projects) = WithLock("/src/app");

        Assert.Equal(
            Coppice.Ports.Usage.Referenced,
            plugin.Resolve("Left-Pad", "1.3.0", projects).Usage);
    }

    [Fact]
    public void Zero_projects_is_Unknown_never_Unreferenced()
    {
        // The card's point: a global package that no lock mentions resolves Unknown, which is correct
        // rather than guessed — a lockfile describes a PROJECT's dependencies and says nothing about what
        // is installed globally.
        Ports.ReferenceVerdict verdict = new NodePlugin(new Support.FakeFileSystem(OperatingSystemKind.Linux))
            .Resolve("typescript", "5.4.5", Coppice.Ports.ProjectSet.Empty);

        Assert.Equal(Coppice.Ports.Usage.Unknown, verdict.Usage);
        Assert.Contains("no projects", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_project_with_no_lock_is_Unknown()
    {
        var fs = new Support.FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile("/src/app/package.json", """{"name":"app"}""");

        Ports.ReferenceVerdict verdict = new NodePlugin(fs).Resolve(
            "left-pad",
            "1.3.0",
            new Coppice.Ports.ProjectSet(
                [new Coppice.Ports.Project("/src/app", ["package.json"], ["node"])], ["node"], []));

        Assert.Equal(Coppice.Ports.Usage.Unknown, verdict.Usage);
    }

    [Fact]
    public void An_unparseable_lock_is_Unknown_not_Unreferenced()
    {
        // Reporting a corrupt lockfile as an EMPTY reference list is the dangerous direction: every package
        // in the cache would claim to be unreferenced.
        (NodePlugin plugin, Coppice.Ports.ProjectSet projects) = WithLock("/src/app", "{ truncated");

        Assert.Equal(
            Coppice.Ports.Usage.Unknown,
            plugin.Resolve("left-pad", "1.3.0", projects).Usage);
    }

    [Fact]
    public void No_filesystem_port_means_Unknown()
    {
        Ports.ReferenceVerdict verdict = new NodePlugin().Resolve(
            "left-pad",
            "1.3.0",
            new Coppice.Ports.ProjectSet(
                [new Coppice.Ports.Project("/src/app", ["package.json"], ["node"])], ["node"], []));

        Assert.Equal(Coppice.Ports.Usage.Unknown, verdict.Usage);
    }

    // ---- version ordering ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.3.0", "1.10.0", -1)]   // numeric, not lexical
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    public void Npm_versions_compare_by_semver(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(new SemVerOrdering().Compare(left, right)));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]      // npm permits the leading 'v' in a package.json
    [InlineData("^1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void The_common_version_prefixes_parse(string input, string equivalent)
    {
        Assert.Equal(
            new SemVerOrdering().TryParse(equivalent),
            new SemVerOrdering().TryParse(input));
    }

    [Fact]
    public void A_non_version_is_not_parsed_rather_than_guessed()
    {
        Assert.Null(new SemVerOrdering().TryParse("latest"));
        Assert.Null(new SemVerOrdering().TryParse(""));
    }

    // ---- the manifest ------------------------------------------------------------------------------------

    private static string ManifestText(string name)
    {
        string marker = ".manifests." + name;

        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.EndsWith(marker, StringComparison.Ordinal))
                {
                    continue;
                }

                using Stream? stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                using StreamReader reader = new(stream);
                return reader.ReadToEnd();
            }
        }

        throw new FileNotFoundException($"no embedded manifest named {name}");
    }

    [Fact]
    public void The_node_manifest_validates()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("node.toml"),
            ManifestOrigin.BuiltIn,
            "node.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
    }

    [Fact]
    public void The_cache_declares_an_empty_layout_which_is_what_means_whole_location()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("node.toml"),
            ManifestOrigin.BuiltIn,
            "node.toml");

        LocationRecord cache = Assert.Single(result.Locations, l => l.Id == "npm-cache");

        // The empty layout is load-bearing: it tells the engine the whole root is the unit. A layout like
        // "{package}" would invite per-package enumeration that the cache cannot support.
        Assert.Equal(string.Empty, cache.Layout);
        Assert.Empty(cache.Fingerprint.EntryPatterns);
        Assert.Equal(["_cacache"], cache.Fingerprint.RequiredMarkers);
    }

    [Fact]
    public void The_cache_is_queried_so_it_carries_no_confidence_penalty()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("node.toml"),
            ManifestOrigin.BuiltIn,
            "node.toml");

        LocationRecord cache = Assert.Single(result.Locations, l => l.Id == "npm-cache");

        // The contrast with cargo: npm CAN be asked where its cache is, so the path is confirmed.
        Assert.Equal(0, cache.ConfidencePenalty);
        Assert.Contains(cache.Sources, s => s.Via == CoreDomain.ResolvedVia.Tool);
    }

    [Fact]
    public void The_cache_query_uses_the_line_parser_because_npm_prints_a_bare_path()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("node.toml"),
            ManifestOrigin.BuiltIn,
            "node.toml");

        LocationRecord cache = Assert.Single(result.Locations, l => l.Id == "npm-cache");

        // A real capture (recorded/npm-config-get-cache.Windows.txt) shows a bare path with no label.
        // Declaring label-value here would parse nothing and the location would fall through to the
        // default — silently reporting ~/.npm on a machine configured elsewhere.
        ManifestSource tool = Assert.Single(cache.Sources, s => s.Via == CoreDomain.ResolvedVia.Tool);
        Assert.Equal("npm config get cache", tool.Run);
        Assert.Equal(ManifestParserKind.Line, tool.Parse);
    }

    [Fact]
    public void Both_node_removals_are_native_routes()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("node.toml"),
            ManifestOrigin.BuiltIn,
            "node.toml");

        Assert.Equal("npm cache clean --force", Assert.Single(result.Locations, l => l.Id == "npm-cache").RemoveCommand);
        Assert.Equal("npm uninstall -g", Assert.Single(result.Locations, l => l.Id == "npm-global").RemoveCommand);
    }
}

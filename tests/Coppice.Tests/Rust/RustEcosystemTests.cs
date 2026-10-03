using Xunit;

namespace Coppice.Tests.Rust;

// These tests cross the PLUGIN boundary, so they use the Ports shapes throughout. Declaring the usings
// INSIDE the namespace is what makes them take precedence over the project's global aliases — a
// file-scoped alias that shadows a global one is a compile error, which is why this is here rather than
// at the top of the file.
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;
using CrateVersionOrdering = Coppice.Plugins.Rust.CrateVersionOrdering;
using EcosystemManifest = Coppice.Manifests.EcosystemManifest;
using LocationRecord = Coppice.Manifests.LocationRecord;
using LocationSpec = Coppice.Core.Resolution.LocationSpec;
using ManifestOrigin = Coppice.Manifests.ManifestOrigin;
using ManifestValidationResult = Coppice.Manifests.ManifestValidationResult;
using ResolutionEngine = Coppice.Core.Resolution.ResolutionEngine;
using ResolutionOutcome = Coppice.Core.Resolution.ResolutionOutcome;
using ResolvedRoot = Coppice.Core.Domain.ResolvedRoot;
using RootConfidence = Coppice.Core.Domain.RootConfidence;
using RustPlugin = Coppice.Plugins.Rust.RustEcosystem;
using SourceResult = Coppice.Core.Resolution.SourceResult;
using Support = Coppice.Tests.Support;

/// <summary>
/// T-023: the Rust ecosystem profile (FR-23, 09-ecosystems "rust").
/// <para>
/// Cargo is the profile that proves the confidence mechanism. It has NO query command, so every location
/// except <c>rustup-toolchains</c> is located by convention — and the acceptance criterion is that the
/// report SAYS so. These tests cover the three things that matter: the confidence is computed, the
/// confidence is visible, and a crate version is not confused with a crate name.
/// </para>
/// </summary>
public class RustEcosystemTests
{
    private static Support.FakeFileSystem Fs(OperatingSystemKind os = OperatingSystemKind.Linux) =>
        new(os);

    private static ScanContext ContextFor(
        Support.FakeFileSystem fs,
        string locationId = "cargo-registry-src",
        string path = "/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f")
    {
        // Never cloned in these tests, so the factory throws rather than self-referencing a local that
        // is not yet assigned — the conformance harness's own per-case fixtures do the same.
        Conformance.ConformanceFixture fixture = new()
        {
            Name = "rust",
            FileSystem = fs,
            ProcessRunner = new Support.FakeProcessRunner(),
            Factory = () => throw new NotSupportedException("this fixture is built per-case and is never cloned"),
            Roots = [Conformance.ConformanceFixture.Root(locationId, path)],
            PluginFactory = () => new RustPlugin(fs),
        };

        return fixture.ToContext();
    }

    private static async Task<List<PortableItem>> DiscoverAsync(
        RustPlugin plugin,
        Support.FakeFileSystem fs,
        string locationId = "cargo-registry-src",
        string path = "/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f")
    {
        var items = new List<PortableItem>();

        await foreach (PortableItem item in plugin.Discover(ContextFor(fs, locationId, path)))
        {
            items.Add(item);
        }

        return items;
    }

    // ---- the hyphen ambiguity, which is the whole parsing problem -------------------------------------

    [Theory]
    // Crate names contain hyphens constantly. Splitting on the FIRST hyphen invents a crate "serde"
    // at version "json-1.0.0", which exists in no Cargo.lock and would never match.
    [InlineData("serde-json-1.0.0", "serde-json", "1.0.0")]
    [InlineData("serde-1.0.0", "serde", "1.0.0")]
    [InlineData("syn-2.0.90", "syn", "2.0.90")]
    [InlineData("winapi-i686-pc-windows-gnu-0.4.0", "winapi-i686-pc-windows-gnu", "0.4.0")]
    // Pre-release and build metadata are part of the version.
    [InlineData("tokio-1.35.0-beta.1", "tokio", "1.35.0-beta.1")]
    [InlineData("libc-0.2.155+build.1", "libc", "0.2.155+build.1")]
    // Not a crate at all.
    [InlineData("index.crates.io-6f17d22bba15001f", "", "")]
    [InlineData("registry", "", "")]
    [InlineData("some-directory", "", "")]
    [InlineData("1.0.0", "", "")]
    public void The_version_split_searches_right_to_left(string directory, string name, string version)
    {
        bool parsed = RustPlugin.TrySplitCrateVersion(directory, out string actualName, out string actualVersion);

        if (name.Length == 0)
        {
            Assert.False(parsed);
            return;
        }

        Assert.True(parsed);
        Assert.Equal(name, actualName);
        Assert.Equal(version, actualVersion);
    }

    [Fact]
    public async Task Crates_are_found_below_the_registry_index_directory()
    {
        // The real shape is src/<registry-hash>/<name>-<version>, so the item is two levels down and the
        // registry segment varies. A fixed shallow walk would find nothing — the same mistake the Go
        // plugin made first, which is why this is asserted explicitly.
        var fs = Fs();
        fs.AddDirectory("/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/serde-1.0.197");
        fs.AddDirectory("/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/serde_json-1.0.114");

        List<PortableItem> items = await DiscoverAsync(new RustPlugin(fs), fs);

        Assert.Equal(2, items.Count);
        Assert.Equal(["serde-1.0.197", "serde_json-1.0.114"], items.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["1.0.114", "1.0.197"], items.Select(i => i.Version).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_crates_own_contents_are_not_separate_items()
    {
        var fs = Fs();
        fs.AddDirectory("/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/serde-1.0.197");
        fs.AddDirectory("/home/dev/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/serde-1.0.197/vendor/serde-core-1.0.0");

        List<PortableItem> items = await DiscoverAsync(new RustPlugin(fs), fs);

        Assert.Equal("serde-1.0.197", Assert.Single(items).Name);
    }

    [Fact]
    public async Task Discovery_writes_nothing_and_is_deterministic()
    {
        var fs = Fs();
        fs.AddDirectory("/home/dev/.cargo/registry/src/idx/a-1.0.0");
        fs.AddDirectory("/home/dev/.cargo/registry/src/idx/b-2.0.0");

        List<PortableItem> first = await DiscoverAsync(new RustPlugin(fs), fs);
        List<PortableItem> second = await DiscoverAsync(new RustPlugin(fs), fs);

        Assert.Empty(fs.Writes);
        Assert.Equal(first.Select(i => i.ItemId), second.Select(i => i.ItemId));
    }

    [Fact]
    public async Task Toolchains_are_listed_and_rustup_bookkeeping_is_not()
    {
        var fs = Fs();
        fs.AddDirectory("/home/dev/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin");
        fs.AddDirectory("/home/dev/.rustup/toolchains/1.75.0-x86_64-unknown-linux-gnu/bin");

        // rustup's own state, beside the toolchains. None of these is an installed toolchain.
        fs.AddFile("/home/dev/.rustup/toolchains/update-hashes", "{}", readOnly: true);
        fs.AddFile("/home/dev/.rustup/toolchains/settings.toml", "x");
        fs.AddDirectory("/home/dev/.rustup/toolchains/download-tmp");

        // A directory with no bin/ is a partial download, not an install.
        fs.AddDirectory("/home/dev/.rustup/toolchains/nightly-x86_64-unknown-linux-gnu");

        List<PortableItem> items = await DiscoverAsync(
            new RustPlugin(fs),
            fs,
            "rustup-toolchains",
            "/home/dev/.rustup/toolchains");

        Assert.Equal(
            ["1.75.0-x86_64-unknown-linux-gnu", "stable-x86_64-unknown-linux-gnu"],
            items.Select(i => i.Name).Order(StringComparer.Ordinal));

        // A toolchain is an installed binary, not a regenerable download.
        Assert.All(items, i => Assert.Equal(Coppice.Ports.Risk.Review, i.Risk));
    }

    // ---- Cargo.lock: three states ---------------------------------------------------------------------

    private const string LockFile = """
        # This file is automatically @generated by Cargo.
        version = 3

        [[package]]
        name = "serde"
        version = "1.0.197"
        source = "registry+https://github.com/rust-lang/crates.io-index"
        checksum = "abc"

        [[package]]
        name = "serde_json"
        version = "1.0.114"
        source = "registry+https://github.com/rust-lang/crates.io-index"

        [[package]]
        name = "myapp"
        version = "0.1.0"

        [[package]]
        name = "vendored-dep"
        version = "2.0.0"
        source = "git+https://example.com/dep?branch=main#deadbeef"

        """;

    /// <summary>
    /// A plugin and a project set sharing ONE fake filesystem. The plugin reads through the port it was
    /// constructed with, so a helper that wrote the lock file into a throwaway VFS would leave the plugin
    /// looking at a machine that has never been built.
    /// </summary>
    private static (RustPlugin Plugin, ProjectSet Projects) WithLock(string root, string? contents = null)
    {
        var fs = Fs();
        fs.AddFile($"{root}/Cargo.lock", contents ?? LockFile);

        return (new RustPlugin(fs), new ProjectSet(
            [new Project(root, ["Cargo.toml"], ["rust"])], ["rust"], []));
    }

    [Fact]
    public void A_locked_crate_is_Referenced()
    {
        (RustPlugin plugin, ProjectSet projects) = WithLock("/src/app");
        Assert.Equal(Usage.Referenced, plugin.Resolve("serde", "1.0.197", projects).Usage);
    }

    [Fact]
    public void A_crate_absent_from_the_lock_is_Unreferenced_when_every_project_read()
    {
        (RustPlugin plugin, ProjectSet projects) = WithLock("/src/app");
        Assert.Equal(Usage.Unreferenced, plugin.Resolve("serde", "1.0.150", projects).Usage);
    }

    [Fact]
    public void Zero_projects_is_Unknown_never_Unreferenced()
    {
        Ports.ReferenceVerdict verdict = new RustPlugin(Fs()).Resolve("serde", "1.0.197", ProjectSet.Empty);
        Assert.Equal(Usage.Unknown, verdict.Usage);
    }

    [Fact]
    public void A_project_that_has_never_been_built_is_Unknown_not_Unreferenced()
    {
        // A Cargo.toml with no Cargo.lock: the normal state of a fresh clone. Nothing may be concluded.
        var fs = Fs();
        fs.AddFile("/src/app/Cargo.toml", "[package]\nname = \"app\"\n");

        ProjectSet projects = new([new Project("/src/app", ["Cargo.toml"], ["rust"])], ["rust"], []);

        Ports.ReferenceVerdict verdict = new RustPlugin(fs).Resolve("serde", "1.0.197", projects);

        Assert.Equal(Usage.Unknown, verdict.Usage);
        Assert.Contains("Cargo.lock", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_unreadable_project_makes_every_crate_Unknown()
    {
        var fs = Fs();
        fs.AddFile("/src/good/Cargo.lock", LockFile);
        fs.AddFile("/src/bad/Cargo.toml", "[package]\nname='bad'\n");

        ProjectSet projects = new(
            [new Project("/src/good", ["Cargo.lock"], ["rust"]), new Project("/src/bad", ["Cargo.toml"], ["rust"])],
            ["rust"],
            []);

        Assert.Equal(Usage.Unknown, new RustPlugin(fs).Resolve("other", "1.0.0", projects).Usage);
    }

    [Fact]
    public void The_packages_own_name_and_version_are_read_not_the_sources()
    {
        // Cargo.lock has `source = "..."` and `checksum = "..."` fields whose values look nothing like a
        // package, and a lock's `version = 3` at the top level is the LOCK FORMAT version, not a package.
        // A parser that pattern-matched globally would produce a package named "3".
        (RustPlugin plugin, ProjectSet projects) = WithLock("/src/app");

        Assert.Equal(Usage.Referenced, plugin.Resolve("myapp", "0.1.0", projects).Usage);
        Assert.Equal(Usage.Unreferenced, plugin.Resolve("3", "1", projects).Usage);
        Assert.Equal(Usage.Unreferenced, plugin.Resolve("registry+https://github.com/rust-lang/crates.io-index", "1", projects).Usage);
    }

    [Fact]
    public void A_git_dependency_is_matched_like_any_other_package()
    {
        (RustPlugin plugin, ProjectSet projects) = WithLock("/src/app");
        Assert.Equal(Usage.Referenced, plugin.Resolve("vendored-dep", "2.0.0", projects).Usage);
    }

    [Fact]
    public void No_filesystem_port_means_Unknown()
    {
        ProjectSet projects = new([new Project("/src/app", ["Cargo.toml"], ["rust"])], ["rust"], []);
        Assert.Equal(Usage.Unknown, new RustPlugin().Resolve("serde", "1.0.197", projects).Usage);
    }

    // ---- version ordering ------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.9.0", "1.10.0", -1)]   // numeric, not lexical
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    public void Crate_versions_compare_by_semver(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(new CrateVersionOrdering().Compare(left, right)));
    }

    [Fact]
    public void An_unparseable_version_sorts_below_every_parseable_one()
    {
        var ordering = new CrateVersionOrdering();

        Assert.True(ordering.Compare("not-a-version", "0.0.1") < 0);
        Assert.True(ordering.Compare("0.0.1", "not-a-version") > 0);

        // Two unparseable values still order deterministically, so two runs agree.
        Assert.Equal(0, ordering.Compare("bogus", "bogus"));
    }

    // ---- the confidence mechanism, which is this card's real content ------------------------------------

    [Fact]
    public void The_rust_manifest_validates()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("rust.toml"),
            ManifestOrigin.BuiltIn,
            "rust.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
    }

    [Fact]
    public void Cargo_locations_declare_a_confidence_penalty_because_cargo_cannot_be_queried()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("rust.toml"),
            ManifestOrigin.BuiltIn,
            "rust.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));

        // Every cargo location carries the penalty...
        LocationRecord[] cargo = [.. result.Locations.Where(l => l.Id.StartsWith("cargo-", StringComparison.Ordinal))];
        Assert.NotEmpty(cargo);
        Assert.All(cargo, l => Assert.True(l.ConfidencePenalty > 0, $"{l.Id} has no confidence penalty"));

        // ...and no cargo location declares a `tool` source, because none exists.
        foreach (LocationRecord location in cargo)
        {
            Assert.DoesNotContain(location.Sources, s => s.Via == CoreDomain.ResolvedVia.Tool);
        }
    }

    [Fact]
    public void The_rustup_location_has_no_penalty_because_rustup_can_be_queried()
    {
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("rust.toml"),
            ManifestOrigin.BuiltIn,
            "rust.toml");

        LocationRecord rustup = Assert.Single(result.Locations, l => l.Id == "rustup-toolchains");

        // The contrast is what makes the others legible: rustup CAN answer, so its path is confirmed.
        Assert.Equal(0, rustup.ConfidencePenalty);
        Assert.Contains(rustup.Sources, s => s.Via == CoreDomain.ResolvedVia.Tool);
    }

    [Fact]
    public void The_penalty_survives_conversion_into_a_location_spec()
    {
        // The penalty is validated, stored, and then handed to the engine. An earlier version of
        // ToManifest built LocationSpecs from Id and Mode only, so the field was carried all the way to the
        // spec and dropped at the last step — present everywhere and doing nothing.
        ManifestValidationResult result = ManifestValidationResult.FromToml(
            ManifestText("rust.toml"),
            ManifestOrigin.BuiltIn,
            "rust.toml");

        EcosystemManifest? manifest = result.ToManifest();
        Assert.NotNull(manifest);

        LocationSpec registry = Assert.Single(manifest!.Locations, l => l.Id == "cargo-registry");
        Assert.Equal(0.5, registry.ConfidencePenalty, 3);

        LocationSpec rustup = Assert.Single(manifest.Locations, l => l.Id == "rustup-toolchains");
        Assert.Equal(0, rustup.ConfidencePenalty, 3);
    }

    [Fact]
    public void A_cargo_root_resolved_by_convention_is_reported_as_low_confidence()
    {
        // The end-to-end property: a root with no confirming tool comes back flagged, which is the
        // acceptance criterion ("confidence field visibly reduced").
        var fs = Fs();
        fs.AddDirectory("/home/dev/.cargo/registry");

        ResolutionEngine engine = new(fs, OperatingSystemKind.Linux, "/home/dev");

        LocationSpec spec = new()
        {
            Id = "cargo-registry",
            Mode = Coppice.Core.Resolution.ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            ConfidencePenalty = 0.5,
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Linux] = "/home/dev/.cargo/registry",
            },

            // The default rung answered. There is no tool source, because cargo has no query command —
            // which is exactly the situation the penalty exists to describe.
            Sources = new Dictionary<CoreDomain.ResolvedVia, SourceResult>
            {
                [CoreDomain.ResolvedVia.Default] = SourceResult.Of(
                    "/home/dev/.cargo/registry",
                    CoreDomain.ResolvedVia.Default),
            },
        };

        ResolutionOutcome outcome = engine.Resolve(spec);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootConfidence.Lowered, root.Confidence);
        Assert.True(root.IsLowConfidence);
        Assert.Contains("no query command", root.ConfidenceDetail!, StringComparison.OrdinalIgnoreCase);

        // Confidence is NOT a risk gate: the root is still cleanable. It answers "how did we find this",
        // not "may we touch it" — conflating the two would make cargo caches unusable.
        Assert.Equal(CoreDomain.RootValidity.Ok, root.Validity);
    }

    [Fact]
    public void A_root_confirmed_by_a_tool_stays_full_confidence()
    {
        // The contrast that makes the first test legible: the same penalty, but answered by a tool.
        var fs = Fs();

        ResolutionEngine engine = new(fs, OperatingSystemKind.Linux, "/home/dev");

        LocationSpec spec = new()
        {
            Id = "rustup-toolchains",
            Mode = Coppice.Core.Resolution.ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            ConfidencePenalty = 0.5,
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Linux] = "/home/dev/.rustup/toolchains",
            },
            Sources = new Dictionary<CoreDomain.ResolvedVia, SourceResult>
            {
                [CoreDomain.ResolvedVia.Tool] = SourceResult.Of(
                    "/home/dev/.rustup/toolchains",
                    CoreDomain.ResolvedVia.Tool,
                    "rustup toolchain list"),
            },
        };

        ResolutionOutcome outcome = engine.Resolve(spec);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootConfidence.Full, root.Confidence);
        Assert.False(root.IsLowConfidence);
        Assert.Null(root.ConfidenceDetail);
    }

    [Fact]
    public void A_pin_stays_full_confidence_because_the_user_stated_the_path()
    {
        // A pin is not a tool query, but it IS a statement from the user rather than a convention, so
        // penalising it would tell someone who pinned deliberately that we doubt them.
        var fs = Fs();
        fs.AddDirectory("/opt/cargo-home/registry");

        ResolutionEngine engine = new(fs, OperatingSystemKind.Linux, "/home/dev");

        LocationSpec spec = new()
        {
            Id = "cargo-registry",
            Mode = Coppice.Core.Resolution.ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            Pin = "/opt/cargo-home/registry",
            ConfidencePenalty = 0.5,
            Sources = new Dictionary<CoreDomain.ResolvedVia, SourceResult>(),
        };

        ResolutionOutcome outcome = engine.Resolve(spec);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootConfidence.Full, root.Confidence);
    }

    [Fact]
    public void A_location_with_no_penalty_is_never_low_confidence()
    {
        // The default: a location that does not opt in reports Full, so the mechanism cannot surprise a
        // manifest author who never asked for it.
        var fs = Fs();
        fs.AddDirectory("/cache");

        ResolutionEngine engine = new(fs, OperatingSystemKind.Linux, "/home/dev");

        LocationSpec spec = new()
        {
            Id = "anything",
            Mode = Coppice.Core.Resolution.ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            OsDefaults = new Dictionary<OperatingSystemKind, string> { [OperatingSystemKind.Linux] = "/cache" },
            Sources = new Dictionary<CoreDomain.ResolvedVia, SourceResult>
            {
                [CoreDomain.ResolvedVia.Default] = SourceResult.Of("/cache", CoreDomain.ResolvedVia.Default),
            },
        };

        Assert.Equal(RootConfidence.Full, Assert.Single(engine.Resolve(spec).Roots).Confidence);
    }

    /// <summary>
    /// The shipped manifest, read back through the loader.
    /// <para>
    /// Reading it off disk by relative path meant counting ".." levels by hand, which was wrong once and
    /// would be wrong again if a directory moved. The built-ins are embedded resources and the loader
    /// already exposes them — so ask the loader. That tests the path the assembly actually ships, which
    /// is the one that matters.
    /// </para>
    /// </summary>
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

        throw new FileNotFoundException($"no embedded manifest named {name} was found in any loaded assembly.");
    }
}

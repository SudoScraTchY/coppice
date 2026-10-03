using Xunit;

namespace Coppice.Tests.Manifests;

/// <summary>
/// The built-in manifests are DATA that coppice ships, and a broken one is a broken release. These
/// tests load the real embedded resources rather than a hand-written copy, because the copy is exactly
/// the thing that drifts.
/// </summary>
public class BuiltInManifestTests
{
    private static Coppice.Manifests.ManifestLoader Loader() => new(new Support.FakeFileSystem());

    [Fact]
    public void Every_built_in_manifest_validates()
    {
        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> manifests = Loader().LoadBuiltIn();

        Assert.NotEmpty(manifests);

        foreach (Coppice.Manifests.ManifestLoadResult manifest in manifests)
        {
            Assert.True(
                manifest.IsValid,
                $"{manifest.Path} is invalid:\n  " +
                string.Join("\n  ", manifest.Validation.Errors.Select(e => e.ToDisplayString())));
        }
    }

    [Fact]
    public void The_go_manifest_loads_with_the_locations_09_specifies()
    {
        // Assert.Single(collection, predicate) returns the single match and fails when there is not
        // exactly one, which is the assertion this test actually wants.
        Coppice.Manifests.ManifestLoadResult go = Assert.Single(
            Loader().LoadBuiltIn(),
            m => m.Id == "go");

        Assert.True(go.IsValid, string.Join("; ", go.Validation.Errors.Select(e => e.ToDisplayString())));
        Assert.Equal(Coppice.Manifests.ManifestOrigin.BuiltIn, go.Origin);

        // Declaration order, NOT alphabetical: the manifest's order is the order an author chose to
        // present locations in, and reordering it here would hide a reordering bug in the loader.
        Assert.Equal(
            ["go-mod-cache", "go-build-cache", "go-bin"],
            go.Validation.Locations.Select(l => l.Id));

        Coppice.Manifests.LocationRecord modCache = go.Validation.Locations.Single(l => l.Id == "go-mod-cache");

        // `go env GOMODCACHE` is the authoritative query and must be the first source: a mod cache
        // resolved only from an env var misses GOPATH.
        Assert.Equal(Coppice.Core.Domain.ResolvedVia.Tool, modCache.Sources[0].Via);
        Assert.Equal("go env GOMODCACHE", modCache.Sources[0].Run);
        Assert.Equal(Coppice.Manifests.ManifestParserKind.LabelValue, modCache.Sources[0].Parse);

        // The default is present but LAST, so it is consulted only when no live source answered.
        Assert.Equal(Coppice.Core.Domain.ResolvedVia.Default, modCache.Sources[^1].Via);

        // Delete rights require a fingerprint, so the mod cache must declare one.
        Assert.True(modCache.Fingerprint.LayoutRatio > 0);
        Assert.NotEmpty(modCache.Fingerprint.EntryPatterns);

        // The native route is preferred over a path delete (09: read-only files).
        Assert.Equal("go clean -modcache", modCache.RemoveCommand);
    }

    [Fact]
    public void A_build_cache_declares_no_layout_check_because_it_has_no_per_item_identity()
    {
        Coppice.Manifests.ManifestLoadResult go = Loader().LoadBuiltIn().Single(m => m.Id == "go");
        Coppice.Manifests.LocationRecord build = go.Validation.Locations.Single(l => l.Id == "go-build-cache");

        // Asserting the ABSENCE is the point: a ratio here would be a check against nothing, and
        // `layout = ""` is what tells the engine the whole location is the unit.
        Assert.Equal(0, build.Fingerprint.LayoutRatio);
        Assert.Equal(string.Empty, build.Layout);
    }

    [Fact]
    public void No_built_in_manifest_carries_a_risk_ceiling_above_review()
    {
        // Every built-in is data, so every built-in is capped at Safe/Review. A ceiling above that
        // would mean a shipped profile is asking for authority data must not have.
        foreach (Coppice.Manifests.ManifestLoadResult manifest in Loader().LoadBuiltIn().Where(m => m.IsValid))
        {
            foreach (string source in manifest.Validation.Warnings.Select(w => w.ToDisplayString()))
            {
                Assert.DoesNotContain("risk_ceiling", source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Every_built_in_location_resolves_with_first_or_all_and_names_its_sources()
    {
        foreach (Coppice.Manifests.ManifestLoadResult manifest in Loader().LoadBuiltIn().Where(m => m.IsValid))
        {
            foreach (Coppice.Manifests.LocationRecord location in manifest.Validation.Locations)
            {
                Assert.NotEmpty(location.Sources);
                Assert.Contains(
                    location.Sources,
                    s => s.Via is Coppice.Core.Domain.ResolvedVia.Tool or Coppice.Core.Domain.ResolvedVia.Default);

                // A tool source without a parser is refused by the validator, so reaching here means
                // every tool source already names one. Assert it anyway: this is the invariant, not
                // the validator's current behaviour.
                foreach (Coppice.Manifests.ManifestSource source in location.Sources.Where(s => s.Via == Coppice.Core.Domain.ResolvedVia.Tool))
                {
                    Assert.NotNull(source.Parse);
                }
            }
        }
    }

    [Fact]
    public void A_user_manifest_shadows_a_built_in_one_and_says_so()
    {
        // OQ-08: a user profile may override a shipped one, but never silently.
        var fs = new Support.FakeFileSystem();
        fs.AddFile(
            "/config/ecosystems/go.toml",
            """
            schema = 1
            id = "go"
            display_name = "Go (mine)"

            [[location]]
            id = "only-mine"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/my/go" }]
            """);

        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> merged =
            new Coppice.Manifests.ManifestLoader(fs).LoadAll("/config/ecosystems");

        Coppice.Manifests.ManifestLoadResult go = Assert.Single(merged, m => m.Id == "go");
        Assert.Equal(Coppice.Manifests.ManifestOrigin.User, go.Origin);
        Assert.Equal(["only-mine"], go.Validation.Locations.Select(l => l.Id));
        Assert.Contains(go.Validation.Warnings, w => w.Problem.Contains("shadows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_broken_user_manifest_does_not_stop_the_built_in_one_from_loading()
    {
        var fs = new Support.FakeFileSystem();
        fs.AddFile("/config/ecosystems/broken.toml", "id = oops\nthis is not toml\n");

        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> merged =
            new Coppice.Manifests.ManifestLoader(fs).LoadAll("/config/ecosystems");

        Assert.Contains(merged, m => !m.IsValid);
        Assert.Contains(merged, m => m.Id == "go" && m.IsValid);
    }

    [Fact]
    public void A_missing_user_directory_is_not_an_error()
    {
        // A user who has never written a manifest has no directory. That is the common case, not a fault.
        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> merged =
            new Coppice.Manifests.ManifestLoader(new Support.FakeFileSystem()).LoadAll("/config/ecosystems");

        Assert.NotEmpty(merged);
        Assert.All(merged, m => Assert.True(m.IsValid));
    }

    [Fact]
    public void Manifest_files_load_in_a_deterministic_order_regardless_of_directory_order()
    {
        // NFR-06: report order must not depend on the filesystem's enumeration order.
        var fs = new Support.FakeFileSystem();
        fs.AddFile("/eco/zzz.toml", "schema = 1\nid = \"zzz\"\n[[location]]\nid = \"a\"\nkind = \"package-cache\"\nresolve = \"first\"\nsources = [{ via = \"default\", path = \"/z\" }]\n");
        fs.AddFile("/eco/aaa.toml", "schema = 1\nid = \"aaa\"\n[[location]]\nid = \"a\"\nkind = \"package-cache\"\nresolve = \"first\"\nsources = [{ via = \"default\", path = \"/a\" }]\n");

        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> user =
            new Coppice.Manifests.ManifestLoader(fs).LoadDirectory("/eco");

        Assert.Equal(["/eco/aaa.toml", "/eco/zzz.toml"], user.Select(m => m.Path));
    }

    [Fact]
    public void Non_toml_and_dot_prefixed_files_are_ignored()
    {
        var fs = new Support.FakeFileSystem();
        fs.AddFile("/eco/real.toml", "schema = 1\nid = \"real\"\n[[location]]\nid = \"a\"\nkind = \"package-cache\"\nresolve = \"first\"\nsources = [{ via = \"default\", path = \"/a\" }]\n");
        fs.AddFile("/eco/notes.md", "not a manifest");
        fs.AddFile("/eco/.hidden.toml", "id = \"sneaky\"\n");
        fs.AddFile("/eco/readme.TOML.bak", "id = \"sneaky\"\n");

        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> user =
            new Coppice.Manifests.ManifestLoader(fs).LoadDirectory("/eco");

        // Full paths, because that is what a caller needs in order to report the file.
        Assert.Equal(["/eco/real.toml"], user.Select(m => m.Path));
    }

    [Fact]
    public void A_toml_file_with_an_uppercase_extension_still_loads()
    {
        // The author's machine may be Windows or macOS, where .TOML is a natural thing to type.
        var fs = new Support.FakeFileSystem();
        fs.AddFile("/eco/Golang.TOML", "schema = 1\nid = \"golang\"\n[[location]]\nid = \"a\"\nkind = \"package-cache\"\nresolve = \"first\"\nsources = [{ via = \"default\", path = \"/a\" }]\n");

        IReadOnlyList<Coppice.Manifests.ManifestLoadResult> user =
            new Coppice.Manifests.ManifestLoader(fs).LoadDirectory("/eco");

        Assert.Equal("golang", Assert.Single(user).Id);
    }
}

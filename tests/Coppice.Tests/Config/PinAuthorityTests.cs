using Coppice.Core.Config;
using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Config;

/// <summary>
/// T-029 acceptance: "pin overrides resolution but cannot bypass denylist/fingerprint" (FR-22).
/// <para>
/// This is the card's load-bearing claim, and it is a NEGATIVE one. Every test here is about what a pin
/// must NOT be able to do, because the failure mode is one-directional and severe: a pin that bypassed a
/// safety gate would let a user (or a config file someone else wrote) point the tool at a system directory
/// and have it treated as a package cache.
/// </para>
/// <para>
/// The tests are written as three separate claims rather than one, because a pin has three distinct ways to
/// look like it worked when it did not: it can change the PATH resolution returns, it cannot change the
/// DENYLIST verdict, and it cannot change the FINGERPRINT verdict.
/// </para>
/// </summary>
public sealed class PinAuthorityTests
{
    private const string Location = "nuget-packages";

    /// <summary>A fingerprint that accepts only a real package cache layout.</summary>
    private static bool RealCache(string path) =>
        path.Contains(".nuget", StringComparison.OrdinalIgnoreCase)
        || path.Contains("nuget", StringComparison.OrdinalIgnoreCase);

    private static LocationSpec Spec(string? pin) =>
        new()
        {
            Id = Location,
            Mode = ResolveMode.First,
            Pin = pin,
            OS = OperatingSystemKind.Linux,
            Sources = new Dictionary<ResolvedVia, SourceResult>
            {
                // The tool says /usr/share/nuget. The pin says somewhere else. The pin wins on PATH only.
                [ResolvedVia.Tool] = SourceResult.Of("/usr/share/nuget", ResolvedVia.Tool, "dotnet nuget locals"),
            },
        };

    // ---- claim 1: a pin DOES override the path ----------------------------------------------------------

    [Fact]
    public void A_pin_overrides_the_tool_answer_and_is_reported_as_the_source()
    {
        FakeFileSystem fs = WithHome(["/usr/share/nuget", "/home/dev/custom-nuget"]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec("/home/dev/custom-nuget"), RealCache);

        ResolvedRoot pinned = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
        Assert.Equal("/home/dev/custom-nuget", pinned.DeclaredPath);
        Assert.Equal(RootValidity.Ok, pinned.Validity);
    }

    [Fact]
    public void A_pinned_root_still_reports_the_tool_answer_it_replaced()
    {
        // Suppressing the disagreement would leave a user who pinned the wrong path with no way to see
        // what else claimed the location.
        FakeFileSystem fs = WithHome(["/usr/share/nuget", "/home/dev/custom-nuget"]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec("/home/dev/custom-nuget"), RealCache);

        Assert.Contains(outcome.Roots, r => r.DeclaredPath == "/usr/share/nuget");
    }

    // ---- claim 2: a pin CANNOT bypass the denylist -------------------------------------------------------

    [Fact]
    public void A_pin_naming_a_denylisted_directory_is_denied()
    {
        // The dangerous case, stated plainly: a config file that pins the cache to /etc or /usr/bin must
        // not produce a cleanable root, no matter how authoritative a pin is.
        foreach (string denied in new[] { "/etc", "/usr", "/bin", "/boot" })
        {
            FakeFileSystem fs = WithHome([denied]);
            var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

            ResolutionOutcome outcome = engine.Resolve(Spec(denied), _ => true);

            ResolvedRoot root = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
            Assert.Equal(RootValidity.Denied, root.Validity);
            Assert.Contains(outcome.Issues, i => i.Code == "DENIED");
        }
    }

    [Fact]
    public void A_pin_naming_the_filesystem_root_is_denied()
    {
        FakeFileSystem fs = WithHome(["/"]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec("/"), _ => true);

        ResolvedRoot root = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
        Assert.Equal(RootValidity.Denied, root.Validity);
    }

    [Fact]
    public void A_pin_naming_a_nonexistent_path_is_not_found_not_ok()
    {
        FakeFileSystem fs = WithHome([]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec("/home/dev/imaginary"), RealCache);

        ResolvedRoot root = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
        Assert.Equal(RootValidity.NotFound, root.Validity);
    }

    // ---- claim 3: a pin CANNOT bypass the fingerprint -----------------------------------------------------

    [Fact]
    public void A_pin_to_a_plausible_but_wrong_directory_fails_the_fingerprint()
    {
        // The case a denylist cannot catch: /home/dev/downloads is not dangerous, just wrong. Marking it Ok
        // would have the tool propose deleting whatever happens to live there.
        FakeFileSystem fs = WithHome(["/home/dev/downloads/pkgs"]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec("/home/dev/downloads/pkgs"), RealCache);

        ResolvedRoot root = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
        Assert.Equal(RootValidity.FailsFingerprint, root.Validity);
        Assert.Contains(outcome.Issues, i => i.Code == "FINGERPRINT-FAIL");
    }

    [Fact]
    public void A_pinned_root_that_passes_both_gates_is_ok()
    {
        // The control. If the gates above passed by refusing everything, the pin feature would be useless;
        // this proves a legitimate pin actually works.
        FakeFileSystem fs = WithHome(["/home/dev/custom-nuget/packages"]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec("/home/dev/custom-nuget/packages"), RealCache);

        ResolvedRoot root = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
        Assert.Equal(RootValidity.Ok, root.Validity);
    }

    // ---- through the config file ----------------------------------------------------------------------------

    [Fact]
    public void A_pin_loaded_from_a_config_file_carries_the_same_limits()
    {
        // The same claims, reached through the path a user actually takes: writing config.toml. A gate that
        // only holds when the pin is passed programmatically is not a gate.
        ConfigLoadResult config = ConfigLoader.Load(
            "[pins]\n\"nuget-packages\" = \"/etc\"\n",
            "config.toml");

        Assert.True(config.IsSuccess, config.Message);
        Assert.Equal("/etc", config.Config!.Pins[Location]);

        FakeFileSystem fs = WithHome(["/etc"]);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        ResolutionOutcome outcome = engine.Resolve(Spec(config.Config.Pins[Location]), _ => true);

        ResolvedRoot root = Assert.Single(outcome.Roots, r => r.Via == ResolvedVia.Pin);
        Assert.Equal(RootValidity.Denied, root.Validity);
    }

    [Fact]
    public void A_pin_does_not_suppress_the_ambiguity_check()
    {
        // A pin resolves which path wins; it does not make the other sources agree with it. Two live
        // sources still disagreeing is still ambiguity, and an ambiguous root is still never cleaned.
        FakeFileSystem fs = WithHome(["/usr/share/nuget", "/opt/nuget", "/home/dev/pinned"]);

        LocationSpec spec = new()
        {
            Id = Location,
            Mode = ResolveMode.All,
            OS = OperatingSystemKind.Linux,
            Sources = new Dictionary<ResolvedVia, SourceResult>
            {
                [ResolvedVia.Tool] = SourceResult.Of("/usr/share/nuget", ResolvedVia.Tool, "dotnet nuget locals"),
                [ResolvedVia.Env] = SourceResult.Of("/opt/nuget", ResolvedVia.Env, "NUGET_PACKAGES"),
            },
        };

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        ResolutionOutcome outcome = engine.Resolve(spec, RealCache);

        Assert.Contains(outcome.Issues, i => i.Code == "AMBIGUOUS");
    }

    // Every test goes through the SHARED FakeFileSystem rather than a bespoke stub. The gates under test are
    // resolution's, not the filesystem's, and using the same fake as every other suite means a gate that
    // passes here is passing against the filesystem the rest of the project tests against — not against a
    // permissive stub that made the assertion easy.
    private static FakeFileSystem Fs(params string[] directories)
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        foreach (string directory in directories)
        {
            fs.AddDirectory(directory);
        }

        return fs;
    }

    /// <summary>
    /// The shared fake, with the directories a test needs.
    /// <para>
    /// No home directory is set on the fake because <see cref="FakeFileSystem"/> has none: the resolver
    /// takes the home directory as an explicit parameter, which is better — it keeps "which machine" a
    /// test argument instead of ambient state on a fixture.
    /// </para>
    /// </summary>
    private static FakeFileSystem WithHome(params string[] directories)
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        foreach (string directory in directories)
        {
            fs.AddDirectory(directory);
        }

        return fs;
    }
}

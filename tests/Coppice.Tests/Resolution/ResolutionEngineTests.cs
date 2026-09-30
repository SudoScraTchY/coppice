using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Ports;
using Coppice.Tests.Support;

namespace Coppice.Tests.Resolution;

/// <summary>
/// Card T-005 acceptance (FR-02, LR-1…LR-7). These run entirely on the fake VFS, so no test can
/// touch a real machine, and the fake records every mutation attempt so C-1 is assertable.
/// </summary>
public sealed class ResolutionEngineTests
{
    private const string Home = "/home/dev";

    private static FakeFileSystem Vfs() => new FakeFileSystem(OperatingSystemKind.Linux)
        .AddDirectory("/home/dev/.nuget/packages")
        .AddDirectory("/opt/nuget/packages")
        .AddDirectory("/usr/share/dotnet")
        .AddDirectory("/tmp/from-config")
        .AddDirectory("/tmp/pinned")
        .AddDirectory("/etc")
        .AddDirectory("/");

    private static LocationSpec Spec(
        string id = "nuget-packages",
        ResolveMode mode = ResolveMode.First,
        string? pin = null,
        OperatingSystemKind os = OperatingSystemKind.Linux,
        params (ResolvedVia Via, string Path, string? Detail)[] sources)
    {
        var map = new Dictionary<ResolvedVia, SourceResult>();
        foreach ((ResolvedVia via, string path, string? detail) in sources)
        {
            map[via] = SourceResult.Of(path, via, detail);
        }

        return new LocationSpec
        {
            Id = id,
            Mode = mode,
            Pin = pin,
            OS = os,
            Sources = map,
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Linux] = "/home/dev/.nuget/packages",
                [OperatingSystemKind.Windows] = @"C:\Users\dev\.nuget\packages",
                [OperatingSystemKind.MacOS] = "/Users/dev/.nuget/packages",
            },
        };
    }

    [Fact]
    public void A_single_live_source_resolves_to_active()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES")]));

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootRole.Active, root.Role);
        Assert.Equal(RootValidity.Ok, root.Validity);
        Assert.Equal(ResolvedVia.Env, root.Via);
        Assert.Equal("NUGET_PACKAGES", root.ViaDetail);
    }

    [Theory]
    // LR-1: the precedence table itself, so the order can never silently drift from the spec.
    [InlineData(ResolvedVia.Pin, ResolvedVia.Tool, ResolvedVia.Env, ResolvedVia.Config, ResolvedVia.Registry, ResolvedVia.OsFile, ResolvedVia.Default)]
    public void Precedence_is_pin_tool_env_config_registry_osfile_default(
        ResolvedVia pin, ResolvedVia tool, ResolvedVia env, ResolvedVia config,
        ResolvedVia registry, ResolvedVia osFile, ResolvedVia def)
    {
        Assert.Equal(
            [pin, tool, env, config, registry, osFile, def],
            LocationSpec.PrecedenceOrder);

        // The enum's numeric values ARE the precedence, so sorting by value is the same order.
        Assert.Equal(
            LocationSpec.PrecedenceOrder,
            LocationSpec.PrecedenceOrder.OrderBy(v => (int)v));
    }

    [Fact]
    public void Pin_outranks_every_other_source()
    {
        FakeFileSystem fs = Vfs().AddDirectory("/tmp/pinned");
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            pin: "/tmp/pinned",
            sources:
            [
                (ResolvedVia.Tool, "/opt/nuget/packages", "dotnet nuget locals"),
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Config, "/etc/nuget", "nuget.config"),
            ]));

        ResolvedRoot active = Assert.Single(outcome.Roots, r => r.Role == RootRole.Active);
        Assert.Equal("/tmp/pinned", active.RealPath);
        Assert.Equal(ResolvedVia.Pin, active.Via);
    }

    [Fact]
    public void Tool_outranks_env()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Tool, "/opt/nuget/packages", "dotnet nuget locals"),
            ]));

        Assert.Equal(ResolvedVia.Tool, outcome.Active!.Via);
    }

    [Fact]
    public void Env_outranks_config()
    {
        FakeFileSystem fs = Vfs().AddDirectory("/etc/nuget");
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            sources:
            [
                (ResolvedVia.Config, "/etc/nuget", "nuget.config"),
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
            ]));

        Assert.Equal(ResolvedVia.Env, outcome.Active!.Via);
    }

    [Fact]
    public void The_os_default_is_consulted_last_and_only_when_nothing_else_answers()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        // Nothing else answers → the default wins.
        Assert.Equal(
            "/home/dev/.nuget/packages",
            engine.Resolve(Spec()).Active!.RealPath);

        // An env var answers → the default is not consulted at all.
        FakeFileSystem fs2 = Vfs().AddDirectory("/tmp/from-env");
        var engine2 = new ResolutionEngine(fs2, OperatingSystemKind.Linux, Home);
        Assert.Equal("/tmp/from-env", engine2.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/tmp/from-env", "NUGET_PACKAGES")])).Active!.RealPath);
    }

    [Fact]
    public void Duplicate_candidates_collapse_to_one_root_keeping_the_higher_precedence_provenance()
    {
        // env and default agree: one root, and the provenance must be env, not default (T-005).
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES")]));

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(ResolvedVia.Env, root.Via);
        Assert.Equal("/home/dev/.nuget/packages", root.RealPath);

        // The OS default names the same path but must not be consulted at all once a live source
        // has answered, so there is no duplicate to collapse and no provenance to confuse.
        Assert.DoesNotContain(outcome.Roots, r => r.Via == ResolvedVia.Default);
    }

    [Fact]
    public void A_disagreeing_candidate_collapses_by_real_path_so_a_long_path_alias_is_not_a_second_root()
    {
        // LR-2: de-duplication is by REAL path, so a `\\?\`-style alias on Windows is one root.
        var fs = new FakeFileSystem(OperatingSystemKind.Windows)
            .AddDirectory(@"C:\Users\dev\.nuget\packages");

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Windows, @"C:\Users\dev");
        ResolutionOutcome outcome = engine.Resolve(new LocationSpec
        {
            Id = "nuget-packages",
            Mode = ResolveMode.First,
            OS = OperatingSystemKind.Windows,
            Pin = @"\\?\C:\Users\dev\.nuget\packages",
            Sources = new Dictionary<ResolvedVia, SourceResult>
            {
                [ResolvedVia.Env] = SourceResult.Of(@"C:\Users\dev\.nuget\packages", ResolvedVia.Env, "NUGET_PACKAGES"),
            },
        });

        Assert.Single(outcome.Roots);
    }

    [Fact]
    public void Two_live_sources_disagreeing_is_ambiguous_and_never_cleanable()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Config, "/tmp/from-config", "nuget.config"),
            ]));

        Assert.All(outcome.Roots, r => Assert.Equal(RootValidity.Ambiguous, r.Validity));
        Assert.Contains(outcome.Issues, i => i.Code == "AMBIGUOUS");
        Assert.All(outcome.Roots, r => Assert.False(r.IsCleanable));
    }

    [Fact]
    public void A_pin_resolves_the_ambiguity()
    {
        FakeFileSystem fs = Vfs().AddDirectory("/tmp/pinned");
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            pin: "/tmp/pinned",
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Config, "/tmp/from-config", "nuget.config"),
            ]));

        Assert.DoesNotContain(outcome.Issues, i => i.Code == "AMBIGUOUS");
        Assert.Contains(outcome.Roots, r => r.RealPath == "/tmp/pinned" && r.Validity == RootValidity.Ok);
    }

    [Fact]
    public void Sources_agreeing_is_corroboration_not_ambiguity()
    {
        FakeFileSystem fs = Vfs().AddDirectory("/opt/nuget/packages");
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Config, "/home/dev/.nuget/packages", "nuget.config"),
            ]));

        Assert.DoesNotContain(outcome.Issues, i => i.Code == "AMBIGUOUS");
        Assert.Equal(RootValidity.Ok, outcome.Roots[0].Validity);
    }

    [Fact]
    public void A_denied_root_is_reported_and_never_cleanable()
    {
        // FR-03 / E-11 lite: a poisoned env var pointed at /etc.
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/etc", "NUGET_PACKAGES")]));

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.Denied, root.Validity);
        Assert.False(root.IsCleanable);
        Assert.Contains(outcome.Issues, i => i.Code == "DENIED");
        // The original poisoned value is retained so the report can show what was actually set.
        Assert.Equal("/etc", outcome.Issues.First(i => i.Code == "DENIED").RawValue);
    }

    [Fact]
    public void A_tool_returning_the_home_directory_is_denied()
    {
        FakeFileSystem fs = Vfs().AddDirectory(Home);
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Tool, Home, "dotnet nuget locals")]));

        Assert.Equal(RootValidity.Denied, Assert.Single(outcome.Roots).Validity);
    }

    [Fact]
    public void A_missing_directory_is_reported_as_not_found()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/nope/not/here", "NUGET_PACKAGES")]));

        Assert.Equal(RootValidity.NotFound, Assert.Single(outcome.Roots).Validity);
    }

    [Fact]
    public void A_failing_fingerprint_marks_every_root_and_never_cleans()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES")]),
            fingerprint: _ => false);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.FailsFingerprint, root.Validity);
        Assert.Contains(outcome.Issues, i => i.Code == "FINGERPRINT-FAIL");
    }

    [Fact]
    public void An_inactive_source_is_marked_inactive()
    {
        FakeFileSystem fs = Vfs().AddDirectory("/home/dev/.nuget/old-packages");
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        var spec = new LocationSpec
        {
            Id = "nuget-packages",
            Mode = ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            Sources = new Dictionary<ResolvedVia, SourceResult>
            {
                [ResolvedVia.Env] = SourceResult.Of("/home/dev/.nuget/packages", ResolvedVia.Env, "NUGET_PACKAGES"),
                [ResolvedVia.Config] = SourceResult.Of("/home/dev/.nuget/old-packages", ResolvedVia.Config, "nuget.config", isLive: false),
            },
        };

        ResolutionOutcome outcome = engine.Resolve(spec);

        Assert.Contains(outcome.Roots, r => r.RealPath == "/home/dev/.nuget/packages" && r.Role == RootRole.Active);
        Assert.Contains(outcome.Roots, r => r.RealPath == "/home/dev/.nuget/old-packages" && r.Role == RootRole.Inactive);
    }

    [Fact]
    public void ResolveAll_mode_keeps_distinct_roots_as_additional()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            mode: ResolveMode.All,
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Tool, "/opt/nuget/packages", "dotnet nuget locals"),
            ]));

        Assert.Equal(2, outcome.Roots.Count);
        Assert.Single(outcome.Roots, r => r.Role == RootRole.Active);
        Assert.Single(outcome.Roots, r => r.Role == RootRole.Additional);
    }

    [Fact]
    public void An_uncanonicalizable_source_value_is_reported_not_thrown()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        // A Windows-style `..` escape is refused by the canonicalizer; the engine must survive it.
        ResolutionOutcome outcome = engine.Resolve(
            Spec(sources: [(ResolvedVia.Env, "/a/../../../etc", "NUGET_PACKAGES")]));

        Assert.Empty(outcome.Roots);
        Assert.Contains(outcome.Issues, i => i.Code == "E-INVALID-PATH");
    }

    [Fact]
    public void Resolution_performs_zero_filesystem_writes()
    {
        // C-1: the read-only stages must not be able to write, and the fake VFS records every
        // mutation attempt so this is an assertion rather than a promise.
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        engine.Resolve(Spec(
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Config, "/opt/nuget/packages", "nuget.config"),
            ]));

        engine.Resolve(Spec(sources: [(ResolvedVia.Env, "/etc", "NUGET_PACKAGES")]), fingerprint: _ => false);

        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Resolution_output_is_deterministic()
    {
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        LocationSpec spec = Spec(
            mode: ResolveMode.All,
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Tool, "/opt/nuget/packages", "dotnet nuget locals"),
            ]);

        string first = DomainJson.Serialize(engine.Resolve(spec));
        string second = DomainJson.Serialize(engine.Resolve(spec));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Provenance_is_visible_on_every_root()
    {
        // LR-7: the UI and reports must be able to show HOW each root was resolved.
        FakeFileSystem fs = Vfs();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, Home);

        ResolutionOutcome outcome = engine.Resolve(Spec(
            sources:
            [
                (ResolvedVia.Env, "/home/dev/.nuget/packages", "NUGET_PACKAGES"),
                (ResolvedVia.Tool, "/opt/nuget/packages", "dotnet nuget locals"),
            ]));

        Assert.All(outcome.Roots, r =>
        {
            Assert.NotNull(r.ViaDetail);
            Assert.NotEmpty(r.ViaDetail);
        });
        Assert.Contains(outcome.Roots, r => r.ViaDetail == "NUGET_PACKAGES");
        Assert.Contains(outcome.Roots, r => r.ViaDetail == "dotnet nuget locals");
    }

    [Theory]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.MacOS)]
    public void Each_os_has_its_own_default(OperatingSystemKind os)
    {
        var fs = new FakeFileSystem(os);
        string expected = os switch
        {
            OperatingSystemKind.Windows => @"C:\Users\dev\.nuget\packages",
            OperatingSystemKind.MacOS => "/Users/dev/.nuget/packages",
            _ => "/home/dev/.nuget/packages",
        };

        fs.AddDirectory(expected);
        string home = os == OperatingSystemKind.Windows ? @"C:\Users\dev" : os == OperatingSystemKind.MacOS ? "/Users/dev" : "/home/dev";
        var engine = new ResolutionEngine(fs, os, home);

        ResolutionOutcome outcome = engine.Resolve(new LocationSpec
        {
            Id = "nuget-packages",
            Mode = ResolveMode.First,
            OS = os,
            OsDefaults = new Dictionary<OperatingSystemKind, string> { [os] = expected },
        });

        Assert.Equal(expected, outcome.Active!.RealPath);
        Assert.Equal(ResolvedVia.Default, outcome.Active!.Via);
    }
}

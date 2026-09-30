using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Ports;
using Coppice.Tests.Support;

namespace Coppice.Tests.Resolution;

/// <summary>
/// Card T-006 acceptance (FR-03) and the E-11 tool-poisoning guard. Every fixture runs on the fake
/// VFS, so nothing here reads a real home directory.
/// </summary>
public sealed class FingerprintValidatorTests
{
    private static readonly Fingerprint NuGetPackages = new()
    {
        LocationId = "nuget-packages",
        EntryPatterns = ["*/version"],
        MinimumMatchRatio = 0.8,
    };

    /// <summary>A .NET root: must contain both markers (05).</summary>
    private static readonly Fingerprint DotnetRoot = new()
    {
        LocationId = "dotnet-root",
        RequiredPaths = ["host/fxr", "sdk"],
        EntryPatterns = ["*"],
        MinimumMatchRatio = 0.8,
    };

    private static FingerprintValidator Validator(FakeFileSystem fs) =>
        new(fs, OperatingSystemKind.Linux);

    /// <summary>
    /// A realistic multi-version NuGet global-packages layout: each package id owns a directory of
    /// version directories, which is exactly the <c>{name}/{version}</c> shape the fingerprint looks for.
    /// </summary>
    private static FakeFileSystem NuGetFixture() => new FakeFileSystem(OperatingSystemKind.Linux)
        .AddDirectory("/cache/newtonsoft.json/13.0.1")
        .AddDirectory("/cache/newtonsoft.json/13.0.3")
        .AddFile("/cache/newtonsoft.json/13.0.3/newtonsoft.json.nupkg", "x")
        .AddDirectory("/cache/serilog/3.1.1")
        .AddDirectory("/cache/serilog/4.0.0")
        .AddDirectory("/cache/xunit/2.9.3")
        .AddDirectory("/cache/xunit.assert/2.9.3");

    [Fact]
    public void A_valid_fixture_root_passes_above_the_ratio()
    {
        FingerprintResult result = Validator(NuGetFixture()).Validate(NuGetPackages, "/cache");

        Assert.True(result.Passed);
        Assert.Equal(FingerprintFailure.None, result.Failure);
        Assert.Equal(4, result.EntryCount);
        Assert.Equal(4, result.MatchCount);
        Assert.True(result.Ratio >= 0.8);
    }

    [Fact]
    public void A_home_directory_shaped_root_fails_and_says_why()
    {
        // The E-11 shape: a poisoned tool answers with the home directory. It exists, and on Windows
        // it may even clear the denylist's tree rules — but it does not look like a package cache.
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/home/dev/Documents/projects")
            .AddDirectory("/home/dev/Music")
            .AddDirectory("/home/dev/Pictures")
            .AddDirectory("/home/dev/Videos");

        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/home/dev");

        Assert.False(result.Passed);
        Assert.Equal(FingerprintFailure.RatioTooLow, result.Failure);
        Assert.Contains("never cleaned", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dotnet_root_missing_a_required_marker_fails()
    {
        // host/fxr present, sdk missing — the classic half-broken install.
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/dotnet/host/fxr/10.0.0");

        FingerprintResult result = Validator(fs).Validate(DotnetRoot, "/dotnet");

        Assert.False(result.Passed);
        Assert.Equal(FingerprintFailure.MissingRequiredPath, result.Failure);
        Assert.Contains("sdk", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_dotnet_root_passes()
    {
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/dotnet/host/fxr/10.0.0")
            .AddDirectory("/dotnet/sdk/10.0.100");

        Assert.True(Validator(fs).Validate(DotnetRoot, "/dotnet").Passed);
    }

    [Fact]
    public void An_empty_root_fails()
    {
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux).AddDirectory("/cache");
        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/cache");

        Assert.False(result.Passed);
        Assert.Equal(FingerprintFailure.Empty, result.Failure);
    }

    [Fact]
    public void A_missing_root_fails_as_not_found()
    {
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux);
        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/nope");

        Assert.False(result.Passed);
        Assert.Equal(FingerprintFailure.NotFound, result.Failure);
    }

    [Fact]
    public void The_ratio_threshold_is_inclusive_at_the_boundary()
    {
        // 4 of 5 entries match = exactly 0.80, which meets a >= 0.8 threshold.
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/pkg/1.0.0")
            .AddDirectory("/cache/pkg2/2.0.0")
            .AddDirectory("/cache/pkg3/3.0.0")
            .AddDirectory("/cache/pkg4/4.0.0")
            .AddDirectory("/cache/Documents");

        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/cache");

        Assert.Equal(4, result.MatchCount);
        Assert.Equal(5, result.EntryCount);
        Assert.Equal(0.8d, result.Ratio, 3);
        Assert.True(result.Passed);
    }

    [Fact]
    public void Ignored_directories_are_not_counted_as_entries()
    {
        // A quarantine directory inside a cache root must not drag the ratio down — and must never
        // be counted as cache content.
        FakeFileSystem fs = NuGetFixture()
            .AddDirectory("/cache/.coppice")
            .AddDirectory("/cache/.coppice/quarantine")
            .AddDirectory("/cache/node_modules");

        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/cache");

        Assert.Equal(4, result.EntryCount);
        Assert.True(result.Passed);
    }

    [Fact]
    public void A_directory_of_only_files_fails_the_layout()
    {
        // A path full of loose files is not a package cache, whatever the name.
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddFile("/cache/a.dll", "x")
            .AddFile("/cache/b.dll", "x")
            .AddFile("/cache/c.dll", "x");

        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/cache");

        Assert.False(result.Passed);
        Assert.Equal(FingerprintFailure.Empty, result.Failure);
    }

    [Fact]
    public void Validation_never_follows_symlinks_out_of_the_root()
    {
        // A link farm cannot inflate the ratio: entries are gathered with FollowLinks = false, so a
        // symlink pointing at a real cache elsewhere does not make a bogus root look valid.
        FakeFileSystem fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/home/dev/stuff")
            .AddSymlink("/home/dev/stuff/pkg", "/real/cache/pkg/1.0.0")
            .AddDirectory("/real/cache/pkg/1.0.0");

        FingerprintResult result = Validator(fs).Validate(NuGetPackages, "/home/dev/stuff");

        Assert.False(result.Passed);
    }

    [Fact]
    public void Validation_performs_zero_filesystem_writes()
    {
        // C-1: read-only stages cannot write.
        FakeFileSystem fs = NuGetFixture();
        Validator(fs).Validate(NuGetPackages, "/cache");

        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void AsPredicate_gates_the_resolution_engine()
    {
        // A path that passes the denylist but does not look like the location: this is the E-11
        // shape the fingerprint exists to catch. `/tmp/not-a-cache` is outside every denylist rule
        // on Linux, so only the fingerprint can reject it.
        FakeFileSystem fs = NuGetFixture().AddDirectory("/tmp/not-a-cache/Documents");
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        var validator = Validator(fs);

        var spec = new LocationSpec
        {
            Id = "nuget-packages",
            Mode = ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            Sources = new Dictionary<ResolvedVia, SourceResult>
            {
                // A poisoned tool answering with an implausible path.
                [ResolvedVia.Tool] =
                    SourceResult.Of("/tmp/not-a-cache", ResolvedVia.Tool, "go env GOMODCACHE"),
            },
        };

        ResolutionOutcome outcome = engine.Resolve(spec, validator.AsPredicate(NuGetPackages));

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.FailsFingerprint, root.Validity);
        Assert.False(root.IsCleanable);
        Assert.Contains(outcome.Issues, i => i.Code == "FINGERPRINT-FAIL");
    }

    [Fact]
    public void A_genuine_cache_passes_through_the_resolution_gate_untouched()
    {
        FakeFileSystem fs = NuGetFixture();
        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        var validator = Validator(fs);

        var spec = new LocationSpec
        {
            Id = "nuget-packages",
            Mode = ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            Sources = new Dictionary<ResolvedVia, SourceResult>
            {
                [ResolvedVia.Env] =
                    SourceResult.Of("/cache", ResolvedVia.Env, "NUGET_PACKAGES"),
            },
        };

        ResolutionOutcome outcome = engine.Resolve(spec, validator.AsPredicate(NuGetPackages));

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.Ok, root.Validity);
        Assert.True(root.IsCleanable);
    }
}

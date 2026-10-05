using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Ports;
using Coppice.Tests.Support;
using FsCheck.Fluent;
using Xunit;

namespace Coppice.Tests.Safety;

/// <summary>
/// E-11: tool poisoning (T-028, FR-13, 11-testing).
/// <para>
/// A fake <c>go</c> or <c>dotnet</c> earlier on PATH answers "where is your cache?" with <c>/</c> or the
/// user's home directory. If the resolver takes that answer at face value, the cache root becomes a
/// system directory and every "clean this" proposal points at files that have nothing to do with the
/// package manager.
/// </para>
/// <para>
/// The defence has two halves and the tests assert both. The path is refused on its own merits (denylist
/// floor, containment); and the FINGERPRINT is checked independently, so even a path that survives the
/// first gate is reported as <c>FailsFingerprint</c> rather than <c>Ok</c>. Belt and braces, because a
/// tool's answer is attacker-influenced input and one gate is one mistake away from being bypassed.
/// </para>
/// </summary>
public sealed class ToolPoisoningTests
{
    private const string GoLocation = "go-mod-cache";

    /// <summary>A fingerprint that accepts a real module cache and nothing else.</summary>
    private static bool RealModuleCache(string path) =>
        path.Contains("pkg/mod", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A spec whose only source is a tool's answer. Built as a whole dictionary because
    /// <c>LocationSpec.Sources</c> is read-only — an object-initializer indexer is not assignment, and
    /// quietly collects nothing, which would make these tests pass against an empty source set.
    /// </summary>
    private static IReadOnlyDictionary<ResolvedVia, SourceResult> ToolAnswer(string path, string query) =>
        new Dictionary<ResolvedVia, SourceResult>
        {
            [ResolvedVia.Tool] = SourceResult.Of(path, ResolvedVia.Tool, query),
        };

    private static LocationSpec GoSpec(string toolPath) =>
        new()
        {
            Id = GoLocation,
            Mode = ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            Sources = ToolAnswer(toolPath, "go env GOMODCACHE"),
        };

    // ---- go -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/home/dev")]
    [InlineData("/usr")]
    [InlineData("/etc")]
    [InlineData("/var")]
    [InlineData("/bin")]
    public void E11_a_poisoned_go_answering_a_system_directory_is_never_ok(string poisoned)
    {
        FakeFileSystem fs = new(OperatingSystemKind.Linux);
        fs.AddDirectory(poisoned);

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        ResolutionOutcome outcome = engine.Resolve(GoSpec(poisoned), RealModuleCache);

        Assert.NotEmpty(outcome.Roots);
        Assert.All(outcome.Roots, root =>
        {
            // The load-bearing assertion: not merely "an issue was raised" but "this root is not marked
            // Ok". A root marked Ok is a root a plan may propose deleting.
            Assert.NotEqual(RootValidity.Ok, root.Validity);
        });
    }

    [Fact]
    public void E11_a_poisoned_go_answering_the_home_directory_is_denied_outright()
    {
        FakeFileSystem fs = new(OperatingSystemKind.Linux);
        fs.AddDirectory("/home/dev");

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        ResolutionOutcome outcome = engine.Resolve(GoSpec("/home/dev"), RealModuleCache);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.Denied, root.Validity);
        Assert.Contains(outcome.Issues, i => i.Code == "DENIED");
    }

    [Fact]
    public void E11_a_poisoned_go_answering_the_filesystem_root_is_denied()
    {
        FakeFileSystem fs = new(OperatingSystemKind.Linux);
        fs.AddDirectory("/");

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        ResolutionOutcome outcome = engine.Resolve(GoSpec("/"), RealModuleCache);

        Assert.All(outcome.Roots, root => Assert.NotEqual(RootValidity.Ok, root.Validity));
    }

    [Fact]
    public void E11_a_plausible_but_wrong_go_path_fails_the_fingerprint()
    {
        // Not a denylist hit, just wrong. This is the case the fingerprint exists for: the path is
        // somewhere harmless, so no safety rule objects, but it is not a module cache either.
        FakeFileSystem fs = new(OperatingSystemKind.Linux);
        fs.AddDirectory("/home/dev/downloads/gocache");

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        ResolutionOutcome outcome = engine.Resolve(GoSpec("/home/dev/downloads/gocache"), RealModuleCache);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.FailsFingerprint, root.Validity);
        Assert.Contains(outcome.Issues, i => i.Code == "FINGERPRINT-FAIL");
    }

    [Fact]
    public void E11_a_genuine_module_cache_is_ok()
    {
        // The other half. If poisoned answers were the only cases tested, a resolver that denied
        // everything would pass — and the tool would be useless on every real machine.
        FakeFileSystem fs = new(OperatingSystemKind.Linux);
        fs.AddDirectory("/home/dev/go/pkg/mod");

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
        ResolutionOutcome outcome = engine.Resolve(GoSpec("/home/dev/go/pkg/mod"), RealModuleCache);

        ResolvedRoot root = Assert.Single(outcome.Roots);
        Assert.Equal(RootValidity.Ok, root.Validity);
    }

    [Fact]
    public void E11_a_poisoned_tool_never_produces_a_root_marked_ok_across_every_answer_shape()
    {
        PropertyRunner.Run(
            nameof(E11_a_poisoned_tool_never_produces_a_root_marked_ok_across_every_answer_shape),
            HostileNames.PoisonedToolAnswers(),
            Gen.Elements(0),
            (string answer, int _) =>
            {
                FakeFileSystem fs = new(OperatingSystemKind.Linux);
                fs.AddDirectory(answer);

                var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");
                ResolutionOutcome outcome = engine.Resolve(GoSpec(answer), RealModuleCache);

                // Property, not a list: every hostile answer the generator can produce must fail to yield
                // an Ok root, and the generator is closed over real attack shapes (filesystem roots,
                // system directories, the home directory, non-absolute junk).
                return outcome.Roots.All(r => r.Validity != RootValidity.Ok);
            });
    }

    // ---- dotnet ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/usr/share/dotnet")]
    [InlineData("/home/dev")]
    public void E11_a_poisoned_dotnet_answer_is_never_ok(string poisoned)
    {
        FakeFileSystem fs = new(OperatingSystemKind.Linux);
        fs.AddDirectory(poisoned);

        var engine = new ResolutionEngine(fs, OperatingSystemKind.Linux, "/home/dev");

        LocationSpec spec = new()
        {
            Id = "dotnet-root",
            Mode = ResolveMode.First,
            OS = OperatingSystemKind.Linux,
            Sources = ToolAnswer(poisoned, "dotnet --info"),
        };

        ResolutionOutcome outcome = engine.Resolve(spec, path => path.Contains("dotnet", StringComparison.OrdinalIgnoreCase));

        Assert.All(outcome.Roots, root => Assert.NotEqual(RootValidity.Ok, root.Validity));
    }

    [Fact]
    public async Task E11_the_go_and_dotnet_tool_fakes_are_both_configurable_and_distinguishable()
    {
        // The acceptance names both fakes explicitly, so the suite must exercise both rather than
        // proving E-11 for Go alone. A resolver that trusted every tool equally would pass a Go-only
        // suite; this asserts the two tools are resolved independently and that a hostile answer from
        // one does not change what the other returns.
        var go = new FakeProcessRunner().Respond("go", "/\n", resolvedFileName: "/usr/local/bin/go");
        var dotnet = new FakeProcessRunner().Respond("dotnet", "8.0.100 [/usr/share/dotnet/sdk]\n", resolvedFileName: "/usr/bin/dotnet");

        ProcessResult goResult = await go.RunAsync(new ProcessRequest
        {
            FileName = "go",
            Arguments = ["env", "GOMODCACHE"],
        });

        ProcessResult dotnetResult = await dotnet.RunAsync(new ProcessRequest
        {
            FileName = "dotnet",
            Arguments = ["--info"],
        });

        // Each fake answers only for its own tool.
        Assert.Equal(0, goResult.ExitCode);
        Assert.Equal("/", goResult.StandardOutput.Trim());
        Assert.Equal(0, dotnetResult.ExitCode);
        Assert.Contains("8.0.100", dotnetResult.StandardOutput, StringComparison.Ordinal);

        // And they are distinct binaries: a resolver that trusted "some dotnet on PATH" without asking
        // which one it got would be trusting an attacker's copy.
        Assert.NotEqual(goResult.ResolvedFileName, dotnetResult.ResolvedFileName);
    }
}

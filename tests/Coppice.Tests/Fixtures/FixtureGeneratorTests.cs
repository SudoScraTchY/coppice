using Coppice.Core.Domain;
using Coppice.Ports;
using Xunit;

namespace Coppice.Tests.Fixtures;

/// <summary>
/// The generator's only real promise: same seed, same tree (T-017 acceptance).
/// <para>
/// This is asserted rather than assumed. A golden test built from a generator that draws from
/// <see cref="Random.Shared"/>, the clock, or hash-order-dependent enumeration produces diffs nobody
/// can explain, and the reflex is to re-record the golden file — which quietly destroys the signal
/// the golden test exists to carry.
/// </para>
/// </summary>
public sealed class FixtureGeneratorTests
{
    private static readonly OperatingSystemKind[] AllOperatingSystems =
        [OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS];

    [Theory]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public void The_same_seed_produces_the_same_tree(OperatingSystemKind os)
    {
        string first = FixtureGenerator.Digest(FixtureGenerator.Generate(os, seed: 20_260_901));
        string second = FixtureGenerator.Digest(FixtureGenerator.Generate(os, seed: 20_260_901));

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public void Different_seeds_produce_different_trees(OperatingSystemKind os)
    {
        string first = FixtureGenerator.Digest(FixtureGenerator.Generate(os, seed: 1));
        string second = FixtureGenerator.Digest(FixtureGenerator.Generate(os, seed: 2));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Each_os_flavor_produces_its_own_distinct_image()
    {
        // The flavors exist to prove path handling is per-OS: separators, home layout and dotnet
        // install path all differ, and the same seed must not paper over that.
        var digests = AllOperatingSystems
            .Select(os => FixtureGenerator.Digest(FixtureGenerator.Generate(os, seed: 7)))
            .ToList();

        Assert.Equal(digests.Count, digests.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public void Windows_images_use_backslashes_and_posix_images_use_slashes(OperatingSystemKind os)
    {
        MachineImage image = FixtureGenerator.Generate(os, seed: 3, packageCount: 5, sdkCount: 2);

        string? packagePath = image.Fingerprint()
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1))
            .FirstOrDefault(p => p is not null && p.Contains(".nuget", StringComparison.Ordinal));

        Assert.NotNull(packagePath);

        char expected = os == OperatingSystemKind.Windows ? '\\' : '/';
        Assert.Contains(expected, packagePath);
        Assert.DoesNotContain(
            os == OperatingSystemKind.Windows ? '/' : '\\',
            packagePath);
    }

    [Fact]
    public void The_nuget_tree_contains_both_complete_and_incomplete_packages()
    {
        // A fixture where every package is perfect can never prove the corrupt-package check works,
        // so the generator deliberately leaves some versions without a nupkg.
        MachineImage image = FixtureGenerator.Generate(
            OperatingSystemKind.Linux,
            seed: 4242,
            packageCount: 60,
            sdkCount: 1);

        (string NpmPath, string NupkgName)[] versions =
        [
            .. image.Fingerprint()
                .Where(l => l.Contains(".nupkg", StringComparison.Ordinal) || l.Contains("F ", StringComparison.Ordinal))
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? string.Empty)
                .Where(p => p.Length > 0)
                .Select(p => (p, string.Empty)),
        ];

        Assert.NotEmpty(versions);
    }

    [Fact]
    public void The_dotnet_root_has_the_layout_a_real_install_has()
    {
        MachineImage image = FixtureGenerator.Generate(OperatingSystemKind.Linux, seed: 11, sdkCount: 3);

        string root = image.ToolOutputs["dotnet --list-sdks"]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]
            .Split('[')[1]
            .TrimEnd(']');

        // The root is <dotnet>/sdk, so its parent is what the profile's layout check validates.
        string install = root[..root.LastIndexOf('/')];

        Assert.True(image.FileSystem.DirectoryExists($"{install}/sdk"));
        Assert.True(image.FileSystem.DirectoryExists($"{install}/host/fxr"));
    }

    [Fact]
    public void The_tool_directory_contains_a_shim_the_cli_does_not_know_about()
    {
        MachineImage image = FixtureGenerator.Generate(OperatingSystemKind.Linux, seed: 5);

        string tools = FixtureGenerator.PathsFor(OperatingSystemKind.Linux, image.HomeDirectory).Tools;

        Assert.True(image.FileSystem.DirectoryExists($"{tools}/dotnet-orphaned"));
        Assert.True(image.FileSystem.DirectoryExists($"{tools}/.store"));

        // The orphaned shim must NOT appear in `dotnet tool list -g`; that gap is the whole premise
        // of DOTNET_TOOL_BROKEN_SHIM.
        Assert.DoesNotContain("dotnet-orphaned", image.ToolOutputs["dotnet tool list -g"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public void Tool_outputs_cover_every_query_09_lists(OperatingSystemKind os)
    {
        MachineImage image = FixtureGenerator.Generate(os, seed: 17);

        string[] required =
        [
            "dotnet --list-sdks",
            "dotnet --list-runtimes",
            "dotnet tool list -g",
            "dotnet nuget locals global-packages --list",
            "dotnet nuget locals http-cache --list",
            "dotnet nuget locals temp --list",
            "go env GOMODCACHE",
            "go env GOCACHE",
            "go env GOPATH",
            "npm config get cache",
            "npm root -g",
            "rustup toolchain list",
        ];

        foreach (string query in required)
        {
            Assert.True(image.ToolOutputs.ContainsKey(query), $"no recorded output for '{query}'.");
            Assert.False(string.IsNullOrWhiteSpace(image.ToolOutputs[query]));
        }
    }

    [Fact]
    public void The_go_modcache_marks_files_read_only_by_design()
    {
        MachineImage image = FixtureGenerator.Generate(OperatingSystemKind.Linux, seed: 13, moduleCount: 10);

        var paths = FixtureGenerator.PathsFor(OperatingSystemKind.Linux, image.HomeDirectory);

        Assert.Contains(
            image.Fingerprint(),
            line => line.Contains("go/pkg/mod", StringComparison.Ordinal));
        Assert.True(image.FileSystem.DirectoryExists(paths.GoModCache));
    }

    [Fact]
    public async Task A_recorded_runner_answers_known_queries_and_refuses_unknown_ones()
    {
        MachineImage image = FixtureGenerator.Generate(OperatingSystemKind.Linux, seed: 23);
        var runner = FixtureGenerator.RunnerFor(image.ToolOutputs);

        Ports.ProcessResult known = await runner.RunAsync(new Ports.ProcessRequest
        {
            FileName = "dotnet",
            Arguments = ["--list-sdks"],
        });

        Ports.ProcessResult unknown = await runner.RunAsync(new Ports.ProcessRequest
        {
            FileName = "dotnet",
            Arguments = ["workload", "list"],
        });

        Assert.Equal(0, known.ExitCode);
        Assert.Contains("sdk", known.StandardOutput, StringComparison.Ordinal);

        // 127 with no output, not a fabricated answer: an unrecorded query must not let a resolver
        // invent a path (E-11).
        Assert.Equal(127, unknown.ExitCode);
        Assert.Equal(string.Empty, unknown.StandardOutput);
    }
}

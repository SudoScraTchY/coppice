using Coppice.Application;
using Coppice.Cli;
using Coppice.Core.Config;
using Coppice.Core.Domain;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Tests.Config;

/// <summary>
/// T-049: the config file is read by the commands, not just parsed by the loader. These tests pin the
/// load path (missing file is defaults, invalid file is a refusal), the platform locations, and the
/// precedence rule: an explicit flag wins over the file, the file wins over the built-in default.
/// </summary>
public sealed class ConfigWiringTests
{
    private const string LinuxPath = "/home/dev/.config/coppice/config.toml";

    [Fact]
    public void A_missing_config_is_working_defaults_not_a_failure()
    {
        ConfigLoadResult loaded = UserConfigService.Load(new FakeFileSystem(), LinuxPath);

        Assert.True(loaded.IsSuccess, loaded.Message);
        Assert.False(loaded.Config!.LoadedFromFile);
        Assert.Empty(loaded.Config.ProjectRoots);
        Assert.Equal(Policy.Default.KeepLatestN, loaded.Config.Policy.ToPolicy().KeepLatestN);
    }

    [Fact]
    public void A_config_supplies_project_roots_policy_and_both_exclusion_lists()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(LinuxPath, """
            [projects]
            roots = ["/home/dev/src", "/home/dev/work"]

            [policy]
            preset = "aggressive"
            keep_latest = 1

            [exclude]
            paths = ["/cache/microsoft.netcore.app*"]
            names = ["legacy.pkg"]
            """);

        ConfigLoadResult loaded = UserConfigService.Load(fs, LinuxPath);

        Assert.True(loaded.IsSuccess, loaded.Message);
        Assert.True(loaded.Config!.LoadedFromFile);
        Assert.Equal(["/home/dev/src", "/home/dev/work"], loaded.Config.ProjectRoots);

        Policy policy = UserConfigService.PolicyOf(loaded.Config);
        Assert.Equal(Policy.Aggressive.PresetName, policy.PresetName);
        Assert.Equal(1, policy.KeepLatestN);
        Assert.Equal(["/cache/microsoft.netcore.app*", "legacy.pkg"], policy.Exclusions);
    }

    [Fact]
    public void An_invalid_config_is_refused_with_diagnostics()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(LinuxPath, """
            [policy]
            keep_latets = 1
            """);

        ConfigLoadResult loaded = UserConfigService.Load(fs, LinuxPath);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(ConfigFailure.UnknownKey, loaded.Failure);
        Assert.NotEmpty(loaded.Diagnostics);
        Assert.Contains("keep_latets", loaded.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows, @"C:\Users\dev\AppData\Roaming\coppice\config.toml")]
    [InlineData(OperatingSystemKind.Linux, "/home/dev/.config/coppice/config.toml")]
    [InlineData(OperatingSystemKind.MacOS, "/Users/dev/Library/Application Support/coppice/config.toml")]
    public void The_config_path_follows_the_platform(OperatingSystemKind os, string expected) =>
        Assert.Equal(expected, UserConfigPath.For(new FakeEnvironment(os)));

    [Fact]
    public void Explicit_project_roots_win_over_the_config()
    {
        var config = new CoppiceConfig { ProjectRoots = ["/from/config"] };

        Assert.Equal(["/from/flag"], new ScanFlags(false, ["/from/flag"]).ResolveProjectRoots(config));
        Assert.Equal(["/from/config"], new ScanFlags(false, []).ResolveProjectRoots(config));
        Assert.Empty(new ScanFlags(false, []).ResolveProjectRoots(CoppiceConfig.Defaults));
    }
}

using Coppice.Core.Domain;
using Coppice.Plugins.Net;
using Coppice.Ports;
using Coppice.Tests.Support;

namespace Coppice.Tests.Plugins;

/// <summary>
/// Card T-010 acceptance: every row of the location table in SDD/09-ecosystems is represented and
/// round-trips, and the per-OS defaults hold for win/linux/mac — including the NuGet temp and
/// http-cache rows, which diverge.
/// </summary>
public sealed class NetProfileTests
{
    private static FakeEnvironment WindowsEnv() => new FakeEnvironment(OperatingSystemKind.Windows)
        .With("USERNAME", "dev")
        .With("TEMP", @"C:\Users\dev\AppData\Local\Temp")
        .With("LOCALAPPDATA", @"C:\Users\dev\AppData\Local")
        .With("USERPROFILE", @"C:\Users\dev");

    private static FakeEnvironment LinuxEnv() => new FakeEnvironment(OperatingSystemKind.Linux)
        .With("USER", "dev")
        .With("HOME", "/home/dev");

    private static FakeEnvironment MacEnv() => new FakeEnvironment(OperatingSystemKind.MacOS)
        .With("USER", "dev");

    /// <summary>Every location id named in 09-ecosystems' .NET table.</summary>
    [Theory]
    [InlineData("nuget-packages")]
    [InlineData("nuget-http-cache")]
    [InlineData("nuget-temp")]
    [InlineData("dotnet-root")]
    [InlineData("dotnet-tools")]
    [InlineData("dotnet-workloads")]
    [InlineData("vs-installer-cache")]
    public void Every_location_row_in_the_spec_is_represented(string id) =>
        Assert.NotNull(NetProfile.Find(id));

    [Fact]
    public void The_table_has_exactly_the_spec_rows_and_no_strays()
    {
        // A row the spec does not mention would be knowledge the kernel has no warrant for.
        Assert.Equal(7, NetProfile.Locations.Count);
        Assert.Equal(
            NetProfile.Locations.Select(l => l.Id).OrderBy(i => i, StringComparer.Ordinal),
            NetProfile.Locations.Select(l => l.Id).Distinct().OrderBy(i => i, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public void Every_cross_platform_location_has_a_default_for_every_os(OperatingSystemKind os)
    {
        // The VS installer cache is Windows-only by design, so it is excluded from this check.
        foreach (NetLocation location in NetProfile.Locations.Where(l => l.Id != "vs-installer-cache"))
        {
            Assert.True(
                location.OsDefaults.ContainsKey(os),
                $"'{location.Id}' has no {os} default in the table.");
        }
    }

    [Fact]
    public void The_vs_installer_cache_is_windows_only()
    {
        NetLocation vs = NetProfile.Find("vs-installer-cache")!;

        Assert.Equal([OperatingSystemKind.Windows], vs.SupportedOperatingSystems);
        Assert.DoesNotContain(NetProfile.ForOperatingSystem(OperatingSystemKind.Linux), l => l.Id == "vs-installer-cache");
        Assert.Contains(NetProfile.ForOperatingSystem(OperatingSystemKind.Windows), l => l.Id == "vs-installer-cache");
    }

    // ---- the divergence the spec calls out ----

    [Fact]
    public void The_nuget_temp_default_diverges_three_ways()
    {
        // 05: "%temp%\NuGetScratch on Windows, /tmp/NuGetScratch on macOS, and
        // /tmp/NuGetScratch<username> on Linux". Getting this wrong points the tool at a directory
        // that never exists, so the whole NuGet temp row silently reports nothing.
        NetLocation temp = NetProfile.Find("nuget-temp")!;

        Assert.Equal(
            @"C:\Users\dev\AppData\Local\Temp\NuGetScratch",
            NetDefaults.Expand(temp.OsDefaults[OperatingSystemKind.Windows], WindowsEnv()));

        Assert.Equal(
            "/tmp/NuGetScratch",
            NetDefaults.Expand(temp.OsDefaults[OperatingSystemKind.MacOS], MacEnv()));

        Assert.Equal(
            "/tmp/NuGetScratchdev",
            NetDefaults.Expand(temp.OsDefaults[OperatingSystemKind.Linux], LinuxEnv()));
    }

    [Fact]
    public void The_nuget_http_cache_default_diverges_between_windows_and_unix()
    {
        NetLocation http = NetProfile.Find("nuget-http-cache")!;

        Assert.Equal(
            @"C:\Users\dev\AppData\Local\NuGet\v3-cache",
            NetDefaults.Expand(http.OsDefaults[OperatingSystemKind.Windows], WindowsEnv()));

        Assert.Equal(
            "/home/dev/.local/share/NuGet/v3-cache",
            NetDefaults.Expand(http.OsDefaults[OperatingSystemKind.Linux], LinuxEnv()));
    }

    [Fact]
    public void The_nuget_packages_default_is_the_same_shape_on_every_os()
    {
        // The one row that does NOT diverge — asserted so a future edit notices.
        NetLocation packages = NetProfile.Find("nuget-packages")!;

        Assert.Equal(@"C:\Users\dev\.nuget\packages", NetDefaults.Expand(packages.OsDefaults[OperatingSystemKind.Windows], WindowsEnv()));
        Assert.Equal("/home/dev/.nuget/packages", NetDefaults.Expand(packages.OsDefaults[OperatingSystemKind.Linux], LinuxEnv()));
        Assert.Equal("/Users/dev/.nuget/packages", NetDefaults.Expand(packages.OsDefaults[OperatingSystemKind.MacOS], MacEnv()));
    }

    [Fact]
    public void The_dotnet_root_default_differs_between_an_installer_and_a_user_install()
    {
        NetLocation root = NetProfile.Find("dotnet-root")!;

        // The Windows default is the MSI location, which is why the row's owner is Unknown and its
        // tier is Review: the bytes may belong to an installer, not to the user.
        Assert.Equal(@"C:\Program Files\dotnet", root.OsDefaults[OperatingSystemKind.Windows]);
        Assert.Equal("~/.dotnet", root.OsDefaults[OperatingSystemKind.Linux]);
        Assert.True(root.ResolveAll);
    }

    // ---- safety metadata ----

    [Fact]
    public void A_dotnet_root_requires_both_fingerprint_markers()
    {
        NetLocation root = NetProfile.Find("dotnet-root")!;

        Assert.Equal(["host/fxr", "sdk"], root.Fingerprint.RequiredPaths.ToArray());
    }

    [Fact]
    public void Tiers_match_the_spec_table()
    {
        // Safe / Review / Manual exactly as 09-ecosystems states them.
        Assert.Equal(Risk.Safe, NetProfile.Find("nuget-packages")!.Tier);
        Assert.Equal(Risk.Safe, NetProfile.Find("nuget-http-cache")!.Tier);
        Assert.Equal(Risk.Safe, NetProfile.Find("nuget-temp")!.Tier);
        Assert.Equal(Risk.Review, NetProfile.Find("dotnet-root")!.Tier);
        Assert.Equal(Risk.Review, NetProfile.Find("dotnet-tools")!.Tier);
        Assert.Equal(Risk.Review, NetProfile.Find("dotnet-workloads")!.Tier);
        Assert.Equal(Risk.Manual, NetProfile.Find("vs-installer-cache")!.Tier);
    }

    [Fact]
    public void The_installer_cache_is_report_only()
    {
        // 06: anything not owned by the user is report-only in v1 (ADR-012). The tool must not
        // delete a Visual Studio Installer's files, ever.
        NetLocation vs = NetProfile.Find("vs-installer-cache")!;

        Assert.True(vs.Removal.ReportOnly);
        Assert.False(vs.Removal.SupportsSelectiveRemoval);
        Assert.Equal(InstallOwner.Msi, vs.Owner);
    }

    [Fact]
    public void Global_tools_and_workloads_are_removed_through_the_tool_not_by_deleting()
    {
        NetLocation tools = NetProfile.Find("dotnet-tools")!;
        NetLocation workloads = NetProfile.Find("dotnet-workloads")!;

        Assert.False(tools.Removal.SupportsSelectiveRemoval);
        Assert.Contains("dotnet tool uninstall", tools.Removal.NativeCommand!, StringComparison.Ordinal);
        Assert.False(workloads.Removal.SupportsSelectiveRemoval);
        Assert.Contains("dotnet workload clean", workloads.Removal.NativeCommand!, StringComparison.Ordinal);
    }

    [Fact]
    public void No_location_declares_delete_rights_without_a_fingerprint()
    {
        // 10-formats validation rules: "A fingerprint is required for any location with delete rights."
        foreach (NetLocation location in NetProfile.Locations.Where(l => l.Removal.SupportsSelectiveRemoval))
        {
            Assert.False(
                location.Fingerprint.IsEmpty,
                $"'{location.Id}' allows selective removal but declares no fingerprint.");
        }
    }

    [Fact]
    public void Cache_locations_are_owned_by_the_user()
    {
        foreach (string id in new[] { "nuget-packages", "nuget-http-cache", "nuget-temp" })
        {
            Assert.Equal(InstallOwner.User, NetProfile.Find(id)!.Owner);
        }
    }

    [Fact]
    public void The_table_round_trips_through_json()
    {
        // The table is data, so it must survive a round-trip: a profile that cannot be persisted
        // cannot be reported, shared, or diffed against a previous run.
        foreach (NetLocation location in NetProfile.Locations)
        {
            string json = DomainJson.Serialize(location);
            NetLocation? restored = DomainJson.Deserialize<NetLocation>(json);

            Assert.NotNull(restored);
            Assert.Equal(location.Id, restored!.Id);
            Assert.Equal(location.Kind, restored.Kind);
            Assert.Equal(location.Tier, restored.Tier);
            Assert.Equal(location.Owner, restored.Owner);
            Assert.Equal(location.OsDefaults, restored.OsDefaults);
            Assert.Equal(location.EnvVariables, restored.EnvVariables);
            Assert.Equal(location.Fingerprint.RequiredPaths, restored.Fingerprint.RequiredPaths);
        }
    }

    // ---- placeholder expansion ----

    [Fact]
    public void An_unresolvable_placeholder_yields_null_rather_than_a_literal_path()
    {
        // A "~/.nuget/packages" that never expands would fail the fingerprint for reasons that look
        // like the user's machine is broken. Null says "we could not resolve this", which is honest.
        var envWithoutHome = new FakeEnvironment(OperatingSystemKind.Linux);
        envWithoutHome.HomeDirectory = null;

        Assert.Null(NetDefaults.Expand("~/.nuget/packages", envWithoutHome));
        Assert.Null(NetDefaults.Expand("/tmp/NuGetScratch{user}", new FakeEnvironment(OperatingSystemKind.Linux)));
    }

    [Fact]
    public void An_unset_variable_is_left_visible_so_the_failure_names_it()
    {
        FakeEnvironment bare = new(OperatingSystemKind.Linux);

        Assert.Equal("%TEMP%\\NuGetScratch", NetDefaults.Expand("%TEMP%\\NuGetScratch", bare));
    }

    [Fact]
    public void Windows_and_posix_variable_syntaxes_both_expand()
    {
        FakeEnvironment env = new FakeEnvironment(OperatingSystemKind.Linux)
            .With("XDG_DATA", "/xdg");

        // Both spellings must produce the same string, and the separator after the variable must
        // survive: a braced form that ate its own '}' would yield "/xdgNuGet/..." and a path that
        // does not exist, which reads as "NuGet has no cache" rather than "we mangled the path".
        Assert.Equal("/xdg/NuGet/v3-cache", NetDefaults.Expand("$XDG_DATA/NuGet/v3-cache", env));
        Assert.Equal("/xdg/NuGet/v3-cache", NetDefaults.Expand("${XDG_DATA}/NuGet/v3-cache", env));

        // And $HOME expands through the same pass.
        FakeEnvironment withHome = new FakeEnvironment(OperatingSystemKind.Linux).With("HOME", "/opt/cache");
        Assert.Equal("/opt/cache/NuGet/v3-cache", NetDefaults.Expand("$HOME/NuGet/v3-cache", withHome));
    }

    [Fact]
    public void Expansion_is_deterministic()
    {
        NetLocation http = NetProfile.Find("nuget-http-cache")!;
        string defaultPath = http.OsDefaults[OperatingSystemKind.Linux];

        Assert.Equal(
            NetDefaults.Expand(defaultPath, LinuxEnv()),
            NetDefaults.Expand(defaultPath, LinuxEnv()));
    }
}

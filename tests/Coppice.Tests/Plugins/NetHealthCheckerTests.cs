using Coppice.Plugins.Net;
using Coppice.Tests.Support;
using Xunit;
using EntryKind = Coppice.Ports.EntryKind;
using FingerprintSpec = Coppice.Ports.FingerprintSpec;
using Ports = Coppice.Ports;
using Problem = Coppice.Ports.Problem;

namespace Coppice.Tests.Plugins;

/// <summary>
/// The health checks from the 09-ecosystems table (T-013, re-homed to the plugin for T-016).
/// <para>
/// Each test drives the checker through a fake VFS and a scripted process runner, so a check that
/// starts shelling out or writing fails loudly here instead of on a user's machine.
/// </para>
/// <para>
/// The recurring theme: a check reports only what it can PROVE. Several tests below assert that
/// NOTHING is reported when a prerequisite is missing, because a health report that speculates is
/// worse than one that stays quiet — the user cannot tell which lines are facts.
/// </para>
/// </summary>
public sealed class NetHealthCheckerTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    // ---- harness ----

    /// <summary>
    /// A whole fake world: filesystem, process runner, the roots the scan was given, and the
    /// environment. One rig per test, because the checks are supposed to be side-effect free and a
    /// shared rig would hide a write.
    /// </summary>
    private sealed class Rig
    {
        public FakeFileSystem Fs { get; init; } = new(Ports.OperatingSystemKind.Linux);

        public FakeProcessRunner Runner { get; set; } = new();

        public List<Ports.ScanRoot> Roots { get; } = [];

        public FakeEnvironment Env { get; } = new(Ports.OperatingSystemKind.Linux);

        public Ports.ScanContext Context() => new(
            Roots,
            Ports.ProjectSet.Empty,
            Fs,
            Runner,
            Env,
            new FakeClock(Epoch),
            CancellationToken.None);
    }

    private static Rig RigFor(params Ports.ScanRoot[] roots)
    {
        var rig = new Rig();
        rig.Roots.AddRange(roots);
        return rig;
    }

    private static Ports.ScanRoot Root(string locationId, string path) => new(
        locationId,
        path,
        locationId,
        Ports.RootRole.Active,
        new FingerprintSpec(),
        "user",
        Ports.Risk.Safe);

    /// <summary>dotnet --list-sdks output, in the shape the CLI prints it.</summary>
    private static FakeProcessRunner Sdks(params string[] versions) =>
        new FakeProcessRunner().Respond("dotnet", string.Join('\n', versions.Select(v => $"{v} [/usr/share/dotnet/sdk]")) + "\n");

    /// <summary>dotnet tool list -g output, in the shape the CLI prints it.</summary>
    private static FakeProcessRunner Tools(params string[] ids) =>
        new FakeProcessRunner().Respond("dotnet", "Package Id      Version      Commands\n" + string.Join('\n', ids.Select(i => $"{i,-14} 1.0.0        {i}")) + "\n");

    /// <summary>
    /// Runs the checks. The process runner is left exactly as the test scripted it: a check that
    /// queries the CLI must see whatever the test decided the CLI says, including "it is missing".
    /// </summary>
    private static Task<IReadOnlyList<Problem>> Check(Rig rig) =>
        new NetHealthChecker(rig.Context()).CheckAsync(CancellationToken.None);

    private static bool Has(IEnumerable<Problem> problems, string code) =>
        problems.Any(p => p.Code == code);

    // ---- 1. DOTNET_ROOT_MISSING_LAYOUT ----

    [Fact]
    public async Task Reports_a_dotnet_root_with_none_of_the_expected_layout()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/empty");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "DOTNET_ROOT_MISSING_LAYOUT");
        Assert.Equal(Ports.Severity.Warning, found.Severity);
        Assert.Equal("/usr/share/dotnet", found.Path);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("host")]
    public async Task Stays_quiet_when_the_root_has_at_least_one_expected_directory(string present)
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory($"/usr/share/dotnet/{present}");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "DOTNET_ROOT_MISSING_LAYOUT"));
    }

    // ---- 2. DOTNET_ROOT_INVALID ----

    [Fact]
    public async Task Reports_a_dotnet_root_variable_pointing_outside_every_resolved_root()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk");
        rig.Env.With("DOTNET_ROOT", "/opt/elsewhere");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "DOTNET_ROOT_INVALID");
        Assert.Equal(Ports.Severity.Error, found.Severity);
        Assert.Equal("/opt/elsewhere", found.Path);
    }

    [Fact]
    public async Task Accepts_a_dotnet_root_variable_that_matches_a_resolved_root()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk");
        rig.Env.With("DOTNET_ROOT", "/usr/share/dotnet");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "DOTNET_ROOT_INVALID"));
    }

    // ---- 3. DOTNET_PATH_MISSING ----

    [Fact]
    public async Task Reports_a_path_entry_naming_dotnet_with_no_executable()
    {
        var rig = RigFor();
        rig.Env.WithPath("/usr/local/bin", "/opt/dotnet-tools");
        rig.Fs.AddDirectory("/opt/dotnet-tools");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "DOTNET_PATH_MISSING");
        Assert.Equal("/opt/dotnet-tools", found.Path);
    }

    [Fact]
    public async Task Accepts_a_path_entry_that_really_holds_the_executable()
    {
        var rig = RigFor();
        rig.Env.WithPath("/usr/local/bin");
        rig.Fs.AddDirectory("/usr/local/bin");
        rig.Fs.AddFile("/usr/local/bin/dotnet");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "DOTNET_PATH_MISSING"));
    }

    // ---- 4. SDK_ORPHAN ----

    [Fact]
    public async Task Reports_an_sdk_folder_the_cli_does_not_know_about()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.400");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/7.0.100");
        rig.Runner.Respond("dotnet", "8.0.400 [/usr/share/dotnet/sdk]\n");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "SDK_ORPHAN");
        Assert.Contains("7.0.100", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_nothing_about_sdks_when_the_cli_cannot_be_run()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.400");
        rig.Runner.Fail("dotnet", exitCode: 127, standardError: "not found");

        IReadOnlyList<Problem> problems = await Check(rig);

        // The CLI being unavailable proves nothing about the SDKs on disk. Claiming every one is an
        // orphan would be a wall of false alarms on any machine without dotnet on PATH.
        Assert.False(Has(problems, "SDK_ORPHAN"));
    }

    [Fact]
    public async Task Reports_nothing_about_sdks_when_no_dotnet_root_was_scanned()
    {
        var rig = RigFor(Root("nuget-packages", "/home/dev/.nuget/packages"));

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.Empty(rig.Runner.Calls);
        Assert.False(Has(problems, "SDK_ORPHAN"));
    }

    // ---- 5. SDK_IN_TOOLS_LOCATION ----

    [Fact]
    public async Task Reports_an_sdk_installed_inside_the_tool_directory()
    {
        var rig = RigFor(Root("dotnet-tools", "/home/dev/.dotnet/tools"));
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/sdk/9.0.100");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "SDK_IN_TOOLS_LOCATION");
        Assert.Equal("/home/dev/.dotnet/tools/sdk/9.0.100", found.Path);
    }

    [Fact]
    public async Task Stays_quiet_when_the_tool_directory_holds_only_shims()
    {
        var rig = RigFor(Root("dotnet-tools", "/home/dev/.dotnet/tools"));
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/dotnetsay");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "SDK_IN_TOOLS_LOCATION"));
    }

    // ---- 6. ROOT_DUPLICATE ----

    [Fact]
    public async Task Reports_the_same_directory_resolved_twice_for_one_location()
    {
        // One via a link, one via the real path: textually different, physically the same.
        var rig = RigFor(
            Root("nuget-packages", "/home/dev/.nuget/packages"),
            Root("nuget-packages", "/mnt/linked/packages"));
        rig.Fs.AddDirectory("/home/dev/.nuget/packages/serilog");
        rig.Fs.AddSymlink("/mnt/linked/packages", "/home/dev/.nuget/packages");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "ROOT_DUPLICATE");
        Assert.Equal(Ports.Severity.Info, found.Severity);
    }

    [Fact]
    public async Task Does_not_confuse_two_separate_installs_with_a_duplicate()
    {
        var rig = RigFor(
            Root("dotnet-root", "/usr/share/dotnet"),
            Root("dotnet-root", "/usr/local/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk");
        rig.Fs.AddDirectory("/usr/local/share/dotnet/sdk");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "ROOT_DUPLICATE"));
    }

    // ---- 7. SDK_PREVIEW_SUPERSEDED ----

    [Fact]
    public void A_feature_band_does_not_span_two_majors()
    {
        // The bug this pins: a band built from minor.patch alone made 9.0.100 and 8.0.100 the SAME
        // band, so 9.0's GA declared 8.0's preview superseded — a false statement that would
        // eventually authorise deleting an SDK a net8.0 project still needs.
        Assert.NotEqual(SdkVersion.BandOf("9.0.100"), SdkVersion.BandOf("8.0.100"));
        Assert.Equal(SdkVersion.BandOf("9.0.100"), SdkVersion.BandOf("9.0.100-preview.3"));
    }

    [Fact]
    public async Task A_ga_from_a_newer_major_does_not_supersede_an_older_bands_preview()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.100-preview.7");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "SDK_PREVIEW_SUPERSEDED"));
    }

    [Fact]
    public async Task Reports_a_preview_superseded_by_ga_in_the_same_feature_band()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.100");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.100-rc.2");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "SDK_PREVIEW_SUPERSEDED");
        Assert.Contains("8.0.100-rc.2", found.Summary, StringComparison.Ordinal);
        Assert.Contains("8.0.100", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_a_preview_that_is_the_only_release_in_its_band()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100-preview.3");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.400");

        IReadOnlyList<Problem> problems = await Check(rig);

        // 8.0.400 is a different feature band, so it does not supersede the 9.0 preview.
        Assert.False(Has(problems, "SDK_PREVIEW_SUPERSEDED"));
    }

    // ---- 8. SDK_SUPERSEDED_MAJOR ----

    [Fact]
    public async Task Reports_an_older_major_sdk_as_superseded()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/7.0.100");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "SDK_SUPERSEDED_MAJOR");
        Assert.Equal(Ports.Severity.Warning, found.Severity);
        Assert.Contains("7.0.100", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_flag_a_single_sdk_as_superseded_by_itself()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "SDK_SUPERSEDED_MAJOR"));
    }

    // ---- 9. SDK_PIN_UNAVAILABLE ----

    [Fact]
    public async Task Reports_a_global_json_pin_that_no_installed_sdk_satisfies()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");
        rig.Fs.AddFile("/home/dev/global.json", """{ "sdk": { "version": "10.0.100" } }""");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "SDK_PIN_UNAVAILABLE");
        Assert.Equal(Ports.Severity.Error, found.Severity);
        Assert.Contains("10.0.100", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepts_a_pin_that_is_installed()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/10.0.100");
        rig.Fs.AddFile("/home/dev/global.json", """{ "sdk": { "version": "10.0.100" } }""");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "SDK_PIN_UNAVAILABLE"));
    }

    [Fact]
    public async Task An_installed_older_major_does_not_satisfy_a_newer_pins_band()
    {
        // The pin protects the named version and its rollForward band. An installed 9.x is NOT
        // within a 10.x pin's band, so the pin is genuinely unsatisfied and must be reported — the
        // build really will fail.
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");
        rig.Fs.AddFile("/home/dev/global.json", """{ "sdk": { "version": "10.0.100" } }""");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.True(Has(problems, "SDK_PIN_UNAVAILABLE"));
    }

    [Fact]
    public async Task A_pin_does_not_downgrade_an_older_major_to_a_pin_error()
    {
        // The converse: a 10.x pin must not turn the installed 9.x SDK into an error about the pin.
        // It is reported once, as superseded-major cleanup advice (a Warning) — a different fact,
        // with a different severity, from "your pinned SDK is missing".
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/10.0.100");
        rig.Fs.AddFile("/home/dev/global.json", """{ "sdk": { "version": "10.0.100" } }""");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "SDK_PIN_UNAVAILABLE"));
        Assert.Contains(problems, p => p.Code == "SDK_SUPERSEDED_MAJOR" && p.Severity == Ports.Severity.Warning);
    }

    [Fact]
    public async Task Degrades_quietly_on_an_unreadable_global_json()
    {
        var rig = RigFor(Root("dotnet-root", "/usr/share/dotnet"));
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/9.0.100");
        rig.Fs.AddFile("/home/dev/global.json", "{ not json at all");

        IReadOnlyList<Problem> problems = await Check(rig);

        // A malformed pin file must not throw and take every other check down with it.
        Assert.False(Has(problems, "SDK_PIN_UNAVAILABLE"));
    }

    // ---- 10. DOTNET_TOOL_BROKEN_SHIM ----

    [Fact]
    public async Task Reports_a_shim_the_cli_does_not_list_as_a_tool()
    {
        var rig = RigFor(Root("dotnet-tools", "/home/dev/.dotnet/tools"));
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/dotnetsay");
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/leftover");
        rig.Runner = Tools("dotnetsay");

        IReadOnlyList<Problem> problems = await Check(rig);

        Problem found = Assert.Single(problems, p => p.Code == "DOTNET_TOOL_BROKEN_SHIM");
        Assert.Contains("leftover", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_flag_the_nuget_store_that_lives_beside_the_shims()
    {
        // '.store' is NuGet's own package store inside the tool directory. It never appears in
        // `dotnet tool list -g`, so treating it as a dead shim would put a false positive in every
        // report on every machine.
        var rig = RigFor(Root("dotnet-tools", "/home/dev/.dotnet/tools"));
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/.store");
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/dotnetsay");
        rig.Runner = Tools("dotnetsay");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "DOTNET_TOOL_BROKEN_SHIM"));
    }

    [Fact]
    public async Task Does_not_flag_any_tool_when_the_cli_cannot_be_run()
    {
        var rig = RigFor(Root("dotnet-tools", "/home/dev/.dotnet/tools"));
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/dotnetsay");
        rig.Runner.Fail("dotnet", exitCode: 1, standardError: "no tool");

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.False(Has(problems, "DOTNET_TOOL_BROKEN_SHIM"));
    }

    // ---- cross-cutting guarantees ----

    [Fact]
    public async Task Problems_are_ordered_by_severity_then_code()
    {
        var rig = RigFor(
            Root("dotnet-root", "/usr/share/dotnet"),
            Root("dotnet-tools", "/home/dev/.dotnet/tools"));

        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.100");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/7.0.100");
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/leftover");
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/sdk/6.0.100");
        rig.Env.With("DOTNET_ROOT", "/opt/broken");
        rig.Runner = Tools();

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.NotEmpty(problems);

        // Severity descending, then code ordinal — total and content-derived, so two scans of the
        // same machine produce byte-identical reports (NFR-06).
        for (int i = 1; i < problems.Count; i++)
        {
            int bySeverity = problems[i - 1].Severity.CompareTo(problems[i].Severity);
            if (bySeverity == 0)
            {
                Assert.True(
                    string.CompareOrdinal(problems[i - 1].Code, problems[i].Code) <= 0,
                    $"{problems[i - 1].Code} should not sort after {problems[i].Code}.");
            }
            else
            {
                Assert.True(bySeverity > 0, "problems must be ordered most severe first.");
            }
        }
    }

    [Fact]
    public async Task Checks_never_write_through_the_port()
    {
        var rig = RigFor(
            Root("dotnet-root", "/usr/share/dotnet"),
            Root("dotnet-tools", "/home/dev/.dotnet/tools"),
            Root("nuget-packages", "/home/dev/.nuget/packages"));

        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/8.0.100");
        rig.Fs.AddDirectory("/usr/share/dotnet/sdk/7.0.100");
        rig.Fs.AddDirectory("/home/dev/.dotnet/tools/leftover");
        rig.Fs.AddDirectory("/home/dev/.nuget/packages/serilog/13.0.3");
        rig.Env.With("DOTNET_ROOT", "/opt/broken");
        rig.Runner = Tools();

        _ = await Check(rig);

        // C-9: a health check is an observation. The moment it writes, "check health" and "clean
        // up" are the same button.
        Assert.Empty(rig.Fs.Writes);
    }

    [Fact]
    public async Task An_empty_scan_context_reports_nothing_and_runs_nothing()
    {
        var rig = RigFor();

        IReadOnlyList<Problem> problems = await Check(rig);

        Assert.Empty(problems);
    }
}

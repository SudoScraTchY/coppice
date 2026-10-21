using Coppice.Cli;
using Coppice.Cli.Tui;
using Xunit;

namespace Coppice.Tests.Cli;

/// <summary>
/// T-045: project roots are explicit. These pin the parsing half; the scan/plan half lives in
/// <c>ProjectRootScanTests</c>.
/// </summary>
public sealed class ProjectRootFlagTests
{
    [Fact]
    public void Scan_accepts_repeated_project_roots_and_json()
    {
        ScanFlags flags = ScanFlags.Parse(["--projects", "/work/a", "--projects", "/work/b", "--json"]);

        Assert.True(flags.Json);
        Assert.Equal(new[] { "/work/a", "/work/b" }, flags.ProjectRoots);
    }

    [Fact]
    public void Tui_accepts_project_roots_alongside_its_other_flags()
    {
        TuiFlags flags = TuiFlags.Parse(["--once", "--no-color", "--projects", "/work/a"]);

        Assert.True(flags.Once);
        Assert.True(flags.NoColor);
        Assert.Equal(new[] { "/work/a" }, flags.ProjectRoots);
    }

    [Fact]
    public void A_missing_project_root_or_unknown_flag_is_a_usage_error()
    {
        Assert.Throws<ArgumentException>(() => ScanFlags.Parse(["--projects"]));
        Assert.Throws<ArgumentException>(() => ScanFlags.Parse(["--projects", " "]));
        Assert.Throws<ArgumentException>(() => ScanFlags.Parse(["--bogus"]));
        Assert.Throws<ArgumentException>(() => TuiFlags.Parse(["--projects"]));
        Assert.Throws<ArgumentException>(() => TuiFlags.Parse(["--projects", ""]));
    }
}

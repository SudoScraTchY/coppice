using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Ports;
using Xunit;
using Problem = Coppice.Core.Domain.Problem;
// ScanReport.Usage is the Application UsageBreakdown, not the domain one of the same name.
using UsageBreakdown = Coppice.Application.UsageBreakdown;

namespace Coppice.Tests.Export;

/// <summary>
/// T-042: the self-contained HTML report (nice-to-have #18, 10-formats).
/// <para>
/// The acceptance is two claims. First, the document stands alone: one file, inline CSS, no script, no
/// linked asset and no URL, so it can be opened on a machine with no network and cannot phone home.
/// Second, an attacker-influenced name or path is text in the document rather than markup: a package
/// called <c>&lt;script&gt;</c> must not become a script. Both are asserted against the rendered bytes,
/// because a claim about a file is only worth what the file contains.
/// </para>
/// </summary>
public sealed class HtmlReportTests
{
    // ---- fixtures -------------------------------------------------------------------------------

    private static Item Pkg(
        string ecosystem = "dotnet",
        string name = "newtonsoft.json",
        string version = "13.0.1",
        Usage usage = Usage.Referenced,
        ulong size = 1024,
        Risk risk = Risk.Safe,
        string? path = null) =>
        new(
            ecosystem,
            "package",
            name,
            version,
            "nuget-packages",
            path ?? $@"C:\Users\dev\.nuget\packages\{name}\{version}",
            size,
            risk,
            Facts.From([new KeyValuePair<string, string>("usage", usage.ToString())]));

    private static ScanReport Report(
        Item[] items,
        Problem[]? problems = null,
        ScanIssue[]? issues = null,
        string snapshotId = "snap_20250112T1015Z",
        string[]? hints = null) =>
        new(
            snapshotId,
            OperatingSystemKind.Windows,
            items,
            issues ?? [],
            problems ?? [],
            UsageBreakdown.FromItems(items),
            [.. items.Select(i => i.Ecosystem).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            2,
            hints ?? []);

    private static Problem Problem(string code, string summary, string? path = null) =>
        new()
        {
            Ecosystem = "dotnet",
            Code = code,
            Severity = Severity.Warning,
            Summary = summary,
            Path = path,
        };

    // ---- self-containment -----------------------------------------------------------------------

    [Fact]
    public void The_report_is_one_complete_html_document()
    {
        string html = HtmlReport.Render(Report([Pkg()]));

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_has_no_external_references()
    {
        // A realistic, non-hostile scan: the check is about what the exporter emits, not about a name
        // that happens to spell a URL. Every reference the document could make is absent.
        string html = HtmlReport.Render(Report(
            [Pkg("dotnet", "newtonsoft.json"), Pkg("go", "golang.org/x/text", size: 4096, usage: Usage.Unreferenced)],
            [Problem("E-DENIED", "The cache directory is not readable.")],
            [new ScanIssue("nuget-packages", "E-IO", "A file disappeared during the walk.", @"C:\cache\gone")]));

        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_content_security_policy_forbids_network_loading()
    {
        string html = HtmlReport.Render(Report([Pkg()]));

        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
    }

    // ---- escaping --------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a&b", "a&amp;b")]
    [InlineData("<script>", "&lt;script&gt;")]
    [InlineData("\"quoted\"", "&quot;quoted&quot;")]
    [InlineData("it's", "it&#39;s")]
    [InlineData("&lt;", "&amp;lt;")]
    public void Html_escape_encodes_every_markup_character(string input, string expected)
    {
        // Ampersand first: an incoming entity must not be decoded into markup the exporter never wrote.
        Assert.Equal(expected, HtmlReport.Escape(input));
    }

    [Fact]
    public void A_hostile_item_name_is_text_not_markup()
    {
        Item nasty = Pkg(
            name: "<script>alert('xss')</script>\" onmouseover=\"alert(1)",
            path: @"C:\cache\<img src=x onerror=alert(1)>\pkg");

        string html = HtmlReport.Render(Report([nasty]));

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&quot; onmouseover=&quot;alert(1)", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hostile_problem_summary_and_path_are_escaped()
    {
        Problem nasty = Problem("E-<b>", "<script>alert(1)</script>", "\" onmouseover=\"alert(1)");

        string html = HtmlReport.Render(Report([Pkg()], problems: [nasty]));

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("E-&lt;b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&quot; onmouseover=&quot;alert(1)", html, StringComparison.Ordinal);
    }

    // ---- totals and sections ---------------------------------------------------------------------

    [Fact]
    public void The_report_states_the_snapshot_totals_and_usage_counts()
    {
        Item[] items =
        [
            Pkg("dotnet", "a", usage: Usage.Referenced, size: 1000),
            Pkg("dotnet", "b", usage: Usage.Unreferenced, size: 2000),
            Pkg("go", "c", usage: Usage.Unknown, size: 3000),
            Pkg("go", "d", usage: Usage.Referenced, size: 4000),
        ];

        ScanReport report = Report(items);
        ReportTotals totals = ReportTotals.From(items, [], []);
        string html = HtmlReport.Render(report);

        Assert.Contains("snap_20250112T1015Z", html, StringComparison.Ordinal);
        Assert.Contains($"<dd>{totals.ItemCount} ({ReportTotals.FormatBytes(totals.TotalBytes)})</dd>", html, StringComparison.Ordinal);
        Assert.Contains($"<td>Referenced</td><td class=\"num\">{totals.ReferencedCount}</td>", html, StringComparison.Ordinal);
        Assert.Contains($"<td>Unreferenced</td><td class=\"num\">{totals.UnreferencedCount}</td>", html, StringComparison.Ordinal);
        Assert.Contains($"<td>Unknown</td><td class=\"num\">{totals.UnknownUsageCount}</td>", html, StringComparison.Ordinal);

        // One row per ecosystem plus the total row; the per-ecosystem numbers are the shared totals.
        Assert.Contains("<td>dotnet</td><td class=\"num\">2</td><td class=\"num\">2.9 KB</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>go</td><td class=\"num\">2</td><td class=\"num\">6.8 KB</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_lists_every_item_and_every_problem()
    {
        Item[] items = [Pkg(name: "alpha"), Pkg("go", "beta", size: 2048), Pkg("rust", "gamma", size: 4096)];
        Problem[] problems = [Problem("E-ONE", "first problem"), Problem("E-TWO", "second problem")];

        string html = HtmlReport.Render(Report(items, problems));

        foreach (Item item in items)
        {
            Assert.Contains($"<td>{item.Name}</td>", html, StringComparison.Ordinal);
            Assert.Contains($"<code>{item.Path}</code>", html, StringComparison.Ordinal);
        }

        foreach (Problem problem in problems)
        {
            Assert.Contains($"<td>{problem.Summary}</td>", html, StringComparison.Ordinal);
            Assert.Contains($"<td>{problem.Code}</td>", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_empty_scan_renders_without_dividing_by_zero()
    {
        string html = HtmlReport.Render(Report([]));

        Assert.Contains("No items were found.", html, StringComparison.Ordinal);
        Assert.Contains("No problems were reported.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_hints_are_present_and_escaped()
    {
        ScanReport report = Report([Pkg()], hints: ["No projects were found. Every package is <Unknown>."]);

        string html = HtmlReport.Render(report);

        Assert.Contains("No projects were found. Every package is &lt;Unknown&gt;.", html, StringComparison.Ordinal);
    }

    // ---- determinism -----------------------------------------------------------------------------

    [Fact]
    public void The_render_is_byte_identical_across_runs_and_platform_newlines()
    {
        Item[] items = [Pkg(name: "b"), Pkg(name: "a"), Pkg("go", "c")];
        ScanReport report = Report(items);

        string first = HtmlReport.Render(report);
        string second = HtmlReport.Render(report);

        Assert.Equal(first, second, StringComparer.Ordinal);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_ecosystem_rows_are_ordinal_sorted_not_in_discovery_order()
    {
        Item[] items = [Pkg("rust", "r"), Pkg("go", "g"), Pkg("dotnet", "d")];

        string html = HtmlReport.Render(Report(items));

        int dotnet = html.IndexOf("<td>dotnet</td>", StringComparison.Ordinal);
        int go = html.IndexOf("<td>go</td>", StringComparison.Ordinal);
        int rust = html.IndexOf("<td>rust</td>", StringComparison.Ordinal);

        Assert.True(dotnet >= 0 && dotnet < go && go < rust, "ecosystem rows must be ordinal-sorted");
    }

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(1073741824UL, "1.0 GB")]
    public void Sizes_use_the_shared_invariant_formatter(ulong bytes, string expected)
    {
        Assert.Equal(expected, ReportTotals.FormatBytes(bytes));
        Assert.Contains(expected, HtmlReport.Render(Report([Pkg(size: bytes)])), StringComparison.Ordinal);
    }
}

using System.Globalization;
using System.Text;

using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>
/// Self-contained HTML export (T-042, nice-to-have #18, 10-formats).
/// <para>
/// The report is a single file: one stylesheet inline in a <c>&lt;style&gt;</c> element, no scripts, no
/// linked assets and no URLs of any kind. That is a delivery constraint, not a stylistic one. A scan
/// report is exactly the kind of file a person attaches to a ticket or opens on an air-gapped machine,
/// and a document that quietly fetches a font or a chart library has exported the user's cache
/// inventory to whoever serves that asset. The <c>default-src 'none'</c> Content-Security-Policy is a
/// second lock: even if a future edit introduced an external reference, the browser would refuse it.
/// </para>
/// <para>
/// Every dynamic value is HTML-escaped. Names come from manifests and paths come from the filesystem,
/// so both are attacker-influenced: a package called <c>&lt;script&gt;</c> must render as text, never as
/// markup. Totals come from <see cref="ReportTotals"/> so the HTML cannot disagree with the console,
/// the CSV or the Markdown about the same scan.
/// </para>
/// </summary>
public static class HtmlReport
{
    public static string Render(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        ReportTotals totals = ReportTotals.From(report.Items, report.Problems, report.Issues);
        var html = new StringBuilder(16 * 1024);

        html.Append("<!DOCTYPE html>\n");
        html.Append("<html lang=\"en\">\n");
        html.Append("<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");

        // 'none' for everything except the inline stylesheet we just wrote. A browser opening this file
        // offline has no reason to fetch anything, and this makes that a rule rather than a hope.
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n");
        html.Append("<title>Coppice scan &mdash; ").Append(Escape(report.SnapshotId)).Append("</title>\n");
        html.Append(Css);
        html.Append("</head>\n");
        html.Append("<body>\n");
        html.Append("<main>\n");

        html.Append("<header>\n");
        html.Append("<p class=\"eyebrow\">Coppice scan</p>\n");
        html.Append("<h1>Cache inventory</h1>\n");
        html.Append("<p class=\"lede\">A read-only inventory of the caches on this machine. ")
            .Append("Nothing here is a proposal; the plan lists what Coppice would do.</p>\n");
        html.Append("</header>\n");

        // ---- summary ----

        html.Append("<section>\n<h2>Summary</h2>\n<dl class=\"facts\">\n");
        Fact(html, "Snapshot", report.SnapshotId, code: true);
        Fact(html, "Platform", report.OS.ToString());
        Fact(html, "Projects scanned", report.ProjectCount.ToString(CultureInfo.InvariantCulture));
        Fact(html, "Ecosystems", report.Ecosystems.Count == 0 ? "None" : string.Join(", ", report.Ecosystems));
        Fact(html, "Items", string.Create(
            CultureInfo.InvariantCulture,
            $"{totals.ItemCount} ({ReportTotals.FormatBytes(totals.TotalBytes)})"));
        html.Append("</dl>\n</section>\n");

        // ---- usage ----

        html.Append("<section>\n<h2>Usage</h2>\n");
        html.Append("<table>\n<thead><tr><th scope=\"col\">Verdict</th><th scope=\"col\" class=\"num\">Count</th></tr></thead>\n<tbody>\n");
        Row(html, "Referenced", totals.ReferencedCount);
        Row(html, "Unreferenced", totals.UnreferencedCount);
        Row(html, "Unknown", totals.UnknownUsageCount);
        html.Append("<tr class=\"total\"><th scope=\"row\">Total</th><td class=\"num\">")
            .Append(totals.ItemCount.ToString(CultureInfo.InvariantCulture))
            .Append("</td></tr>\n");
        html.Append("</tbody>\n</table>\n</section>\n");

        // ---- per ecosystem ----

        html.Append("<section>\n<h2>By ecosystem</h2>\n");

        if (totals.ByEcosystem.Count == 0)
        {
            html.Append("<p class=\"empty\">No items were found.</p>\n");
        }
        else
        {
            html.Append("<table>\n<thead><tr><th scope=\"col\">Ecosystem</th><th scope=\"col\" class=\"num\">Items</th><th scope=\"col\" class=\"num\">Size</th></tr></thead>\n<tbody>\n");

            // Ordinal, not discovery order: the same snapshot must produce the same document.
            foreach ((string _, EcosystemTotal eco) in totals.ByEcosystem.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                html.Append("<tr>");
                Cell(html, eco.Ecosystem);
                Cell(html, eco.ItemCount.ToString(CultureInfo.InvariantCulture), "num");
                Cell(html, ReportTotals.FormatBytes(eco.Bytes), "num");
                html.Append("</tr>\n");
            }

            html.Append("<tr class=\"total\"><th scope=\"row\">Total</th><td class=\"num\">")
                .Append(totals.ItemCount.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td class=\"num\">")
                .Append(ReportTotals.FormatBytes(totals.TotalBytes))
                .Append("</td></tr>\n");
            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</section>\n");

        // ---- items ----

        html.Append("<section>\n<h2>Items</h2>\n");

        if (report.Items.Count == 0)
        {
            html.Append("<p class=\"empty\">No items were found.</p>\n");
        }
        else
        {
            html.Append("<table class=\"items\">\n");
            html.Append("<thead><tr>")
                .Append("<th scope=\"col\">Ecosystem</th>")
                .Append("<th scope=\"col\">Kind</th>")
                .Append("<th scope=\"col\">Name</th>")
                .Append("<th scope=\"col\">Version</th>")
                .Append("<th scope=\"col\">Usage</th>")
                .Append("<th scope=\"col\">Risk</th>")
                .Append("<th scope=\"col\" class=\"num\">Size</th>")
                .Append("<th scope=\"col\">Path</th>")
                .Append("</tr></thead>\n<tbody>\n");

            foreach (Item item in report.Items)
            {
                html.Append("<tr>");
                Cell(html, item.Ecosystem);
                Cell(html, item.Kind);
                Cell(html, item.Name);
                Cell(html, item.Version);
                Cell(html, ReportTotals.UsageOf(item).ToString());
                Cell(html, item.Risk.ToString());
                Cell(html, ReportTotals.FormatBytes(item.Size), "num");
                CodeCell(html, item.Path);
                html.Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</section>\n");

        // ---- problems ----

        html.Append("<section>\n<h2>Problems</h2>\n");

        if (report.Problems.Count == 0)
        {
            html.Append("<p class=\"empty\">No problems were reported.</p>\n");
        }
        else
        {
            html.Append("<table>\n");
            html.Append("<thead><tr>")
                .Append("<th scope=\"col\">Severity</th>")
                .Append("<th scope=\"col\">Ecosystem</th>")
                .Append("<th scope=\"col\">Code</th>")
                .Append("<th scope=\"col\">Path</th>")
                .Append("<th scope=\"col\">Detail</th>")
                .Append("</tr></thead>\n<tbody>\n");

            foreach (Problem problem in report.Problems)
            {
                html.Append("<tr>");
                Cell(html, problem.Severity.ToString());
                Cell(html, problem.Ecosystem);
                Cell(html, problem.Code);
                CodeCell(html, problem.Path);
                Cell(html, problem.Summary);
                html.Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</section>\n");

        // ---- issues ----

        html.Append("<section>\n<h2>Issues</h2>\n");

        if (report.Issues.Count == 0)
        {
            html.Append("<p class=\"empty\">No issues were reported.</p>\n");
        }
        else
        {
            html.Append("<table>\n");
            html.Append("<thead><tr>")
                .Append("<th scope=\"col\">Location</th>")
                .Append("<th scope=\"col\">Code</th>")
                .Append("<th scope=\"col\">Path</th>")
                .Append("<th scope=\"col\">Detail</th>")
                .Append("</tr></thead>\n<tbody>\n");

            foreach (ScanIssue issue in report.Issues)
            {
                html.Append("<tr>");
                Cell(html, issue.LocationId);
                Cell(html, issue.Code);
                CodeCell(html, issue.Path);
                Cell(html, issue.Summary);
                html.Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</section>\n");

        // ---- onboarding hints ----

        if (report.OnboardingHints.Length > 0)
        {
            html.Append("<section>\n<h2>Hints</h2>\n<ul class=\"hints\">\n");

            foreach (string hint in report.OnboardingHints)
            {
                html.Append("<li>").Append(Escape(hint)).Append("</li>\n");
            }

            html.Append("</ul>\n</section>\n");
        }

        html.Append("<footer>\n<p>Read-only report. Coppice proposes nothing here; see the plan for what it would do.</p>\n</footer>\n");
        html.Append("</main>\n");
        html.Append("</body>\n");
        html.Append("</html>\n");

        return html.ToString();
    }

    /// <summary>
    /// Escapes a value for HTML text or a double-quoted attribute.
    /// <para>
    /// Ampersand is replaced first so an incoming <c>&amp;lt;</c> cannot be double-decoded into a tag.
    /// Both quote characters are escaped because dynamic values also reach the document title.
    /// </para>
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = new StringBuilder(value.Length + 16);

        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                case '\'':
                    escaped.Append("&#39;");
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    private static void Fact(StringBuilder html, string label, string? value, bool code = false)
    {
        html.Append("<div><dt>").Append(Escape(label)).Append("</dt><dd>");

        if (code)
        {
            html.Append("<code>").Append(Escape(value)).Append("</code>");
        }
        else
        {
            html.Append(Escape(value));
        }

        html.Append("</dd></div>\n");
    }

    private static void Row(StringBuilder html, string label, int count)
    {
        html.Append("<tr><td>").Append(Escape(label)).Append("</td><td class=\"num\">")
            .Append(count.ToString(CultureInfo.InvariantCulture))
            .Append("</td></tr>\n");
    }

    private static void Cell(StringBuilder html, string? value, string? cssClass = null)
    {
        html.Append("<td");

        if (cssClass is not null)
        {
            html.Append(" class=\"").Append(cssClass).Append('"');
        }

        html.Append('>').Append(Escape(value)).Append("</td>");
    }

    private static void CodeCell(StringBuilder html, string? value)
    {
        html.Append("<td><code>").Append(Escape(value)).Append("</code></td>");
    }

    /// <summary>
    /// The entire stylesheet. Inline by design: there is no external stylesheet, no font file, no image
    /// and no <c>url()</c> reference to fetch.
    /// </summary>
    private const string Css =
        """
        <style>
        :root {
          --ink: #1b1f23;
          --muted: #57606a;
          --line: #d0d7de;
          --panel: #f6f8fa;
          --accent: #1a7f37;
        }
        * { box-sizing: border-box; }
        html { background: #ffffff; }
        body {
          margin: 0;
          padding: 2.5rem 1.25rem 4rem;
          color: var(--ink);
          background: #ffffff;
          font-family: system-ui, -apple-system, "Segoe UI", "Helvetica Neue", Arial, sans-serif;
          font-size: 15px;
          line-height: 1.55;
        }
        main { max-width: 1120px; margin: 0 auto; }
        header { border-bottom: 2px solid var(--ink); padding-bottom: 1rem; margin-bottom: 1.5rem; }
        .eyebrow { margin: 0; color: var(--accent); font-size: .78rem; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; }
        h1 { margin: .15rem 0 .5rem; font-size: 1.9rem; letter-spacing: -.02em; }
        h2 { margin: 2rem 0 .75rem; font-size: 1.15rem; }
        .lede { margin: 0; max-width: 70ch; color: var(--muted); }
        section { margin: 0 0 1.5rem; }
        .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: .75rem 1.5rem; margin: 0; padding: 1rem; background: var(--panel); border: 1px solid var(--line); border-radius: 6px; }
        .facts div { min-width: 0; }
        .facts dt { color: var(--muted); font-size: .78rem; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; }
        .facts dd { margin: .15rem 0 0; overflow-wrap: anywhere; }
        table { width: 100%; border-collapse: collapse; border: 1px solid var(--line); font-size: .92rem; }
        th, td { padding: .45rem .6rem; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
        thead th { background: var(--panel); font-size: .78rem; letter-spacing: .04em; text-transform: uppercase; }
        tbody tr:last-child td, tbody tr:last-child th { border-bottom: 0; }
        tr.total th, tr.total td { background: var(--panel); font-weight: 700; }
        .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        code { font-family: ui-monospace, "Cascadia Mono", "SFMono-Regular", Consolas, monospace; font-size: .86em; overflow-wrap: anywhere; }
        .empty { margin: 0; padding: .75rem 1rem; background: var(--panel); border: 1px solid var(--line); border-radius: 6px; color: var(--muted); }
        .hints { margin: 0; padding-left: 1.25rem; }
        footer { margin-top: 2.5rem; padding-top: 1rem; border-top: 1px solid var(--line); color: var(--muted); font-size: .88rem; }
        @media (max-width: 640px) { body { padding: 1.25rem .75rem 2rem; } h1 { font-size: 1.5rem; } th, td { padding: .4rem .45rem; } }
        </style>

        """;
}

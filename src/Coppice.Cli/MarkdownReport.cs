using System.Globalization;
using System.Text;

using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>
/// Markdown export (T-027, FR-18, 10-formats: "Markdown (shareable summary)").
/// <para>
/// The point of this format is that a person pastes it into an issue or a chat and someone else can
/// understand a machine's cache state without running the tool. That makes two things non-negotiable:
/// every cell is escaped so a package name cannot break the table's shape, and every number is the same
/// number the console printed.
/// </para>
/// </summary>
public static class MarkdownReport
{
    public static string Render(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        ReportTotals totals = ReportTotals.From(report.Items, report.Problems, report.Issues);
        var md = new StringBuilder();

        md.Append("# Coppice scan\n\n");
        md.Append(CultureInfo.InvariantCulture, $"- **Snapshot:** `{Escape(report.SnapshotId)}`\n");
        md.Append(CultureInfo.InvariantCulture, $"- **Platform:** {Escape(report.OS.ToString())}\n");
        md.Append(CultureInfo.InvariantCulture, $"- **Projects scanned:** {report.ProjectCount}\n");
        md.Append(CultureInfo.InvariantCulture, $"- **Items found:** {totals.ItemCount} ({ReportTotals.FormatBytes(totals.TotalBytes)})\n");

        if (report.OnboardingHints.Length > 0)
        {
            md.Append("\n> [!IMPORTANT]\n");

            foreach (string hint in report.OnboardingHints)
            {
                md.Append(CultureInfo.InvariantCulture, $"> {Escape(hint)}\n");
            }
        }

        // ---- usage ----

        md.Append("\n## Usage\n\n");
        md.Append("| Verdict | Count |\n| --- | ---: |\n");
        md.Append(CultureInfo.InvariantCulture, $"| Referenced | {totals.ReferencedCount} |\n");
        md.Append(CultureInfo.InvariantCulture, $"| Unreferenced | {totals.UnreferencedCount} |\n");
        md.Append(CultureInfo.InvariantCulture, $"| Unknown | {totals.UnknownUsageCount} |\n");
        md.Append(CultureInfo.InvariantCulture, $"| **Total** | **{totals.ItemCount}** |\n");

        // ---- per ecosystem ----

        if (totals.ByEcosystem.Count > 0)
        {
            md.Append("\n## By ecosystem\n\n");
            md.Append("| Ecosystem | Items | Size |\n| --- | ---: | ---: |\n");

            foreach ((string _, EcosystemTotal eco) in totals.ByEcosystem.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                md.Append(CultureInfo.InvariantCulture, $"| {Escape(eco.Ecosystem)} | {eco.ItemCount} | {ReportTotals.FormatBytes(eco.Bytes)} |\n");
            }

            md.Append(CultureInfo.InvariantCulture, $"| **Total** | **{totals.ItemCount}** | **{ReportTotals.FormatBytes(totals.TotalBytes)}** |\n");
        }

        // ---- items ----

        md.Append("\n## Items\n\n");

        if (report.Items.Count == 0)
        {
            md.Append("_No items were found._\n");
        }
        else
        {
            md.Append("| Ecosystem | Name | Version | Usage | Risk | Size | Path |\n");
            md.Append("| --- | --- | --- | --- | --- | ---: | --- |\n");

            foreach (Item item in report.Items)
            {
                md.Append(CultureInfo.InvariantCulture,
                    $"| {Escape(item.Ecosystem)} "
                    + $"| {Escape(item.Name)} "
                    + $"| {Escape(item.Version)} "
                    + $"| {Escape(ReportTotals.UsageOf(item).ToString())} "
                    + $"| {Escape(item.Risk.ToString())} "
                    + $"| {ReportTotals.FormatBytes(item.Size)} "
                    + $"| `{Escape(item.Path)}` |\n");
            }
        }

        // ---- problems ----

        if (report.Problems.Count > 0)
        {
            md.Append("\n## Problems\n\n");
            md.Append("| Severity | Location | Code | Detail |\n| --- | --- | --- | --- |\n");

            foreach (Problem problem in report.Problems)
            {
                md.Append(CultureInfo.InvariantCulture,
                    $"| {Escape(problem.Severity.ToString())} "
                    + $"| {Escape(problem.Detail.TryGetValue("locationId", out string? locationId) ? locationId : string.Empty)} "
                    + $"| {Escape(problem.Code)} "
                    + $"| {Escape(problem.Summary)} |\n");
            }
        }

        // ---- issues ----

        if (report.Issues.Count > 0)
        {
            md.Append("\n## Issues\n\n");
            md.Append("| Location | Code | Path | Detail |\n| --- | --- | --- | --- |\n");

            foreach (ScanIssue issue in report.Issues)
            {
                md.Append(CultureInfo.InvariantCulture,
                    $"| {Escape(issue.LocationId)} "
                    + $"| {Escape(issue.Code)} "
                    + $"| `{Escape(issue.Path ?? string.Empty)}` "
                    + $"| {Escape(issue.Summary)} |\n");
            }
        }

        md.Append("\n---\n");
        md.Append("Read-only report. Coppice proposes nothing here; see the plan for what it would do.\n");

        return md.ToString();
    }

    /// <summary>
    /// Escapes a value for a Markdown table cell or a blockquote line.
    /// <para>
    /// A package name and a path are both attacker-influenced: one comes from a manifest, the other from
    /// the filesystem. A name containing a pipe would silently add a column, and a name containing a
    /// newline would silently add a row — turning one package into two entries in someone else's issue
    /// tracker. Backslash-escaping the pipe is what GFM's table syntax honours.
    /// </para>
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}

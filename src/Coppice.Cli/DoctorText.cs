using System.Text;
using Coppice.Application;

namespace Coppice.Cli;

/// <summary>The human rendering of a doctor report. The report itself is application data; the table is the CLI's.</summary>
public static class DoctorText
{
    /// <summary>Human output: the roots table, then the problems, then the exit code rationale.</summary>
    public static string ToText(DoctorReport report, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        home ??= string.Empty;

        sb.AppendLine("coppice doctor");
        sb.AppendLine();
        sb.AppendLine(TextReport.RootHeader());
        sb.AppendLine(new string('-', 100));

        if (report.Roots.Count == 0)
        {
            sb.AppendLine("  (no locations resolved)");
        }

        foreach (RootReport root in report.Roots)
        {
            foreach (string line in RootsText.Lines(root, home))
            {
                sb.AppendLine(line);
            }
        }

        if (report.Detection.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Ecosystems:");
            sb.AppendLine(TextReport.DetectionHeader());
            sb.AppendLine(new string('-', 100));

            foreach (EcosystemDetectionReport detection in report.Detection)
            {
                sb.AppendLine(TextReport.DetectionRow(detection));
            }
        }

        if (report.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Issues:");
            foreach (string issue in report.Issues)
            {
                sb.AppendLine($"  {issue}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(Summary(report));

        return sb.ToString();
    }

    private static string Summary(DoctorReport report)
    {
        int total = report.Roots.Count;
        int cleanable = report.Roots.Count(r => r.Root.IsCleanable);

        if (total == 0)
        {
            return "No locations resolved. Nothing is known about this machine yet.";
        }

        return $"{cleanable} of {total} location(s) are resolved and cleanable. "
            + "This tool does not delete anything on its own; run 'coppice plan' to review a removal.";
    }
}

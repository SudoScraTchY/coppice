using System.Text;

using Coppice.Application;

namespace Coppice.Cli;

/// <summary>
/// The `roots` view: every candidate the resolver considered, not only the winner (FR-02).
/// <para>
/// A user runs this command to answer "why is my cache not listed?" or "why will it not be cleaned?".
/// That question is about the candidates that lost, so denied, not-found, fingerprint-failed,
/// inactive and ambiguous roots are all printed with their own reason. The paths live on their own
/// lines because a declared path and a real path are different facts — a symlinked root is one path
/// on paper and another on disk, and collapsing them into one column hides exactly that.
/// </para>
/// <para>
/// Shared with <see cref="DoctorText"/> so the health check and the roots command cannot describe
/// the same root two different ways.
/// </para>
/// </summary>
public static class RootsText
{
    /// <summary>The whole `roots` output, as stdout should carry it.</summary>
    public static string ToText(DoctorReport report, string? home)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("coppice roots");
        sb.AppendLine();
        AppendTable(sb, report, home);
        sb.AppendLine();
        sb.AppendLine(Summary(report));
        return sb.ToString();
    }

    /// <summary>The candidate table, header and all.</summary>
    public static void AppendTable(StringBuilder sb, DoctorReport report, string? home)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(report);

        sb.AppendLine(TextReport.RootHeader());
        sb.AppendLine(new string('-', 100));

        if (report.Roots.Count == 0)
        {
            sb.AppendLine("  (no candidate locations)");
            return;
        }

        foreach (RootReport root in report.Roots)
        {
            foreach (string line in Lines(root, home))
            {
                sb.AppendLine(line);
            }
        }
    }

    /// <summary>
    /// One candidate as one row plus its evidence. The size note is printed when the path was not
    /// measured, so a dash in the table can never be mistaken for "zero bytes".
    /// </summary>
    public static IEnumerable<string> Lines(RootReport root, string? home)
    {
        ArgumentNullException.ThrowIfNull(root);

        yield return TextReport.RootRow(root.LocationId, root.Root, root.Owner, root.Size);

        string detail = root.Root.ViaDetail is null ? string.Empty : $" ({root.Root.ViaDetail})";
        yield return $"      via: {TextReport.Via(root.Root.Via)}{detail}";

        yield return $"      declared: {TextReport.Shorten(TextReport.DisplayPath(root.Root.DeclaredPath, home), 120)}";
        yield return $"      real:     {TextReport.Shorten(TextReport.DisplayPath(root.Root.RealPath, home), 120)}";

        if (root.SizeNote is { Length: > 0 } sizeNote)
        {
            yield return $"      size: {sizeNote}";
        }

        if (TextReport.ConfidenceNote(root.Root) is { } confidence)
        {
            yield return $"      ~ {confidence}";
        }

        if (root.Ambiguous)
        {
            // The flag is its own line, not only a word in a column: an ambiguous location is the one
            // case where a user has to act (pin it), and it is never cleanable until they do.
            yield return "      ! ambiguous: two sources disagree; not cleanable until this location is pinned";
            yield break;
        }

        if (!string.IsNullOrEmpty(root.Problem))
        {
            yield return $"      -> {root.Problem}";
        }
    }

    /// <summary>What the table adds up to, including how many candidates may actually be cleaned.</summary>
    public static string Summary(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Roots.Count == 0)
        {
            return "No candidate locations. This command only reads; it changes nothing.";
        }

        int cleanable = report.Roots.Count(r => r.Root.IsCleanable);
        string sizing = report.SizesMeasured
            ? " Sizes cover existing directory candidates; a denied or missing path is deliberately not measured."
            : string.Empty;

        return $"{report.Roots.Count} candidate(s); {cleanable} cleanable.{sizing} "
            + "This command only reads; it changes nothing.";
    }
}

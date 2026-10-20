using System.Text;

namespace Coppice.Cli.Tui;

/// <summary>The screens 07 declares, in the order Tab visits them (T-036, FR-20).</summary>
public enum TuiScreen
{
    Dashboard,
    Roots,
    Items,
    Problems,
    PlanReview,
    ApplyProgress,
    Quarantine,
}

/// <summary>One table row. <see cref="Selected"/> is a cursor, never a decision.</summary>
public sealed record TuiRow(IReadOnlyList<string> Cells, bool Selected = false);

/// <summary>
/// A labeled region of a screen. The label is not decoration: it is what a screen reader announces and
/// what the plain renderer prints as a heading, so every block has one and a test enforces it (FR-20's
/// accessibility acceptance).
/// </summary>
public sealed record TuiBlock(
    string Label,
    IReadOnlyList<string> Headers,
    IReadOnlyList<TuiRow> Rows,
    string? Note = null)
{
    /// <summary>A block of prose lines, rendered as a panel rather than a table.</summary>
    public static TuiBlock Lines(string label, IEnumerable<string> lines) =>
        new(label, [], [.. lines.Select(line => new TuiRow([line]))]);
}

public sealed record TuiKeyHelp(string Key, string Action);

/// <summary>
/// Everything a renderer needs for one screen. Pure data, so the plain and interactive renderers cannot
/// disagree about content — only about presentation.
/// </summary>
public sealed record TuiFrame(
    TuiScreen Screen,
    string Title,
    IReadOnlyList<TuiBlock> Blocks,
    string Status,
    IReadOnlyList<TuiKeyHelp> Keys);

/// <summary>
/// The piped-output renderer (NFR-09). No ANSI by construction — there is no escape character in this
/// file — and the same labels the interactive renderer shows, in the same order, so a screen-reader user
/// piping to a reader gets the screen rather than a blank terminal.
/// </summary>
public static class TuiPlain
{
    public static string Render(TuiFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var text = new StringBuilder();
        text.Append("coppice · ").AppendLine(frame.Title);
        text.AppendLine();

        foreach (TuiBlock block in frame.Blocks)
        {
            text.Append('[').Append(block.Label).AppendLine("]");

            if (block.Headers.Count > 0)
            {
                text.AppendLine(string.Join("  ", block.Headers));
            }

            foreach (TuiRow row in block.Rows)
            {
                text.Append(row.Selected ? "> " : "  ");
                text.AppendLine(string.Join("  ", row.Cells));
            }

            if (!string.IsNullOrEmpty(block.Note))
            {
                text.Append("  ").AppendLine(block.Note);
            }

            text.AppendLine();
        }

        if (frame.Status.Length > 0)
        {
            text.Append("status: ").AppendLine(frame.Status);
        }

        text.Append("keys: ");
        text.AppendLine(string.Join(" · ", frame.Keys.Select(k => $"{k.Key} {k.Action}")));

        return text.ToString();
    }
}

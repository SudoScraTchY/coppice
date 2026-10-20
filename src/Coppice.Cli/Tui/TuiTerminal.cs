using Spectre.Console;
using Spectre.Console.Rendering;

namespace Coppice.Cli.Tui;

/// <summary>
/// The TUI's terminal. Abstracted for two reasons: the piped-output rule (NFR-09) must be testable
/// without redirecting a process, and the state machine must never learn how a screen is drawn.
/// </summary>
public interface ITuiTerminal
{
    /// <summary>True only when a human can both see the screen and answer a key.</summary>
    bool IsInteractive { get; }

    void Render(TuiFrame frame);

    void WritePlain(string text);

    bool KeyAvailable { get; }

    ConsoleKeyInfo ReadKey();
}

/// <summary>
/// The interactive renderer (Spectre.Console, decision 003). Every dynamic string is
/// <see cref="Markup.Escape"/>d exactly once: paths and package names are attacker-influenced, and a
/// name containing <c>[red]</c> must be shown as text rather than interpreted as markup.
/// </summary>
public sealed class SystemTuiTerminal : ITuiTerminal
{
    private readonly IAnsiConsole _console;

    public SystemTuiTerminal()
        : this(AnsiConsole.Console)
    {
    }

    public SystemTuiTerminal(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public bool KeyAvailable => IsInteractive && Console.KeyAvailable;

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public void WritePlain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Console.Out.Write(text);
    }

    public void Render(TuiFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // A clear is cosmetic and needs a real console; a redirected or headless host must still render.
        if (!Console.IsOutputRedirected)
        {
            try
            {
                _console.Clear();
            }
            catch (IOException)
            {
                // No console behind the output; the frame below is still correct.
            }
        }

        _console.Write(new Rule(Markup.Escape(frame.Title)).LeftJustified());
        _console.WriteLine();

        foreach (TuiBlock block in frame.Blocks)
        {
            _console.Write(RenderBlock(block));
            _console.WriteLine();
        }

        if (frame.Status.Length > 0)
        {
            _console.Write(new Markup("[yellow]" + Markup.Escape(frame.Status) + "[/]"));
            _console.WriteLine();
        }

        _console.Write(new Markup(
            "[grey]" + Markup.Escape(string.Join(" · ", frame.Keys.Select(k => $"{k.Key} {k.Action}"))) + "[/]"));
        _console.WriteLine();
    }

    private static IRenderable RenderBlock(TuiBlock block)
    {
        if (block.Headers.Count == 0)
        {
            var lines = new List<IRenderable>();
            foreach (TuiRow row in block.Rows)
            {
                lines.Add(new Markup(Markup.Escape(string.Join("  ", row.Cells))));
            }

            if (!string.IsNullOrEmpty(block.Note))
            {
                lines.Add(new Markup("[grey]" + Markup.Escape(block.Note) + "[/]"));
            }

            if (lines.Count == 0)
            {
                lines.Add(new Markup("[grey](none)[/]"));
            }

            return new Panel(new Rows(lines))
            {
                Header = new PanelHeader(Markup.Escape(block.Label)),
                Expand = true,
            };
        }

        var table = new Table().Border(TableBorder.None).Expand();
        foreach (string header in block.Headers)
        {
            table.AddColumn(new TableColumn(new Markup("[bold]" + Markup.Escape(header) + "[/]")));
        }

        foreach (TuiRow row in block.Rows)
        {
            var cells = new List<IRenderable>();
            for (int i = 0; i < row.Cells.Count; i++)
            {
                string text = row.Selected && i == 0 ? "› " + row.Cells[i] : row.Cells[i];
                cells.Add(new Markup(
                    Markup.Escape(text),
                    row.Selected ? new Style(decoration: Decoration.Invert) : Style.Plain));
            }

            table.AddRow([.. cells]);
        }

        if (!string.IsNullOrEmpty(block.Note))
        {
            table.Caption("[grey]" + Markup.Escape(block.Note) + "[/]");
        }

        return new Panel(table)
        {
            Header = new PanelHeader(Markup.Escape(block.Label)),
            Expand = true,
        };
    }
}

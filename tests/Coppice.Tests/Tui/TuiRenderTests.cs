using System.Text;
using Coppice.Cli;
using Coppice.Cli.Tui;
using Coppice.Tests.Support;
using Spectre.Console;
using Xunit;

namespace Coppice.Tests.Tui;

/// <summary>
/// T-036 rendering acceptance: piped output is plain (NFR-09), hostile names are text in the interactive
/// renderer, and the plain form keeps every label the screen reader depends on.
/// </summary>
public sealed class TuiRenderTests
{
    [Fact]
    public async Task A_piped_run_prints_the_plain_dashboard_and_exits_zero()
    {
        var terminal = new ScriptedTuiTerminal(interactive: false);
        var backend = new FakeTuiBackend(TuiFixtures.Data());
        var command = new TuiCommand(backend, terminal);

        int exit = await command.RunAsync(new TuiFlags(Once: false, Ci: false, NoColor: false));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(terminal.Frames);
        Assert.Contains("coppice · Dashboard", terminal.Plain.ToString(), StringComparison.Ordinal);
        Assert.Contains("[Snapshot]", terminal.Plain.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', terminal.Plain.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Once_and_ci_are_plain_even_on_a_terminal(bool once, bool ci)
    {
        var terminal = new ScriptedTuiTerminal(interactive: true);
        var backend = new FakeTuiBackend(TuiFixtures.Data());
        var command = new TuiCommand(backend, terminal);

        int exit = await command.RunAsync(new TuiFlags(once, ci, NoColor: false));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(terminal.Frames);
        Assert.NotEmpty(terminal.Plain.ToString());
        Assert.DoesNotContain('\u001b', terminal.Plain.ToString());
    }

    [Fact]
    public async Task The_interactive_loop_renders_each_screen_and_quits_on_q()
    {
        var terminal = new ScriptedTuiTerminal(interactive: true);
        terminal.Enqueue(TuiFixtures.Tab(), TuiFixtures.Letter('q'));
        var backend = new FakeTuiBackend(TuiFixtures.Data());
        var command = new TuiCommand(backend, terminal);

        int exit = await command.RunAsync(new TuiFlags(Once: false, Ci: false, NoColor: false));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal([TuiScreen.Dashboard, TuiScreen.Roots], terminal.Frames.Select(f => f.Screen));
        Assert.Empty(terminal.Plain.ToString());
    }

    [Fact]
    public void Hostile_names_are_rendered_as_text_not_markup()
    {
        var writer = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        var frame = new TuiFrame(
            TuiScreen.Items,
            "Items [red]not a tag[/]",
            [
                new TuiBlock(
                    "Items [blue]",
                    ["NAME", "PATH"],
                    [new TuiRow(["[red]evil[/]", @"C:\cache\[bold]pkg[/]"])]),
            ],
            "status [green]",
            [new TuiKeyHelp("q", "quit")]);

        new SystemTuiTerminal(console).Render(frame);

        string output = writer.ToString();

        Assert.Contains("[red]evil[/]", output, StringComparison.Ordinal);
        Assert.Contains(@"[bold]pkg[/]", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', output);
    }

    [Fact]
    public void The_plain_renderer_prints_a_deterministic_document()
    {
        TuiState state = TuiFixtures.State();
        string first = TuiPlain.Render(state.Frame());
        string second = TuiPlain.Render(TuiFixtures.State().Frame());

        Assert.Equal(first, second);
        Assert.Contains("keys: ", first, StringComparison.Ordinal);
        Assert.Contains("policy: default", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_tui_flags_are_rejected_rather_than_ignored()
    {
        Assert.Throws<ArgumentException>(() => TuiFlags.Parse(["--bogus"]));
        Assert.Throws<ArgumentException>(() => TuiFlags.Parse(["--json"]));
    }
}

using Coppice.Cli;
using Xunit;

namespace Coppice.Tests.Completions;

/// <summary>
/// T-042: shell completions (nice-to-have #5).
/// <para>
/// The scripts are asserted, not executed. Running a generated script would test the developer's shell —
/// and on Windows the Linux shells are not there to run — while the thing this card owns is the content:
/// every verb, every documented flag, and the syntax each shell actually recognises. The command list is
/// read from <see cref="CoppiceApp.Verbs"/> so a newly dispatched verb cannot be missing from all four
/// scripts without this failing.
/// </para>
/// </summary>
public sealed class CompletionScriptTests
{
    public static TheoryData<string, string> ShellMarkers => new()
    {
        { "powershell", "Register-ArgumentCompleter" },
        { "bash", "complete -F" },
        { "zsh", "compdef" },
        { "fish", "complete -c coppice" },
    };

    [Theory]
    [MemberData(nameof(ShellMarkers))]
    public void Each_shell_gets_a_non_empty_script_in_its_own_syntax(string shell, string marker)
    {
        string script = CompletionScript.Render(shell, CoppiceApp.Verbs.Keys);

        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains(marker, script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    public void Every_verb_is_completed(string shell)
    {
        string script = CompletionScript.Render(shell, CoppiceApp.Verbs.Keys);

        Assert.NotEmpty(CoppiceApp.Verbs);
        foreach (string verb in CoppiceApp.Verbs.Keys)
        {
            Assert.Contains(verb, script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    public void Every_documented_common_flag_is_completed(string shell)
    {
        string script = CompletionScript.Render(shell, CoppiceApp.Verbs.Keys);

        foreach (string flag in CompletionScript.Flags)
        {
            // Fish spells a long option as `-l json`; the other three take the literal `--json`.
            string expected = shell == "fish" ? $"-l {flag[2..]}" : flag;
            Assert.Contains(expected, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_verb_list_is_ordinal_sorted_and_deduplicated()
    {
        string[] verbs = ["scan", "audit", "scan", "apply", "tui"];

        string script = CompletionScript.Render("bash", verbs);

        int apply = script.IndexOf("apply", StringComparison.Ordinal);
        int audit = script.IndexOf("audit", StringComparison.Ordinal);
        int scan = script.IndexOf("scan", StringComparison.Ordinal);
        int tui = script.IndexOf("tui", StringComparison.Ordinal);

        Assert.True(apply >= 0 && apply < audit && audit < scan && scan < tui);
        Assert.Equal(1, script.Split("scan", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void The_bash_script_does_not_rely_on_a_completion_package()
    {
        // `_init_completion` is provided by the bash-completion package, not by bash. Using it would make
        // this script fail on a stock shell, which is the opposite of self-contained.
        string script = CompletionScript.Render("bash", CoppiceApp.Verbs.Keys);

        Assert.DoesNotContain("_init_completion", script, StringComparison.Ordinal);
        Assert.Contains("compgen -W", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_names_are_case_insensitive()
    {
        Assert.Equal(
            CompletionScript.Render("powershell", CoppiceApp.Verbs.Keys),
            CompletionScript.Render("PowerShell", CoppiceApp.Verbs.Keys));
    }

    [Theory]
    [InlineData("tcsh")]
    [InlineData("cmd")]
    [InlineData("")]
    public void An_unknown_shell_is_rejected_by_the_generator(string shell)
    {
        Assert.False(CompletionScript.IsSupported(shell));
        Assert.Throws<ArgumentException>(() => CompletionScript.Render(shell, CoppiceApp.Verbs.Keys));
    }

    [Fact]
    public void The_cli_rejects_an_unknown_shell_with_a_usage_error_and_no_stdout()
    {
        string stdout = CaptureStdout(() => CoppiceApp.Run(["completions", "tcsh"]), out int exit);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Theory]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    [InlineData("bash", "complete -F")]
    [InlineData("zsh", "compdef")]
    [InlineData("fish", "complete -c coppice")]
    public void The_cli_wires_the_verb_and_prints_the_script(string shell, string marker)
    {
        string stdout = CaptureStdout(() => CoppiceApp.Run(["completions", shell]), out int exit);

        Assert.Equal(0, exit);
        Assert.Contains(marker, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_rejects_a_missing_shell_argument_without_printing_a_script()
    {
        string stdout = CaptureStdout(() => CoppiceApp.Run(["completions"]), out int exit);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with stdout redirected and returns what it printed.
    /// <para>
    /// The lock is not for intra-class parallelism (xUnit runs one class at a time); it is because
    /// <see cref="Console.Out"/> is process-wide and a future test elsewhere might capture it too.
    /// </para>
    /// </summary>
    private static string CaptureStdout(Func<int> action, out int exit)
    {
        lock (typeof(CompletionScriptTests))
        {
            TextWriter original = Console.Out;
            var captured = new StringWriter();

            try
            {
                Console.SetOut(captured);
                exit = action();
            }
            finally
            {
                Console.SetOut(original);
            }

            return captured.ToString();
        }
    }
}

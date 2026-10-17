using Coppice.Cli;

namespace Coppice.Tests.Support;

/// <summary>
/// Scripted <see cref="IApplyConsole"/>. The confirmation gate is the difference between a tool that asks
/// and one that does not, so a test has to be able to say "this is a terminal" and "the human typed this" —
/// and to prove that when it is NOT a terminal, apply refuses before it reads so much as a plan.
/// </summary>
public sealed class FakeApplyConsole : IApplyConsole
{
    private readonly Queue<string?> _answers = new();

    public FakeApplyConsole(bool interactive = true) => IsInteractive = interactive;

    public bool IsInteractive { get; set; }

    public List<string> Out { get; } = [];

    public List<string> Errors { get; } = [];

    public List<string> Prompts { get; } = [];

    public int ReadLineCalls { get; private set; }

    public FakeApplyConsole Answer(params string?[] answers)
    {
        foreach (string? a in answers)
        {
            _answers.Enqueue(a);
        }

        return this;
    }

    public void WriteLine(string line) => Out.Add(line);

    public void WritePrompt(string text) => Prompts.Add(text);

    public void WriteError(string line) => Errors.Add(line);

    public string? ReadLine()
    {
        ReadLineCalls++;
        return _answers.Count > 0 ? _answers.Dequeue() : null;
    }

    public string AllOutput => string.Join('\n', Out);

    public string AllErrors => string.Join('\n', Errors);
}

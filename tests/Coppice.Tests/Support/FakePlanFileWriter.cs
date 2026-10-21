using Coppice.Cli;

namespace Coppice.Tests.Support;

/// <summary>
/// Captures what <c>plan --out</c> would have written, or fails the write on demand. The command's rule
/// under test is "store the plan, then write the file, and never write an invalid one", so the writer has
/// to be observable without a real disk.
/// </summary>
public sealed class FakePlanFileWriter : IPlanFileWriter
{
    public List<(string Path, string Json)> Writes { get; } = [];

    public Exception? Failure { get; set; }

    public void Write(string path, string json)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Writes.Add((path, json));
    }
}

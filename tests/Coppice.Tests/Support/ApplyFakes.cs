using Coppice.Core.Apply;

namespace Coppice.Tests.Support;

/// <summary>
/// A kill -9, as far as the code under test is concerned.
/// <para>
/// Nothing in the product catches this type, which is the point: the gateway and the quarantine index catch
/// only the exceptions a real filesystem produces, so throwing this simulates the process dying at that
/// instant rather than an operation failing. A crash test that threw an <see cref="IOException"/> instead
/// would exercise a refusal path and prove nothing about recovery.
/// </para>
/// </summary>
public sealed class SimulatedCrash : Exception
{
    public SimulatedCrash(string where)
        : base($"simulated kill -9 at {where}")
    {
    }
}

/// <summary>
/// In-memory <see cref="IAppendLog"/> that can die mid-append or leave a torn final line, so the journal's
/// tolerance rules have something to be tested against.
/// </summary>
public sealed class FakeAppendLog : IAppendLog
{
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);

    /// <summary>Every line appended, across keys, in order.</summary>
    public List<string> Appended { get; } = [];

    /// <summary>Kill once this many matching appends have happened. Null means never.</summary>
    public int? CrashAfterLines { get; set; }

    /// <summary>Kill only when a line of this phase is appended. Null means any phase.</summary>
    public string? CrashOnPhase { get; set; }

    /// <summary>Chop the tail off the last line, as a kill mid-write would.</summary>
    public bool TearLastLine { get; set; }

    public Task AppendLineAsync(string key, string line)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(line);

        if (!_lines.TryGetValue(key, out List<string>? stored))
        {
            stored = [];
            _lines[key] = stored;
        }

        stored.Add(line);
        Appended.Add(line);

        if (CrashAfterLines is int budget && PhaseOf(line) is { } phase
            && (CrashOnPhase is null || string.Equals(phase, CrashOnPhase, StringComparison.Ordinal)))
        {
            _matching++;
            if (_matching >= budget)
            {
                // The kill lands mid-append, so what reached the disk is a prefix with no newline.
                TearTail(key);
                throw new SimulatedCrash($"append #{Appended.Count} ({phase})");
            }
        }

        return Task.CompletedTask;
    }

    private int _matching;

    public Task<string?> ReadAllTextAsync(string key)
    {
        if (!_lines.TryGetValue(key, out List<string>? stored) || stored.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        // The final line carries no newline when it was torn: a kill mid-append leaves a partial line with no
        // terminator, and that is exactly what the reader has to tolerate.
        string joined = string.Join('\n', stored);
        return Task.FromResult<string?>(Torn.Contains(key) ? joined : joined + "\n");
    }

    /// <summary>Keys whose last line was left unterminated.</summary>
    public HashSet<string> Torn { get; } = new(StringComparer.Ordinal);

    /// <summary>The lines under one key, newest last.</summary>
    public IReadOnlyList<string> Lines(string key) => _lines.GetValueOrDefault(key) ?? [];

    /// <summary>Replaces the final line under a key with a prefix of itself — an interrupted append.</summary>
    public void TearTail(string key, int keepCharacters = 12)
    {
        List<string> stored = _lines[key];
        string last = stored[^1];
        stored[^1] = last[..Math.Min(keepCharacters, last.Length)];
        Torn.Add(key);
    }

    /// <summary>Appends raw text with no trailing newline, as a torn write would leave it.</summary>
    public void AppendRaw(string key, string text)
    {
        if (!_lines.TryGetValue(key, out List<string>? stored))
        {
            stored = [];
            _lines[key] = stored;
        }

        stored.Add(text);
    }

    private static string? PhaseOf(string line)
    {
        int index = line.IndexOf("\"phase\":\"", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        int start = index + "\"phase\":\"".Length;
        int end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }
}

using System.Collections.ObjectModel;

namespace Coppice.Ports;

public sealed record ProcessRequest
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed record ProcessResult
{
    public required int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool TimedOut { get; init; }

    public required string ResolvedFileName { get; init; }
}

/// <summary>
/// The only process-spawning capability (04-plugin-contract rule 2). Adapters MUST run the child
/// with a neutral working directory and must never inherit ambient coppice state, so a poisoned
/// tool on PATH cannot be influenced by our environment (E-11).
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs a process and captures output. Never throws on non-zero exit; timeouts return <c>TimedOut = true</c>.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Read-only view of environment variables, home directory, PATH, and the OS family.</summary>
public interface IEnvironment
{
    OperatingSystemKind OS { get; }

    string? GetVariable(string name);

    /// <summary>Machine-level (not user-level) variable lookup, for registry-backed resolution.</summary>
    string? GetMachineVariable(string name);

    string? HomeDirectory { get; }

    string? GetFolderPath(string wellKnownFolder);

    /// <summary>Directories on PATH, in order. Used to detect a poisoned tool (E-11).</summary>
    IReadOnlyList<string> PathEntries { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed record StateDocument
{
    public required string Key { get; init; }

    public required string ContentType { get; init; }

    public required string Json { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// Persisted state: snapshots, plans, audit log, quarantine index. The snapshot store is what makes
/// reports comparable over time (v0.4 history/trends).
/// </summary>
public interface IStateStore
{
    Task<string> WriteAsync(string key, string contentType, string json, CancellationToken cancellationToken = default);

    Task<StateDocument?> ReadAsync(string key, CancellationToken cancellationToken = default);

    IReadOnlyList<string> ListKeys(string prefix);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Progress reporting for long scans. Implementations must be non-blocking.</summary>
public interface IProgressSink
{
    void Report(string stage, string message, int? percentComplete = null);

    static IProgressSink Null { get; } = new NullProgressSink();

    private sealed class NullProgressSink : IProgressSink
    {
        public void Report(string stage, string message, int? percentComplete = null)
        {
        }
    }
}

/// <summary>Structured log. Safety-relevant events (denied, skipped, applied) must reach this.</summary>
public interface ICoppiceLogger
{
    IDisposable BeginScope(string name);

    void Log(CoppiceLogLevel level, string category, string message, Exception? exception = null);

    static ICoppiceLogger Null { get; } = new NullLogger();

    private sealed class NullLogger : ICoppiceLogger
    {
        public IDisposable BeginScope(string name) => Scope.Instance;

        public void Log(CoppiceLogLevel level, string category, string message, Exception? exception = null)
        {
        }

        private sealed class Scope : IDisposable
        {
            public static readonly Scope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

public enum CoppiceLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Convenience helpers so callers do not need string flags.</summary>
public static class ReadOnlyDictionaryExtensions
{
    public static IReadOnlyDictionary<string, string> Empty { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
}

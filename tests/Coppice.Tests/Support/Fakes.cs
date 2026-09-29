using Coppice.Ports;

namespace Coppice.Tests.Support;

/// <summary>
/// Scripted <see cref="IProcessRunner"/>. A resolver test says "when this tool is asked this question,
/// answer that" — including the implausible answers a poisoned tool returns (E-11).
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, Func<ProcessRequest, ProcessResult>> _rules = new(StringComparer.OrdinalIgnoreCase);

    public List<ProcessRequest> Calls { get; } = [];

    /// <summary>Keys the runner does not know about, so a test can assert a tool was never invoked.</summary>
    public List<string> UnknownInvocations { get; } = [];

    public FakeProcessRunner Respond(string fileName, string standardOutput, int exitCode = 0, string? resolvedFileName = null) =>
        Respond(fileName, _ => new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            ResolvedFileName = resolvedFileName ?? fileName,
        });

    public FakeProcessRunner Fail(string fileName, int exitCode, string standardError = "boom") =>
        Respond(fileName, _ => new ProcessResult
        {
            ExitCode = exitCode,
            StandardError = standardError,
            ResolvedFileName = fileName,
        });

    public FakeProcessRunner Respond(string fileName, Func<ProcessRequest, ProcessResult> handler)
    {
        _rules[fileName] = handler;
        return this;
    }

    /// <summary>Simulates a tool that hangs, so a resolver's timeout behaviour can be tested.</summary>
    public FakeProcessRunner Hangs(string fileName, TimeSpan delay)
    {
        _rules[fileName] = _ => new ProcessResult
        {
            ExitCode = -1,
            TimedOut = true,
            ResolvedFileName = fileName,
        };
        HangsByFile[fileName] = delay;
        return this;
    }

    public Dictionary<string, TimeSpan> HangsByFile { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls.Add(request);

        if (HangsByFile.ContainsKey(request.FileName))
        {
            return Task.FromResult(new ProcessResult
            {
                ExitCode = -1,
                TimedOut = true,
                ResolvedFileName = request.FileName,
            });
        }

        if (_rules.TryGetValue(request.FileName, out Func<ProcessRequest, ProcessResult>? handler))
        {
            return Task.FromResult(handler(request));
        }

        UnknownInvocations.Add(request.FileName);
        return Task.FromResult(new ProcessResult
        {
            ExitCode = 127,
            StandardError = "not found",
            ResolvedFileName = request.FileName,
        });
    }
}

public sealed class FakeEnvironment : IEnvironment
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _machineVariables = new(StringComparer.OrdinalIgnoreCase);

    public FakeEnvironment(OperatingSystemKind os = OperatingSystemKind.Linux)
    {
        OS = os;
        HomeDirectory = os switch
        {
            OperatingSystemKind.Windows => @"C:\Users\dev",
            OperatingSystemKind.MacOS => "/Users/dev",
            _ => "/home/dev",
        };
    }

    public OperatingSystemKind OS { get; }

    public string? HomeDirectory { get; set; }

    public IReadOnlyList<string> PathEntries { get; set; } = [];

    public FakeEnvironment With(string name, string value)
    {
        _variables[name] = value;
        return this;
    }

    public FakeEnvironment WithMachine(string name, string value)
    {
        _machineVariables[name] = value;
        return this;
    }

    public FakeEnvironment WithPath(params string[] entries)
    {
        PathEntries = entries;
        return this;
    }

    public string? GetVariable(string name) => _variables.GetValueOrDefault(name);

    public string? GetMachineVariable(string name) => _machineVariables.GetValueOrDefault(name);

    public string? GetFolderPath(string wellKnownFolder) => wellKnownFolder.ToUpperInvariant() switch
    {
        "USERPROFILE" or "HOME" => HomeDirectory,
        "LOCALAPPDATA" => HomeDirectory + (OS == OperatingSystemKind.Windows ? @"\AppData\Local" : "/.local/share"),
        "APPDATA" => HomeDirectory + (OS == OperatingSystemKind.Windows ? @"\AppData\Roaming" : "/.config"),
        _ => null,
    };
}

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; set; }
}

public sealed class FakeStateStore : IStateStore
{
    private readonly Dictionary<string, StateDocument> _documents = new(StringComparer.Ordinal);

    public List<string> Writes { get; } = [];

    public Task<string> WriteAsync(string key, string contentType, string json, CancellationToken cancellationToken = default)
    {
        _documents[key] = new StateDocument
        {
            Key = key,
            ContentType = contentType,
            Json = json,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        Writes.Add(key);
        return Task.FromResult(key);
    }

    public Task<StateDocument?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.GetValueOrDefault(key));

    public IReadOnlyList<string> ListKeys(string prefix) =>
        [.. _documents.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal)];

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _documents.Remove(key);
        return Task.CompletedTask;
    }
}

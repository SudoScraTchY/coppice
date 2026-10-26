using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// How one ecosystem declares its own presence (FR-01).
/// <para>
/// This is registration data, exactly like a location: the scan coordinator asks the registry "is
/// this ecosystem here?" and never learns that .NET answers with <c>dotnet --version</c> while Rust
/// also accepts a marker. A probe is LOCAL evidence only — it names no cache and can grant no delete
/// right, so adding one cannot widen the safety surface.
/// </para>
/// </summary>
public sealed record EcosystemDetection(string? Tool = null, string? Marker = null)
{
    /// <summary>An ecosystem that declares no probe. Reported absent, never guessed present.</summary>
    public static EcosystemDetection None { get; } = new();

    /// <summary>True when the profile asked for detection at all.</summary>
    public bool IsDeclared =>
        !string.IsNullOrWhiteSpace(Tool) || !string.IsNullOrWhiteSpace(Marker);
}

/// <summary>
/// One ecosystem's detection answer, ready to render (FR-01).
/// <para>
/// <see cref="Source"/> is <c>tool</c>, <c>marker</c> or <c>none</c>. A tool that is missing or that
/// answers with an error is ABSENT, never an exception: detection is a fact the report carries, not
/// a reason for the verb to fail. <see cref="Detail"/> is the answer (or the error) so a user can
/// tell "not installed" from "installed but broken".
/// </para>
/// </summary>
public sealed record EcosystemDetectionReport(
    string EcosystemId,
    string DisplayName,
    bool Present,
    string Source,
    string Detail);

/// <summary>
/// Answers "which declared ecosystems are installed here?" through the ports only (FR-01).
/// <para>
/// Every probe is bounded by <see cref="ProbeTimeout"/> and the caller's token is checked between
/// ecosystems and after each probe, for the same reason <c>DoctorService</c> re-asserts it: the
/// production runner reports a cancelled child as <c>TimedOut</c> rather than throwing, so a probe
/// that returns must not be mistaken for a probe that finished.
/// </para>
/// <para>
/// The order is the registry's, sorted by id. There is no per-ecosystem branch here; the descriptor
/// comes from the profile and a fifth ecosystem travels the same path.
/// </para>
/// </summary>
public sealed class EcosystemDetector
{
    /// <summary>The longest a single detection probe may run.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private const int MaxDetailLength = 160;

    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;

    public EcosystemDetector(IFileSystem fs, IProcessRunner runner, IEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);

        _fs = fs;
        _runner = runner;
        _env = env;
    }

    /// <summary>Detects every registered ecosystem. Never throws for a missing or erroring tool.</summary>
    public IReadOnlyList<EcosystemDetectionReport> Detect(
        IEnumerable<EcosystemPlugin> ecosystems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ecosystems);

        var reports = new List<EcosystemDetectionReport>();

        foreach (EcosystemPlugin ecosystem in ecosystems.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reports.Add(Detect(ecosystem, cancellationToken));
        }

        return reports;
    }

    private EcosystemDetectionReport Detect(EcosystemPlugin ecosystem, CancellationToken cancellationToken)
    {
        EcosystemDetection detection = ecosystem.Detection ?? EcosystemDetection.None;

        if (!detection.IsDeclared)
        {
            return Absent(ecosystem, "none", "no detection probe is declared");
        }

        string? toolFailure = null;

        if (!string.IsNullOrWhiteSpace(detection.Tool))
        {
            (bool Present, string Detail, bool Answered) tool =
                ProbeTool(detection.Tool, cancellationToken);

            if (tool.Present)
            {
                return new EcosystemDetectionReport(ecosystem.Id, ecosystem.DisplayName, true, "tool", tool.Detail);
            }

            // A tool that RAN and answered with an error is an answer: the ecosystem's own binary is
            // there but unusable, and that is more useful than silently falling through to a marker.
            if (tool.Answered)
            {
                return Absent(ecosystem, "tool", tool.Detail);
            }

            toolFailure = tool.Detail;
        }

        if (!string.IsNullOrWhiteSpace(detection.Marker))
        {
            string? markerPath = DoctorService.ExpandDefault(detection.Marker, _env);

            if (!string.IsNullOrEmpty(markerPath)
                && (_fs.DirectoryExists(markerPath) || _fs.FileExists(markerPath)))
            {
                return new EcosystemDetectionReport(
                    ecosystem.Id,
                    ecosystem.DisplayName,
                    true,
                    "marker",
                    markerPath);
            }

            string markerFailure = markerPath is null
                ? $"marker '{detection.Marker}' could not be expanded"
                : $"marker '{markerPath}' does not exist";

            return Absent(ecosystem, "none", Join(toolFailure, markerFailure));
        }

        return Absent(ecosystem, "none", toolFailure ?? "no detection probe answered");
    }

    /// <summary>
    /// Runs one version command. <c>Answered</c> distinguishes "the tool ran and said no" from "the
    /// tool is not here", because only the latter is allowed to fall through to a marker probe.
    /// </summary>
    private (bool Present, string Detail, bool Answered) ProbeTool(string command, CancellationToken cancellationToken)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return (false, "the detection command is empty", Answered: true);
        }

        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult result;

        try
        {
            result = _runner.RunAsync(
                new ProcessRequest
                {
                    FileName = parts[0],
                    Arguments = [.. parts.Skip(1)],
                    // Neutral cwd, like every other probe: a project-local shim must not answer for
                    // the machine (LR-6).
                    WorkingDirectory = null,
                    Timeout = ProbeTimeout,
                },
                cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (false, $"{command} was not found ({FirstLine(ex.Message)})", Answered: false);
        }

        // SystemProcessRunner turns a cancelled child into TimedOut, so cancellation must be
        // re-asserted for the caller's stop to be observable.
        cancellationToken.ThrowIfCancellationRequested();

        if (result.TimedOut)
        {
            return (false, $"{command} timed out after {ProbeTimeout.TotalSeconds:0}s", Answered: false);
        }

        // 127 is the port's "the executable could not be started" answer (SystemProcessRunner maps a
        // failed start onto it). That is a MISSING tool, which may still be detected by a marker; any
        // other non-zero exit is the tool itself reporting a problem and is taken as final.
        if (result.ExitCode == 127)
        {
            return (false, $"{command} was not found", Answered: false);
        }

        if (result.ExitCode != 0)
        {
            string error = FirstLine(result.StandardError);
            string suffix = error.Length == 0 ? string.Empty : $": {error}";
            return (false, $"{command} exited {result.ExitCode}{suffix}", Answered: true);
        }

        string answer = FirstLine(result.StandardOutput);

        if (answer.Length == 0)
        {
            return (false, $"{command} answered nothing", Answered: true);
        }

        return (true, $"{command} -> {answer}", Answered: true);
    }

    private static EcosystemDetectionReport Absent(EcosystemPlugin ecosystem, string source, string detail) =>
        new(ecosystem.Id, ecosystem.DisplayName, false, source, detail);

    private static string Join(string? first, string second) =>
        string.IsNullOrEmpty(first) ? second : $"{first}; {second}";

    /// <summary>
    /// The answer, or the first line of an error. Quotes and control characters are stripped because
    /// this string is rendered into a table and into JSON that a user reads.
    /// </summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]
            .Replace('"', '\'')
            .Trim();

        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength] + "...";
    }
}

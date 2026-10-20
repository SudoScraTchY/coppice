using System.Text.Json;
using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Core.Quarantine;
using Coppice.Ports;

namespace Coppice.Cli;

/// <summary>
/// <c>coppice audit show|verify</c> (T-035, FR-17, S-12) — the read-only look at the mutation ledger.
/// <para>
/// <c>show</c> prints the log as it is on disk. <c>verify</c> replays it: the hash chain says no line was
/// edited, reordered or dropped, and a replay against the quarantine index and the filesystem says the log
/// and the machine agree — no index entry without its line (an unlogged mutation), no quarantined content
/// the log cannot account for. Findings are a REPORT, not an abort: 07 reserves exit 3 for refusals that
/// stopped an apply, and evidence the user has to see is a runtime result, so verify exits 1 when errors
/// were found and 0 when the log verifies clean.
/// </para>
/// <para>
/// This command only reads: it opens no file for writing, and the index it consults is read through the
/// same port <c>doctor</c> uses. The one mutating verb remains <c>apply</c>.
/// </para>
/// </summary>
public sealed class AuditCommand
{
    private readonly IFileSystem _fs;
    private readonly IStateStore _state;
    private readonly IAppendLog _log;
    private readonly IApplyConsole _console;

    public AuditCommand(IFileSystem fs, IStateStore state, IAppendLog log, IApplyConsole console)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(console);

        _fs = fs;
        _state = state;
        _log = log;
        _console = console;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? subcommand = null;
        bool json = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--no-color":
                    // Accepted and ignored: this renderer never emits color, which is the point.
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"unknown option '{arg}'");
                    }

                    if (subcommand is not null)
                    {
                        throw new ArgumentException($"audit takes one subcommand, got both '{subcommand}' and '{arg}'");
                    }

                    subcommand = arg;
                    break;
            }
        }

        return subcommand switch
        {
            "show" => Show(json),
            "verify" => Verify(json),
            _ => throw new ArgumentException("audit needs a subcommand: 'show' or 'verify'"),
        };
    }

    private int Show(bool json)
    {
        string? text = _log.ReadAllTextAsync(AuditLog.Key).GetAwaiter().GetResult();

        if (json)
        {
            // The stored lines ARE the JSON record; echoing them byte-faithfully is the honest --json.
            foreach (string line in RawLines(text))
            {
                _console.WriteLine(line);
            }

            return ExitCodes.Success;
        }

        AuditLogContents contents = AuditLines.Parse(text);
        _console.WriteLine($"coppice audit — {contents.Records.Count} mutation(s) on record{TailNote(contents.TornTail, text)}");

        if (contents.Records.Count == 0)
        {
            return ExitCodes.Success;
        }

        _console.WriteLine(string.Empty);

        foreach (AuditRecord record in contents.Records)
        {
            string where = record.To is null
                ? record.From ?? string.Empty
                : $"{record.From} -> {record.To}";

            string what = record.Command is null ? string.Empty : $"  [{record.Command}]";

            _console.WriteLine(
                $"  #{record.Seq}  {record.Ts:yyyy-MM-dd HH:mm:ss'Z'}  {record.Action,-10} {record.Result,-6}  {where}{what}");
        }

        if (contents.TornTail)
        {
            _console.WriteLine(string.Empty);
            _console.WriteLine("the last line is a torn append (the process died mid-write); the record it carried is absent.");
        }

        return ExitCodes.Success;
    }

    private int Verify(bool json)
    {
        var verifier = new AuditVerifier(_log, new QuarantineIndex(_state), _fs);
        AuditVerifyReport report = verifier.VerifyAsync().GetAwaiter().GetResult();

        if (json)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                ok = report.Ok,
                records = report.RecordsRead,
                lines = report.LineCount,
                tornTail = report.TornTail,
                findings = report.Findings.Select(f => new
                {
                    severity = f.Severity.ToString(),
                    code = f.Code,
                    line = f.Line,
                    message = f.Message,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));

            return report.Ok ? ExitCodes.Success : ExitCodes.RuntimeError;
        }

        _console.WriteLine(
            $"coppice audit verify — {report.RecordsRead} record(s) over {report.LineCount} line(s){TailNote(report.TornTail, null)}");

        foreach (AuditFinding finding in report.Findings)
        {
            string at = finding.Line is int line ? $"line {line}: " : string.Empty;
            _console.WriteLine($"  ! {finding.Severity.ToString().ToUpperInvariant()} {finding.Code}: {at}{finding.Message}");
        }

        if (report.Ok)
        {
            _console.WriteLine("verified: the chain is intact and the log agrees with the quarantine index.");
            return ExitCodes.Success;
        }

        _console.WriteLine(
            $"{report.Findings.Count} finding(s), {report.Errors} of them fatal. The audit log does not verify.");
        return ExitCodes.RuntimeError;
    }

    private static string TailNote(bool tornTail, string? text) =>
        tornTail || AuditLines.Parse(text).TornTail ? " (plus a torn final line)" : string.Empty;

    private static IEnumerable<string> RawLines(string? text)
    {
        if (text is null)
        {
            yield break;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}

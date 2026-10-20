using Coppice.Core.Apply;
using Coppice.Core.Quarantine;
using Coppice.Ports;

namespace Coppice.Core.Audit;

public enum AuditSeverity
{
    /// <summary>Expected imperfection: a torn tail from a kill mid-append.</summary>
    Info,

    /// <summary>The log and the machine disagree in a way that has a benign explanation, and the user should decide.</summary>
    Warning,

    /// <summary>The log does not verify: a line was edited, dropped, or a mutation never got logged.</summary>
    Error,
}

public sealed record AuditFinding(AuditSeverity Severity, string Code, int? Line, string Message);

public sealed record AuditVerifyReport(
    bool Ok,
    IReadOnlyList<AuditFinding> Findings,
    int RecordsRead,
    int LineCount,
    bool TornTail)
{
    public int Errors => Findings.Count(f => f.Severity == AuditSeverity.Error);

    public int Warnings => Findings.Count(f => f.Severity == AuditSeverity.Warning);
}

/// <summary>
/// Replays the audit log against the machine (T-035, FR-17, S-12): does the log still verify, and do the
/// log, the quarantine index and the filesystem tell one story?
/// <para>
/// Three checks, in the order a reader needs them:
/// <b>chain</b> — every line hashes to itself and links to the line before it, so editing, reordering or
/// dropping a line is detectable (a torn tail is the one tolerated gap);
/// <b>unlogged mutations</b> — every quarantine index entry has a line that records its move, which is the
/// "zero unlogged mutations" guarantee read from the other side;
/// <b>orphans</b> — every quarantine line with no restore or prune after it still agrees with the world: an
/// index entry, or content on disk (the case a failed index write produces — the gateway warned about it in
/// its result, and this is where it resurfaces), or a finding.
/// </para>
/// <para>
/// Findings are a report, not an exception: the log is evidence, and the caller decides. <see cref="AuditVerifyReport.Ok"/>
/// is false only when an <see cref="AuditSeverity.Error"/> finding exists.
/// </para>
/// </summary>
public sealed class AuditVerifier
{
    private readonly IAppendLog _log;
    private readonly QuarantineIndex? _index;
    private readonly IFileSystem _fs;

    public AuditVerifier(IAppendLog log, QuarantineIndex? index, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(fs);
        _log = log;
        _index = index;
        _fs = fs;
    }

    public async Task<AuditVerifyReport> VerifyAsync()
    {
        string? text = await _log.ReadAllTextAsync(AuditLog.Key).ConfigureAwait(false);
        AuditLogContents contents = AuditLines.Parse(text);
        var findings = new List<AuditFinding>();

        if (contents.TornTail)
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Info,
                "torn-tail",
                contents.TornLine,
                "A torn append (the process died mid-write) was left in the log; the record it carried is absent."));
        }

        if (contents.UnreadableLine is int unreadable)
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Error,
                "unreadable-line",
                unreadable,
                $"Line {unreadable} is neither an audit record nor the torn tail. The log was edited, or the disk lied."));
        }

        VerifyChain(contents, findings);
        IReadOnlyList<QuarantineEntry> entries = await ListEntriesAsync().ConfigureAwait(false);
        VerifyUnloggedMutations(contents, entries, findings);
        await VerifyOrphansAsync(contents, entries, findings).ConfigureAwait(false);

        return new AuditVerifyReport(
            findings.All(f => f.Severity != AuditSeverity.Error),
            findings,
            contents.Records.Count,
            contents.LineCount,
            contents.TornTail);
    }

    /// <summary>
    /// Every record must hash to itself and link backwards to the record before it. The link is checked
    /// against the hash AS STORED on the previous line, so one tampered line produces one hash finding
    /// instead of cascading through every line after it.
    /// </summary>
    private static void VerifyChain(AuditLogContents contents, List<AuditFinding> findings)
    {
        long expectedSeq = 1;
        string expectedPrev = AuditRecord.Genesis;

        foreach (AuditRecord record in contents.Records)
        {
            if (record.Seq != expectedSeq)
            {
                findings.Add(new AuditFinding(
                    AuditSeverity.Error,
                    "chain-seq",
                    null,
                    $"Record {record.Seq} is out of order: the chain expected {expectedSeq}. A line was dropped or inserted."));
            }

            if (!string.Equals(record.Prev, expectedPrev, StringComparison.Ordinal))
            {
                findings.Add(new AuditFinding(
                    AuditSeverity.Error,
                    "chain-link",
                    null,
                    $"Record {record.Seq} does not link to the record before it. A line was deleted, reordered, or copied in."));
            }

            if (record.Hash is null || !string.Equals(record.Hash, record.ComputeHash(), StringComparison.Ordinal))
            {
                findings.Add(new AuditFinding(
                    AuditSeverity.Error,
                    "chain-hash",
                    null,
                    $"Record {record.Seq} ({record.Action} on '{record.From ?? record.StepId}') does not hash to its stored value. Its content was edited after it was written."));
            }

            // The next link is checked against what is ON DISK here, tampered or not, on purpose.
            expectedSeq = record.Seq + 1;
            expectedPrev = string.IsNullOrEmpty(record.Hash) ? expectedPrev : record.Hash;
        }
    }

    /// <summary>
    /// A live index entry is a move that happened, so the log must have its line. A missing restore or prune
    /// would have removed the entry; its presence with no line means a mutation was never logged.
    /// </summary>
    private static void VerifyUnloggedMutations(
        AuditLogContents contents,
        IReadOnlyList<QuarantineEntry> entries,
        List<AuditFinding> findings)
    {
        foreach (QuarantineEntry entry in entries)
        {
            bool logged = contents.Records.Any(record =>
                record.Action == AuditRecord.QuarantineAction
                && record.Result == AuditRecord.OkResult
                && string.Equals(record.To, entry.QuarantinePath, StringComparison.Ordinal));

            if (!logged)
            {
                findings.Add(new AuditFinding(
                    AuditSeverity.Error,
                    "unlogged-mutation",
                    null,
                    $"'{entry.OriginalPath}' is in the quarantine index at '{entry.QuarantinePath}', but no audit line records the move."));
            }
        }
    }

    /// <summary>
    /// A quarantine line that no restore or prune answered must still agree with the world: the index has
    /// the entry (consistent), the content is on disk (the failed-index-write orphan — restorable by hand),
    /// or neither, which means the content vanished without the log hearing about it.
    /// </summary>
    private async Task VerifyOrphansAsync(
        AuditLogContents contents,
        IReadOnlyList<QuarantineEntry> entries,
        List<AuditFinding> findings)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);

        foreach (AuditRecord record in contents.Records)
        {
            if (record.Action is AuditRecord.RestoreAction or AuditRecord.PruneAction && record.From is not null)
            {
                answered.Add(record.From);
            }
        }

        foreach (AuditRecord record in contents.Records)
        {
            if (record.Action != AuditRecord.QuarantineAction || record.Result != AuditRecord.OkResult)
            {
                continue;
            }

            string to = record.To ?? string.Empty;
            if (to.Length == 0 || answered.Contains(to))
            {
                continue;
            }

            if (entries.Any(entry => string.Equals(entry.QuarantinePath, to, StringComparison.Ordinal)))
            {
                continue;
            }

            bool present = _fs.DirectoryExists(to) || _fs.FileExists(to);
            findings.Add(present
                ? new AuditFinding(
                    AuditSeverity.Warning,
                    "orphaned-content",
                    null,
                    $"'{to}' is quarantined content with no index entry and no restore or prune after it. The index write failed when it moved; `quarantine restore` cannot list it, and it still holds the bytes.")
                : new AuditFinding(
                    AuditSeverity.Warning,
                    "missing-content",
                    null,
                    $"'{to}' is neither in the quarantine index nor on disk, and no restore or prune explains where it went."));
        }
    }

    private async Task<IReadOnlyList<QuarantineEntry>> ListEntriesAsync() =>
        _index is null ? [] : await _index.ListAsync().ConfigureAwait(false);
}

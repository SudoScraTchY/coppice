using Coppice.Core.Apply;
using Coppice.Ports;

namespace Coppice.Core.Audit;

/// <summary>
/// The append-only audit log writer (T-035, S-12): one hash-chained JSONL line per mutation, at
/// <see cref="Key"/> under the state root, next to the journals it differs from on purpose.
/// <para>
/// The chain state — last sequence number and last hash — is recovered FROM the log on a writer's first
/// append of a process, so a writer dying never forks the chain: whatever is on disk is what the next line
/// links to. A torn final line is terminated before the next record, exactly as the journal treats its own
/// torn tail; the record the kill interrupted is simply absent, and the verifier reports that absence as
/// the gap it is rather than guessing at it.
/// </para>
/// <para>
/// Rotation is the append log's business (<c>FileAppendLog</c> rolls at a size cap and never discards), so
/// this writer is agnostic to it: reading back every segment oldest-first keeps the chain verifiable across
/// a roll.
/// </para>
/// </summary>
public sealed class AuditLog
{
    public const string Key = "audit/audit.jsonl";

    private readonly IAppendLog _log;
    private readonly IClock _clock;
    private bool _recovered;
    private long _seq;
    private string _prev = AuditRecord.Genesis;

    public AuditLog(IAppendLog log, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);
        _log = log;
        _clock = clock;
    }

    /// <summary>
    /// Appends one mutation record, stamping the time and filling in the sequence number, back-link and
    /// hash. The caller describes the mutation; the writer makes it verifiable.
    /// </summary>
    public async Task AppendAsync(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_recovered)
        {
            await RecoverAsync().ConfigureAwait(false);
        }

        // Two shapes of the same record, one hash: the hash is computed over the record with the hash
        // field absent, then embedded — a reader recomputes exactly that and compares.
        AuditRecord stamped = record with { Ts = _clock.UtcNow, Seq = ++_seq, Prev = _prev };
        AuditRecord sealedRecord = stamped with { Hash = stamped.ComputeHash() };
        await _log.AppendLineAsync(Key, sealedRecord.Serialize()).ConfigureAwait(false);
        _prev = sealedRecord.Hash!;
    }

    /// <summary>The whole log — every rolled segment oldest-first, then the current one.</summary>
    public Task<string?> ReadAllTextAsync() => _log.ReadAllTextAsync(Key);

    private async Task RecoverAsync()
    {
        _recovered = true;

        string? text = await _log.ReadAllTextAsync(Key).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!text.EndsWith('\n'))
        {
            // A kill mid-append tore the last line. Terminate it so the next record starts on a line of its
            // own; the record it was carrying is absent and verify reports that absence.
            await _log.AppendLineAsync(Key, string.Empty).ConfigureAwait(false);
            text += "\n";
        }

        if (AuditLines.Parse(text).Records is [.., AuditRecord last])
        {
            _seq = last.Seq;
            _prev = string.IsNullOrEmpty(last.Hash) ? AuditRecord.Genesis : last.Hash;
        }
    }
}

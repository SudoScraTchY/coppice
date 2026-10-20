using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppice.Core.Audit;

/// <summary>
/// One line of the audit log (T-035, FR-17, S-12): the record of one mutation that HAPPENED.
/// <para>
/// The field set is 10-formats' audit schema — ts, planId, stepId, eco, kind, action, from, to, bytes,
/// result — plus the tamper-evidence S-12's "verifiable" demands: a sequence number, <see cref="Prev"/>
/// (the previous line's hash), and <see cref="Hash"/>, the SHA-256 of exactly this line with its own hash
/// absent. Editing any field of a written line changes what <see cref="ComputeHash"/> returns; deleting or
/// reordering lines breaks the <see cref="Prev"/> chain; <see cref="AuditVerifier"/> checks both.
/// </para>
/// <para>
/// Unknown fields are ignored on read — the same choice the journal made — so a later build can extend the
/// record without invalidating older logs. <see cref="Command"/> is such an extension: the native command
/// line, which is the one fact an audit reader most wants for an <c>action=native</c> line.
/// </para>
/// <para>
/// A refusal is NOT a record: nothing moved, and the journal already carries the outcome. The audit log is
/// the ledger of mutations, which is what makes "zero unlogged mutations" checkable.
/// </para>
/// </summary>
public sealed record AuditRecord
{
    public const string QuarantineAction = "quarantine";
    public const string RestoreAction = "restore";
    public const string PruneAction = "prune";
    public const string NativeAction = "native";

    public const string OkResult = "ok";
    public const string FailedResult = "failed";

    /// <summary>The <see cref="Prev"/> value of the first line, when there is no previous line to link to.</summary>
    public const string Genesis = "genesis";

    /// <summary>1-based position in the chain. Filled by the writer, never by the caller.</summary>
    public long Seq { get; init; }

    /// <summary>The previous line's <see cref="Hash"/>, or <see cref="Genesis"/> for the first line.</summary>
    public string Prev { get; init; } = Genesis;

    /// <summary>This line's hash over its serialized form with the hash absent. Filled by the writer.</summary>
    public string? Hash { get; init; }

    /// <summary>When the mutation happened. Stamped by the writer's clock.</summary>
    public DateTimeOffset Ts { get; init; }

    /// <summary>The plan that caused the mutation, when it came from an apply.</summary>
    public string? PlanId { get; init; }

    /// <summary>The plan step / quarantine item the mutation acted on.</summary>
    public string? StepId { get; init; }

    public string? Eco { get; init; }

    public string? Kind { get; init; }

    /// <summary><c>quarantine</c>, <c>restore</c>, <c>prune</c>, or <c>native</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Where the content was: the original path for a quarantine, the quarantine path for a restore or prune, the cache root for a native run.</summary>
    public string? From { get; init; }

    /// <summary>Where the content went: the quarantine path for a quarantine, the original path for a restore. Null when nothing moved.</summary>
    public string? To { get; init; }

    public ulong? Bytes { get; init; }

    /// <summary><c>ok</c> or <c>failed</c> — a native command that ran and failed still mutated the machine, so it is recorded as failed, not omitted.</summary>
    public required string Result { get; init; }

    /// <summary>The native command's exit code. Absent for file moves.</summary>
    public int? ExitCode { get; init; }

    /// <summary>How long a native command ran before it exited or was killed, in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The native command line as dispatched. Absent for file moves.</summary>
    public string? Command { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// The hash a written line must carry: SHA-256 over this record's serialized form with the hash field
    /// absent. Recomputed on read, so any edit to a written line is detectable.
    /// </summary>
    public string ComputeHash()
    {
        string payload = JsonSerializer.Serialize(this with { Hash = null }, JsonOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

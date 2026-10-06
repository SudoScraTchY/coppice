using Coppice.Core.Domain;

namespace Coppice.Core.Gateway;

/// <summary>
/// Why a step was not executed (T-031, 06-safety-model).
/// <para>
/// Every value is a distinct reason a step does not run, and each names the gate that stopped it. The
/// gateway's whole value is that a step either passes every check or does not happen — so these are not
/// diagnostic niceties, they are the record of what the tool declined to do.
/// </para>
/// </summary>
public enum GatewayRefusal
{
    /// <summary>It ran. The only non-refusal value; named so a result is never left ambiguous.</summary>
    None = 0,

    /// <summary>The path could not be canonicalized.</summary>
    NotCanonicalizable,

    /// <summary>The path is not inside its validated root (S-14).</summary>
    OutsideRoot,

    /// <summary>The path resolves to the root itself (S-13).</summary>
    IsRoot,

    /// <summary>The path is on the denylist floor.</summary>
    Denied,

    /// <summary>The path does not match the location's allowlist pattern.</summary>
    NotAllowlisted,

    /// <summary>Link resolution did not terminate (T-028).</summary>
    LinkLoop,

    /// <summary>Something holds the item open (S-10). Skipped, never forced.</summary>
    Locked,

    /// <summary>
    /// Live state differs from the snapshot under <c>--strict</c> (S-2). Aborts THIS step only — other
    /// steps are unaffected, because one drifted package says nothing about the rest of the plan.
    /// </summary>
    Drifted,

    /// <summary>The action needs elevation this build does not have (S-7).</summary>
    NeedsElevation,

    /// <summary>Installer-owned; report-only with routing guidance (S-9).</summary>
    InstallerOwned,

    /// <summary>The plan itself failed validation, so no step in it is trustworthy.</summary>
    PlanInvalid,
}

/// <summary>The outcome of one step.</summary>
/// <param name="ItemId">Which step this was.</param>
/// <param name="Result">
/// <see cref="GatewayOutcome.Quarantined"/> for a path move, <see cref="GatewayOutcome.Executed"/> for a
/// native command, and the refusal otherwise. Three outcomes rather than a bool, because "it ran" and
/// "it was deliberately declined" are different facts and a caller auditing the log needs both.
/// </param>
/// <param name="Refusal">Why it did not run, or <see cref="GatewayRefusal.None"/>.</param>
/// <param name="QuarantinePath">Where the item now lives, for a restore. Null when nothing moved.</param>
/// <param name="Message">Human-readable explanation, always populated when a step is refused.</param>
public sealed record GatewayStepResult(
    string ItemId,
    GatewayOutcome Result,
    GatewayRefusal Refusal,
    string? QuarantinePath,
    string Message)
{
    public bool Succeeded => Result is GatewayOutcome.Quarantined or GatewayOutcome.Executed;
}

/// <summary>What actually happened to a step.</summary>
public enum GatewayOutcome
{
    /// <summary>Refused. <see cref="GatewayStepResult.Refusal"/> says why.</summary>
    Skipped,

    /// <summary>Moved to quarantine. Restorable.</summary>
    Quarantined,

    /// <summary>A native command ran (e.g. <c>go clean -modcache</c>). Not restorable — the tool owns it.</summary>
    Executed,
}

/// <summary>
/// What the gateway needs to know about a plan step before it will touch anything (T-031, S-2).
/// <para>
/// Deliberately a separate type from <see cref="PlanStep"/>. A plan file is DATA that arrived from disk and
/// carries only what the builder put there; this carries the facts the gateway must re-derive at apply
/// time — the validated root, the allowlist pattern, the snapshot fingerprint. Building it is a separate,
/// testable step, and a caller cannot hand the gateway a <see cref="PlanStep"/> and skip that.
/// </para>
/// </summary>
public sealed record GatewayStep
{
    public required string ItemId { get; init; }

    /// <summary>The absolute path to act on.</summary>
    public required string Path { get; init; }

    /// <summary>The validated root this path must be inside (S-14).</summary>
    public required string Root { get; init; }

    /// <summary>
    /// Glob the path must match within the root, e.g. <c>*/*</c> for <c>&lt;root&gt;/&lt;name&gt;/&lt;version&gt;</c>.
    /// <para>
    /// Required even though containment already passed, because containment says "inside the root" and this
    /// says "shaped like a cache entry". A root containing a user's own files would otherwise be entirely
    /// fair game.
    /// </para>
    /// </summary>
    public required string AllowlistPattern { get; init; }

    public required RemovalAction Action { get; init; }

    public required Risk Risk { get; init; }

    /// <summary>Path + size + last-write as recorded in the snapshot, for the S-2 drift check.</summary>
    public required SnapshotFingerprint Fingerprint { get; init; }

    /// <summary>Installer-owned roots are report-only (S-9).</summary>
    public bool InstallerOwned { get; init; }
}

/// <summary>
/// The snapshot's record of one item's state, used to detect drift (S-2, T-031 <c>--strict</c>).
/// <para>
/// Size and mtime together, not either alone. Size alone misses a package rewritten in place with the same
/// byte count; mtime alone is defeated by a filesystem with coarse timestamps. Neither is a hash — hashing
/// every package before every apply would cost more than the deletion saves — so this is a cheap check
/// that catches the realistic cases, and it is called a fingerprint rather than a checksum for that reason.
/// </para>
/// </summary>
public sealed record SnapshotFingerprint(string Path, ulong Size, DateTimeOffset LastWriteTimeUtc);

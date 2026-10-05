namespace Coppice.Core.Domain;

/// <summary>
/// How the user wants things treated. Default policy admits <see cref="Risk.Safe"/> only (FR-09)
/// and never admits an item whose usage is <see cref="Usage.Unknown"/> (FR-07, 04-plugin-contract rule 4).
/// </summary>
public sealed record Policy
{
    public static Policy Default { get; } = new()
    {
        PresetName = "default",
    };

    /// <summary>
    /// Confidence floor: refuse anything below this tier. Default is <see cref="Risk.Safe"/>, matching its
    /// ceiling — the previous default of <see cref="Risk.Review"/> sat ABOVE the Safe ceiling, so the
    /// default policy admitted nothing at all and proposed an empty plan on every machine, silently.
    /// </summary>
    public Risk MinimumRisk { get; init; } = Risk.Safe;

    /// <summary>Highest risk the plan is even allowed to contain. Acts as a ceiling above <see cref="MinimumRisk"/>.</summary>
    public Risk MaximumRisk { get; init; } = Risk.Safe;

    /// <summary>
    /// Keep the newest N versions per (name, ecosystem, location). 0 disables the rule.
    /// <para>
    /// Not 0 by default. The rule exists because "unreferenced" does not mean "unused": a package can be
    /// absent from the scanned projects and still be required by one outside them. With the rule off, the
    /// default policy would propose removing every version of every unreferenced package.
    /// </para>
    /// </summary>
    public int KeepLatestN { get; init; } = 2;

    /// <summary>Referenced items are never removed, whatever else says so.</summary>
    public bool ProtectReferenced { get; init; } = true;

    /// <summary>Never plan an item whose usage is Unknown.</summary>
    public bool ProtectUnknownUsage { get; init; } = true;

    /// <summary>
    /// Paths and path patterns this policy never proposes, as declared in config.
    /// <para>
    /// Compared by VALUE in <see cref="Equals(Policy?)"/>. Without that, the compiler-generated
    /// equality would compare this list by reference: a policy that round-tripped through JSON would
    /// deserialize to a <c>List&lt;string&gt;</c> and never equal the array it came from, so a plan and its
    /// own restored copy would report themselves as different documents. A plan is expected to equal the
    /// plan it was restored from.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>Named policy preset, for deterministic plan output and report headers (FR-08).</summary>
    public required string PresetName { get; init; }

    public bool Equals(Policy? other) =>
        other is not null
        && PresetName == other.PresetName
        && MinimumRisk == other.MinimumRisk
        && MaximumRisk == other.MaximumRisk
        && KeepLatestN == other.KeepLatestN
        && ProtectReferenced == other.ProtectReferenced
        && ProtectUnknownUsage == other.ProtectUnknownUsage
        && Exclusions.SequenceEqual(other.Exclusions, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PresetName, StringComparer.Ordinal);
        hash.Add(MinimumRisk);
        hash.Add(MaximumRisk);
        hash.Add(KeepLatestN);
        hash.Add(ProtectReferenced);
        hash.Add(ProtectUnknownUsage);

        // Order matters: exclusions are a set of patterns, but two policies differing only in the order
        // their exclusions were listed are the same policy, so the hash must not depend on that order.
        foreach (string exclusion in Exclusions.Order(StringComparer.Ordinal))
        {
            hash.Add(exclusion, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static Policy Conservative { get; } = Default with
    {
        PresetName = "conservative",
        KeepLatestN = 3,
    };

    public static Policy Aggressive { get; } = Default with
    {
        PresetName = "aggressive",
        MaximumRisk = Risk.Review,
        KeepLatestN = 1,
    };
}

public sealed record RemovalAction(
    RemovalKind Kind,
    string? CommandLine = null,
    string? CanonicalPath = null,
    string? RouteNote = null)
{
    public static RemovalAction PathDelete(string canonicalPath) => new(RemovalKind.PathDelete, CanonicalPath: canonicalPath);

    public static RemovalAction Native(string commandLine) => new(RemovalKind.NativeCommand, CommandLine: commandLine);

    public static RemovalAction ReportOnly(string routeNote) => new(RemovalKind.ReportOnly, RouteNote: routeNote);
}

public sealed record PlanStep(
    string ItemId,
    Risk Risk,
    string Reason,
    RemovalAction Action,
    ulong ExpectedBytes)
{
    /// <summary>Every step carries a reason string — a non-empty reason is part of the product contract.</summary>
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
}

/// <summary>
/// A plan: the set of steps Coppice proposes, justified against one snapshot (T-026, FR-11).
/// <para>
/// This is an envelope, not a bare record, for the same reason <see cref="Snapshots.Snapshot"/> is: the
/// checksum must cover the CONTENT and not the identity fields. <see cref="PlanId"/> is derived from the
/// steps, so including it would make the checksum a function of itself; <see cref="SnapshotId"/> names
/// the state the plan was justified against, not what the plan does. Both are excluded from
/// <see cref="Content"/> for exactly that reason.
/// </para>
/// </summary>
public sealed record Plan
{
    /// <summary>
    /// Schema version of the document. The authoritative value is <see cref="Plans.PlanStore.CurrentSchemaVersion"/>;
    /// a plan written by a build that does not understand it is refused on load, never best-guessed —
    /// a plan is an instruction, and reading an instruction with the wrong schema is how the wrong files
    /// get deleted.
    /// </summary>
    public int SchemaVersion { get; init; } = Plans.PlanStore.CurrentSchemaVersion;

    public required string PlanId { get; init; }

    public required string SnapshotId { get; init; }

    public required Policy Policy { get; init; }

    public required IReadOnlyList<PlanStep> Steps { get; init; }

    /// <summary>
    /// Total bytes the steps expect to reclaim. Always the sum of the steps' expected bytes —
    /// <see cref="Plans.PlanBuilder"/> is the only construction path and computes it, so the two cannot
    /// disagree.
    /// </summary>
    public ulong ExpectedReclaimBytes { get; init; }

    /// <summary>Checksum over <see cref="Content"/>, so tampering is detectable (FR-11).</summary>
    public required string Checksum { get; init; }

    /// <summary>The content the checksum covers: the schema, the policy, and every step.</summary>
    internal PlanContent Content() => new(SchemaVersion, SnapshotId, Policy, Steps, ExpectedReclaimBytes);

    /// <summary>Recomputes the checksum from the content.</summary>
    public string ComputeChecksum() => DomainJson.ComputeChecksum(Content());

    /// <summary>True when the stored checksum matches the content it claims to cover.</summary>
    public bool IsIntact() =>
        string.Equals(Checksum, ComputeChecksum(), StringComparison.OrdinalIgnoreCase);

    internal sealed record PlanContent(
        int SchemaVersion,
        string SnapshotId,
        Policy Policy,
        IReadOnlyList<PlanStep> Steps,
        ulong ExpectedReclaimBytes);

    /// <summary>
    /// Compared by VALUE, including the step list.
    /// <para>
    /// Same reason <see cref="Policy"/> overrides its own equality: the generated comparison would treat
    /// <see cref="Steps"/> as a reference, so a plan deserialized from JSON (which yields a
    /// <c>List&lt;PlanStep&gt;</c>) would never equal the plan that was written. That breaks the one
    /// assertion a plan's round-trip must satisfy, and worse, it makes "did this plan change?" answer no
    /// for every real comparison and yes only when both sides happened to share an array instance.
    /// </para>
    /// </summary>
    public bool Equals(Plan? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && string.Equals(PlanId, other.PlanId, StringComparison.Ordinal)
        && string.Equals(SnapshotId, other.SnapshotId, StringComparison.Ordinal)
        && Policy == other.Policy
        && Steps.SequenceEqual(other.Steps)
        && ExpectedReclaimBytes == other.ExpectedReclaimBytes
        && string.Equals(Checksum, other.Checksum, StringComparison.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(PlanId, StringComparer.Ordinal);
        hash.Add(SnapshotId, StringComparer.Ordinal);
        hash.Add(Policy);

        foreach (PlanStep step in Steps)
        {
            hash.Add(step);
        }

        hash.Add(ExpectedReclaimBytes);
        hash.Add(Checksum, StringComparer.Ordinal);

        return hash.ToHashCode();
    }
}

/// <summary>
/// A health finding. <see cref="Detail"/> is the same opaque primitive-only value bag as
/// <see cref="Facts"/> so a Problem compares structurally after a JSON round-trip; a bare
/// <c>IReadOnlyDictionary</c> would compare by reference and report a false difference.
/// </summary>
public sealed record Problem
{
    public required string Ecosystem { get; init; }

    public required string Code { get; init; }

    public required Severity Severity { get; init; }

    public required string Summary { get; init; }

    public string? Path { get; init; }

    public Facts Detail { get; init; } = Facts.Empty;
}

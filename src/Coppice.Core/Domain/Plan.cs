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

    public Risk MinimumRisk { get; init; } = Risk.Review;

    /// <summary>Highest risk the plan is even allowed to contain. Acts as a ceiling above <see cref="MinimumRisk"/>.</summary>
    public Risk MaximumRisk { get; init; } = Risk.Safe;

    /// <summary>Keep the newest N versions per major/band. 0 disables the rule.</summary>
    public int KeepLatestN { get; init; }

    /// <summary>Referenced items are never removed, whatever else says so.</summary>
    public bool ProtectReferenced { get; init; } = true;

    /// <summary>Never plan an item whose usage is Unknown.</summary>
    public bool ProtectUnknownUsage { get; init; } = true;

    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>Named policy preset, for deterministic plan output and report headers (FR-08).</summary>
    public required string PresetName { get; init; }

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

public sealed record Plan(
    string PlanId,
    string SnapshotId,
    Policy Policy,
    IReadOnlyList<PlanStep> Steps,
    ulong ExpectedReclaimBytes,
    string Checksum);

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

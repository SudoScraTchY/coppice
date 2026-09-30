using Coppice.Core.Domain;

namespace Coppice.Core.Resolution;

/// <summary>
/// One answer from one source, before validation. A source yields zero or more candidates;
/// a location's <see cref="ResolveMode"/> decides whether the first one wins or all of them do.
/// </summary>
public sealed record Candidate
{
    public required string Path { get; init; }

    /// <summary>Which source produced this candidate.</summary>
    public required ResolvedVia Via { get; init; }

    /// <summary>
    /// Provenance detail for the report and UI (LR-7): the environment variable name, the config
    /// key, the tool query, or the registry value this came from. Null for a bare default.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The literal value the source returned, kept verbatim. Resolution is untrusted input
    /// (FR-03): a poisoned tool can return anything, so the original must be reportable even when
    /// the resolved path is rejected.
    /// </summary>
    public string? RawValue { get; init; }

    /// <summary>
    /// True when this source reports the location as currently in use. Used for role assignment:
    /// a source that names the old default but reports it inactive yields <see cref="RootRole.Inactive"/>.
    /// </summary>
    public bool IsLive { get; init; } = true;

    public override string ToString() =>
        Detail is null ? $"{Via}:{Path}" : $"{Via}({Detail}):{Path}";
}

/// <summary>Whether a location takes the first answer or collects them all (05).</summary>
public enum ResolveMode
{
    /// <summary>First live source in precedence order wins. Multi-root locations need an explicit pin per root.</summary>
    First = 0,

    /// <summary>Every distinct root is kept; extras are <see cref="RootRole.Additional"/> (e.g. SDK roots, fallback folders).</summary>
    All = 1,
}

/// <summary>What one source returned for a location. Sources never throw; absence is not failure.</summary>
public sealed record SourceResult
{
    public static SourceResult None { get; } = new();

    public static SourceResult Of(string path, ResolvedVia via, string? detail = null, string? rawValue = null, bool isLive = true) =>
        new() { Candidate = new Candidate { Path = path, Via = via, Detail = detail, RawValue = rawValue ?? path, IsLive = isLive } };

    /// <summary>The candidate this source produced, or null if it knows nothing about the location.</summary>
    public Candidate? Candidate { get; init; }
}

/// <summary>
/// A location definition: an open string id (OCP — the core never learns a new ecosystem)
/// plus the sources that can answer for it.
/// </summary>
public sealed record LocationSpec
{
    public required string Id { get; init; }

    public required ResolveMode Mode { get; init; }

    /// <summary>The pinned path, if any. A pin still passes every validation gate (05).</summary>
    public string? Pin { get; init; }

    /// <summary>Sources keyed by their precedence. Providers fill this per call.</summary>
    public IReadOnlyDictionary<ResolvedVia, SourceResult> Sources { get; init; } =
        new Dictionary<ResolvedVia, SourceResult>();

    /// <summary>Profile-specific OS defaults, consulted last.</summary>
    public IReadOnlyDictionary<Coppice.Ports.OperatingSystemKind, string> OsDefaults { get; init; } =
        new Dictionary<Coppice.Ports.OperatingSystemKind, string>();

    public Coppice.Ports.OperatingSystemKind OS { get; init; }

    /// <summary>Names the sources in precedence order, for tests and for the doctor report.</summary>
    public static IReadOnlyList<ResolvedVia> PrecedenceOrder { get; } =
        [ResolvedVia.Pin, ResolvedVia.Tool, ResolvedVia.Env, ResolvedVia.Config, ResolvedVia.Registry, ResolvedVia.OsFile, ResolvedVia.Default];

    public static IReadOnlyList<ResolvedVia> OrderFor(LocationSpec spec) =>
        [.. PrecedenceOrder.Where(v => v != ResolvedVia.Default || spec.OsDefaults.ContainsKey(spec.OS))];
}

using Coppice.Core.Domain;

namespace Coppice.Core.Config;

/// <summary>
/// The user's configuration (T-029, FR-22, 10-formats "User config").
/// <para>
/// A schema-versioned document. The version is not decoration: a config written for a different schema is
/// MIGRATED or REFUSED, never best-guessed, because every field in here steers a deletion decision.
/// </para>
/// <para>
/// What a pin is and is not: it overrides <em>resolution</em> — "look here instead of asking the tool" —
/// and nothing else. It cannot move a root past the denylist, past a fingerprint check, or past any other
/// gate in 05. That separation is the single most important property of this file, and it is why
/// <see cref="Pins"/> is consulted in resolution rather than being merged into the validated output.
/// </para>
/// </summary>
public sealed record CoppiceConfig
{
    /// <summary>Schema version of the document. Bumped on any incompatible change.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The authoritative schema version this build understands.</summary>
    public const int SupportedSchemaVersion = CurrentSchemaVersion;

    /// <summary>
    /// Declared version, or <c>null</c> when the file omitted it. A missing version is treated as v1 —
    /// the schema shipped first, so files written before versioning existed are v1 by definition.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Project roots to scan for package references. Empty means "discover", not "none".</summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    public PolicySettings Policy { get; init; } = new();

    /// <summary>
    /// Location id to path, as declared by the user.
    /// <para>
    /// A pin changes which path resolution RETURNS. It does not grant permission: the returned path still
    /// passes every gate in 05, and a pin that names a denylisted or unfingerprintable directory is
    /// reported exactly as a tool answer naming it would be.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Pins { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Paths and path patterns that are never proposed for removal.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    /// <summary>Item names that are never proposed for removal, regardless of path.</summary>
    public IReadOnlyList<string> ExcludedNames { get; init; } = [];

    public QuarantineSettings Quarantine { get; init; } = new();

    /// <summary>
    /// True when the document came from a file rather than defaults. Used so a diagnostic can say
    /// "your config says X" rather than implying the default was chosen.
    /// </summary>
    public bool LoadedFromFile { get; init; }

    /// <summary>
    /// The defaults, used when no config file exists.
    /// <para>
    /// Not an error condition. A user with no config gets working defaults and a hint about where to
    /// create one; refusing to run because a file is absent would make the tool unusable on first launch.
    /// </para>
    /// </summary>
    public static CoppiceConfig Defaults => new();
}

/// <summary>Policy defaults from the config, converted into the domain policy.</summary>
public sealed record PolicySettings
{
    public string Preset { get; init; } = "default";

    /// <summary>0 disables the rule. Absent means "inherit from the preset", which is not the same as 0.</summary>
    public int? KeepLatest { get; init; }

    public bool ProtectReferenced { get; init; } = true;

    public bool ProtectUnknownUsage { get; init; } = true;

    /// <summary>
    /// The domain policy these settings describe.
    /// <para>
    /// A missing <see cref="KeepLatest"/> leaves the preset's value alone. Defaulting a MISSING key to 0
    /// would silently disable keep-latest-N on every config that omits the line, which is the T-025 bug
    /// arriving by a different road.
    /// </para>
    /// </summary>
    public Policy ToPolicy(IReadOnlyList<string>? exclusions = null)
    {
        Policy preset = Preset switch
        {
            "conservative" => Policy.Conservative,
            "aggressive" => Policy.Aggressive,
            _ => Policy.Default,
        };

        return preset with
        {
            KeepLatestN = KeepLatest ?? preset.KeepLatestN,
            ProtectReferenced = ProtectReferenced,
            ProtectUnknownUsage = ProtectUnknownUsage,
            Exclusions = exclusions ?? [],
        };
    }
}

/// <summary>Quarantine settings. Directories default per-volume; see 06.</summary>
public sealed record QuarantineSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Quarantine root override. Null means the per-volume default.</summary>
    public string? Directory { get; init; }

    /// <summary>Days to keep a quarantined item before it is eligible for removal. 0 means "keep indefinitely".</summary>
    public int RetentionDays { get; init; }
}

/// <summary>Why a config was refused. Every case names the file position so the message can point at it.</summary>
public enum ConfigFailure
{
    /// <summary>The file is not valid TOML.</summary>
    Malformed,

    /// <summary>A value has the wrong type, or is outside the allowed set.</summary>
    InvalidValue,

    /// <summary>The declared schema version is not one this build understands.</summary>
    UnsupportedSchemaVersion,

    /// <summary>A key this build does not recognise. Refused rather than ignored: a typo'd
    /// <c>keep_latets</c> must not silently mean "use the default".</summary>
    UnknownKey,

    /// <summary>A table is present but empty where its contents are required.</summary>
    MissingSection,
}

/// <summary>The outcome of loading a config: a document, or the reason there is not one.</summary>
public sealed record ConfigLoadResult
{
    private ConfigLoadResult(CoppiceConfig? config, ConfigFailure? failure, string? message, IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        Config = config;
        Failure = failure;
        Message = message;
        Diagnostics = diagnostics;
    }

    public CoppiceConfig? Config { get; }

    public ConfigFailure? Failure { get; }

    /// <summary>Why the config was refused, phrased for the user who has to fix it.</summary>
    public string? Message { get; }

    /// <summary>Every problem found, not just the first. A config with three typos should say so three times.</summary>
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public bool IsSuccess => Config is not null;

    public static ConfigLoadResult Success(CoppiceConfig config) =>
        new(config, null, null, []);

    public static ConfigLoadResult Fail(ConfigFailure failure, string message, IReadOnlyList<ConfigDiagnostic>? diagnostics = null) =>
        new(null, failure, message, diagnostics ?? []);
}

/// <summary>
/// One problem, located precisely.
/// <para>
/// The acceptance asks for "line/field-level hints", so this carries both: the line where it was read and
/// the dotted path of the key. A message saying "invalid config" forces the user to diff the file by hand;
/// "policy.keep_latest on line 7: expected a non-negative whole number, got -1" tells them what to change.
/// </para>
/// </summary>
public sealed record ConfigDiagnostic(string Field, int Line, string Problem, string? Hint = null)
{
    /// <summary>The one-line form shown to a user.</summary>
    public override string ToString() =>
        Hint is null
            ? $"{Field} (line {Line}): {Problem}"
            : $"{Field} (line {Line}): {Problem} — {Hint}";
}

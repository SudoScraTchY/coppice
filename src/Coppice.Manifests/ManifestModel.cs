using Coppice.Core.Domain;
using Coppice.Core.Resolution;

namespace Coppice.Manifests;

/// <summary>
/// A declarative ecosystem profile, as loaded from TOML (FR-23, 10-formats).
/// <para>
/// This is DATA, not behaviour. Adding Go, Rust or Node must not require touching the core (OCP), so
/// a profile describes locations declaratively and the existing resolution engine consumes the
/// result. Nothing here can grant permission the safety model withholds: a manifest may describe a
/// location, and every path it names still passes denylist and fingerprint validation.
/// </para>
/// </summary>
public sealed record EcosystemManifest
{
    /// <summary>Manifest schema version. Bumped on any breaking shape change (NFR-12).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable ecosystem id, e.g. <c>go</c>. Used for plugin selection and report grouping.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name for the report. Defaults to <see cref="Id"/>.</summary>
    public string DisplayName { get; init; } = "";

    public required IReadOnlyList<LocationSpec> Locations { get; init; }

    /// <summary>Where this manifest came from, for the doctor report and shadowing warnings.</summary>
    public ManifestOrigin Origin { get; init; }

    public string Display => DisplayName.Length == 0 ? Id : DisplayName;
}

/// <summary>Where a manifest was loaded from (10-formats: built-ins are embedded resources).</summary>
public enum ManifestOrigin
{
    /// <summary>A built-in profile compiled into the assembly.</summary>
    BuiltIn = 0,

    /// <summary>A user-supplied file in <c>&lt;config&gt;/ecosystems/*.toml</c>.</summary>
    User = 1,
}

/// <summary>
/// The declarative kinds a manifest may declare (10-formats validation rules).
/// <para>
/// This is deliberately NARROWER than <see cref="RootRole"/> or anything v0.3 might need. A
/// declarative profile describes caches; it cannot describe a toolchain root that must never be
/// cleaned. Anything outside this list is a validation error rather than a silently ignored key,
/// because a manifest that appears to work while ignoring what you wrote is worse than one that
/// refuses to load.
/// </para>
/// </summary>
public enum ManifestCacheKind
{
    /// <summary>A cache of downloaded packages/modules. Contents are regenerable.</summary>
    PackageCache = 0,

    /// <summary>A compiled-artifact cache (build cache, object cache).</summary>
    BuildCache = 1,

    /// <summary>A download/HTTP cache with no structure worth walking.</summary>
    DownloadCache = 2,

    /// <summary>Installed tools or global packages, which have real installation semantics.</summary>
    ToolCache = 3,
}

/// <summary>Declarative provenance for one location's resolution chain (05).</summary>
public sealed record ManifestSource
{
    /// <summary>Which rung of the chain this source occupies. Must be in the closed set.</summary>
    public required ResolvedVia Via { get; init; }

    /// <summary>Command to run for <c>tool</c> sources, e.g. <c>go env GOMODCACHE</c>.</summary>
    public string? Run { get; init; }

    /// <summary>
    /// Environment variable name for <c>env</c> sources. Never a shell expansion — the value is read
    /// through the <c>IEnvironment</c> port, so there is nothing to inject into.
    /// </summary>
    public string? Var { get; init; }

    /// <summary>Config key for <c>config</c> sources.</summary>
    public string? Key { get; init; }

    /// <summary>Literal path for <c>pin</c>/<c>default</c> sources. May contain <c>{home}</c>.</summary>
    public string? Path { get; init; }

    /// <summary>Registry value name for <c>registry</c> sources.</summary>
    public string? RegistryValue { get; init; }

    /// <summary>File to read for <c>os-file</c> sources.</summary>
    public string? File { get; init; }

    /// <summary>
    /// Which built-in parser reads this source's output. Must be in the closed set (10-formats).
    /// Required for <c>tool</c> sources; ignored elsewhere.
    /// </summary>
    public ManifestParserKind? Parse { get; init; }

    /// <summary>True when the source is allowed to say the location is inactive.</summary>
    public bool ReportInactive { get; init; } = true;

    /// <summary>
    /// Value equality by hand. A record's generated equality compares only what it can see structurally,
    /// and every property here is init-only with a List member — so the generated version compares those
    /// Lists BY REFERENCE and two identically-written manifests compare unequal. That is the exact failure
    /// mode a "does this extra key change anything?" test relies on to detect a real change.
    /// </summary>
    public bool Equals(ManifestSource? other) =>
        other is not null
        && Via == other.Via
        && Run == other.Run
        && Var == other.Var
        && Key == other.Key
        && Path == other.Path
        && RegistryValue == other.RegistryValue
        && File == other.File
        && Parse == other.Parse
        && ReportInactive == other.ReportInactive;

    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Via, Run, Var, Key),
            HashCode.Combine(Path, RegistryValue, File),
            HashCode.Combine(Parse, ReportInactive));
}

/// <summary>The closed set of built-in parsers a manifest may name (10-formats).</summary>
public enum ManifestParserKind
{
    /// <summary><c>key: value</c> or <c>key = value</c> lines, as used by <c>dotnet nuget locals --list</c>.</summary>
    LabelValue = 0,

    /// <summary>A single bare value per line, as used by <c>dotnet --list-sdks</c>.</summary>
    Line = 1,

    /// <summary>Bracketed absolute paths, as used by <c>dotnet --list-runtimes</c>.</summary>
    SdkBracketPaths = 2,

    /// <summary>A dotted path into a JSON document, as used by <c>npm config get cache</c> style output.</summary>
    JsonPath = 3,

    /// <summary>
    /// A regular expression. Permitted because the spec names it, but flagged in the audit: it is the
    /// one parser whose behaviour is not checkable by reading the parser itself.
    /// </summary>
    Regex = 4,
}

/// <summary>Fingerprint expectations for a location (06-safety-model).</summary>
public sealed record ManifestFingerprint
{
    /// <summary>
    /// Fraction of depth-1 entries that must match <see cref="EntryPatterns"/> before delete rights
    /// are granted. Zero means "no check", which is correct when the profile has nothing to assert.
    /// </summary>
    public double LayoutRatio { get; init; }

    /// <summary>Glob-ish patterns a layout entry must match, e.g. <c>*.nupkg</c>.</summary>
    public IReadOnlyList<string> EntryPatterns { get; init; } = [];

    /// <summary>Marker files/directories that must exist. Checked at depth 1.</summary>
    public IReadOnlyList<string> RequiredMarkers { get; init; } = [];

    /// <summary>Value equality by hand — the List members would otherwise compare by reference.</summary>
    public bool Equals(ManifestFingerprint? other) =>
        other is not null
        && LayoutRatio.Equals(other.LayoutRatio)
        && EntryPatterns.SequenceEqual(other.EntryPatterns)
        && RequiredMarkers.SequenceEqual(other.RequiredMarkers);

    public override int GetHashCode() => HashCode.Combine(LayoutRatio, EntryPatterns.Count, RequiredMarkers.Count);
}

/// <summary>A native removal route, e.g. <c>go clean -modcache</c>.</summary>
public sealed record ManifestRemoval
{
    public required string Command { get; init; }
}

/// <summary>One location declared by a manifest.</summary>
public sealed record ManifestLocation
{
    public required string Id { get; init; }

    public required ManifestCacheKind Kind { get; init; }

    /// <summary>Relative layout under the root, e.g. <c>{name}/{version}</c>. Empty when the whole root is one unit.</summary>
    public string Layout { get; init; } = "";

    /// <summary><c>first</c> or <c>all</c>. Anything else is a validation error.</summary>
    public required ResolveMode Resolve { get; init; }

    public required IReadOnlyList<ManifestSource> Sources { get; init; }

    public ManifestFingerprint Fingerprint { get; init; } = new();

    public ManifestRemoval? Removal { get; init; }

    /// <summary>
    /// Confidence penalty applied to locations resolved without a tool source. Cargo's registry has no
    /// query command, so 09 requires the report to SHOW the lower confidence rather than hide it.
    /// </summary>
    public double ConfidencePenalty { get; init; }

    /// <summary>
    /// True when the root is owned by an installer rather than the user. Installer-owned roots are
    /// never given a native removal command and are never cleaned.
    /// </summary>
    public bool InstallerOwned { get; init; }
}

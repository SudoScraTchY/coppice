using Coppice.Core.Resolution;

namespace Coppice.Manifests;

/// <summary>One validation failure, with enough detail to act on without reading the source (NFR-10).</summary>
public sealed record ManifestError(string Field, string Where, string Problem, string Fix)
{
    /// <summary>The single line a user sees. Format: <c>field: problem — fix</c>.</summary>
    public string ToDisplayString() => $"{Where}: {Field}: {Problem}. {Fix}";
}

/// <summary>A non-fatal observation. Surfaced, never silently dropped.</summary>
public sealed record ManifestWarning(string Field, string Where, string Problem)
{
    public string ToDisplayString() => $"{Where}: {Field}: {Problem}";
}

/// <summary>
/// One location as read from TOML, before conversion into the engine's own types. Public because it
/// appears in <see cref="ManifestValidationResult"/>'s deconstructor, and because a caller reporting a
/// rejected manifest needs to see what was actually read.
/// </summary>
public sealed record LocationRecord(
    string Id,
    string KindText,
    ManifestCacheKind Kind,
    Coppice.Core.Resolution.ResolveMode Resolve,
    string Layout,
    IReadOnlyList<ManifestSource> Sources,
    ManifestFingerprint Fingerprint,
    string? RemoveCommand,
    double ConfidencePenalty,
    bool InstallerOwned)
{
    /// <summary>
    /// Value equality by hand. Two manifests written identically must compare EQUAL, or a test asking
    /// "does adding this key change anything?" cannot answer its own question — it would report a change
    /// when the only difference is a freshly-allocated List. Every List member is compared element-wise.
    /// </summary>
    public bool Equals(LocationRecord? other) =>
        other is not null
        && Id == other.Id
        && KindText == other.KindText
        && Kind == other.Kind
        && Resolve == other.Resolve
        && Layout == other.Layout
        && Sources.SequenceEqual(other.Sources)
        && Fingerprint.Equals(other.Fingerprint)
        && RemoveCommand == other.RemoveCommand
        && ConfidencePenalty.Equals(other.ConfidencePenalty)
        && InstallerOwned == other.InstallerOwned;

    public override int GetHashCode() => HashCode.Combine(Id, Kind, Resolve, Layout, RemoveCommand, ConfidencePenalty, InstallerOwned);
}

/// <summary>Outcome of validating one manifest.</summary>
public sealed record ManifestValidationResult(
    IReadOnlyList<ManifestError> Errors,
    IReadOnlyList<ManifestWarning> Warnings,
    int SchemaVersion,
    string? Id,
    string DisplayName,
    IReadOnlyList<LocationRecord> Locations,
    ManifestOrigin Origin,
    string SourceName)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Parses a manifest and validates it in one step.</summary>
    public static ManifestValidationResult FromToml(string text, ManifestOrigin origin, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            Toml.TomlTable table = Toml.TomlReader.Parse(text);
            return ManifestValidator.Validate(table, origin, sourceName);
        }
        catch (Toml.TomlException ex)
        {
            return new ManifestValidationResult(
                [new ManifestError("toml", sourceName, $"parse error on line {ex.LineNumber} — {ex.Detail}", "fix the TOML syntax at that line")],
                [],
                1,
                null,
                string.Empty,
                [],
                origin,
                sourceName);
        }
    }

    /// <summary>
    /// Builds the manifest the engine consumes, or null when validation failed.
    /// <para>
    /// Returning null rather than a half-built object is deliberate: a manifest with errors has no
    /// meaningful subset, and a caller that ignored the error list would then resolve locations from a
    /// profile that is partly fiction.
    /// </para>
    /// </summary>
    public EcosystemManifest? ToManifest()
    {
        if (!IsValid || Id is null)
        {
            return null;
        }

        var specs = new List<LocationSpec>(Locations.Count);

        foreach (LocationRecord record in Locations)
        {
            specs.Add(new LocationSpec
            {
                Id = record.Id,
                Mode = record.Resolve,
            });
        }

        return new EcosystemManifest
        {
            SchemaVersion = SchemaVersion,
            Id = Id,
            DisplayName = DisplayName,
            Locations = specs,
            Origin = Origin,
        };
    }
}

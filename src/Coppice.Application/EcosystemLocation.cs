using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Manifests;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// One rung of a location's resolution chain, in the portable shape the resolver consumes.
/// <para>
/// This mirrors <see cref="ManifestSource"/> rather than reusing it so a code-backed profile (.NET)
/// and a manifest-backed profile (Go/Rust/Node) produce the SAME descriptor. The scan coordinator then
/// has one kind of location to resolve and never asks which ecosystem it came from.
/// </para>
/// </summary>
public sealed record LocationSource(
    ResolvedVia Via,
    string? Run = null,
    ManifestParserKind? Parse = null,
    string? Label = null,
    string? Variable = null,
    string? PathTemplate = null,
    bool PathIsSdkInstall = false);

/// <summary>
/// Everything the application needs to resolve and scan one location, independent of whether the
/// profile came from code or from TOML. This is registration DATA: adding a location is adding a
/// descriptor, never a branch in <see cref="ScanService"/>.
/// </summary>
public sealed record EcosystemLocation(
    string EcosystemId,
    string Id,
    string Kind,
    ResolveMode Mode,
    Ports.FingerprintSpec Fingerprint,
    Risk Tier,
    string Owner,
    bool InstallerOwned,
    string? AllowlistPattern,
    double ConfidencePenalty,
    IReadOnlyList<LocationSource> Sources,
    IReadOnlyDictionary<OperatingSystemKind, string> DefaultPaths,
    IReadOnlyList<OperatingSystemKind> SupportedOperatingSystems)
{
    public bool AppliesTo(OperatingSystemKind os) =>
        SupportedOperatingSystems.Contains(os);

    /// <summary>The root blueprint a scan receives once this location resolves to a valid root.</summary>
    public Ports.ScanRoot ToScanRoot(string resolvedPath, Ports.RootRole role) =>
        new(Id, resolvedPath, Kind, role, Fingerprint, Owner, ScanWiring.ToPortRisk(Tier));
}

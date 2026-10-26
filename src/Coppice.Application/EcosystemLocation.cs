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
/// <remarks>
/// <c>Inactive</c> is true when the source names a location that is known but not in use — the folder
/// left behind after a cache was moved. The engine assigns it <see cref="RootRole.Inactive"/>, reports
/// it, and never lets it become a plan step (LR-5). No shipped profile sets this yet; it is descriptor
/// data so the inactive case travels the same resolution path instead of a test-only stub.
/// </remarks>
public sealed record LocationSource(
    ResolvedVia Via,
    string? Run = null,
    ManifestParserKind? Parse = null,
    string? Label = null,
    string? Variable = null,
    string? PathTemplate = null,
    bool PathIsSdkInstall = false,
    // True when the source names a known-but-unused location. Descriptor data, not a per-ecosystem
    // switch: the resolver already understands IsLive, and this is the only place the chain sets it.
    bool Inactive = false);

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

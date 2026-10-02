using System.Collections.Generic;

namespace Coppice.Ports;

/// <summary>Risk tier (FR-09, 06-safety-model). Default policy admits <see cref="Safe"/> only.</summary>
public enum Risk
{
    Safe = 0,
    Review = 1,
    Manual = 2,
}

/// <summary>Reference resolution is three-state and deliberately has no "probably unreferenced" (FR-07).</summary>
public enum Usage
{
    Referenced = 0,
    Unreferenced = 1,
    Unknown = 2,
}

/// <summary>How a location root is in use (05-location-resolution).</summary>
public enum RootRole
{
    Active = 0,
    Additional = 1,
    Inactive = 2,
}

/// <summary>Why a root is or is not cleanable. Every non-Ok value must be reported, never silently cleaned (FR-03).</summary>
public enum RootValidity
{
    Ok = 0,
    NotFound = 1,
    FailsFingerprint = 2,
    Denied = 3,
    Ambiguous = 4,
    NeedsElevation = 5,
}

/// <summary>Severity of a health problem.</summary>
public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Removal action kind.</summary>
public enum RemovalKind
{
    NativeCommand = 0,
    PathDelete = 1,
    ReportOnly = 2,
}

/// <summary>What a valid root must look like (05, 09-ecosystems).</summary>
public sealed record FingerprintSpec
{
    /// <summary>Entry subdirectories that must all be present, e.g. "host/fxr" and "sdk".</summary>
    public IReadOnlyList<string> RequiredPaths { get; init; } = [];

    /// <summary>
    /// Fraction of entries that must match <see cref="EntryPatterns"/>. Zero means NO ratio check,
    /// which is the correct default for a location whose layout the profile does not specify.
    /// </summary>
    public double LayoutRatio { get; init; } = 0.0;

    /// <summary>Patterns (glob-style) an entry name must match, e.g. "*/version" for NuGet packages.</summary>
    public IReadOnlyList<string> EntryPatterns { get; init; } = [];
}

/// <summary>Context passed to plugin capabilities during a scan.</summary>
public sealed record ScanContext(
    IReadOnlyList<ScanRoot> Roots,
    ProjectSet Projects,
    IFileSystem FileSystem,
    IProcessRunner ProcessRunner,
    IEnvironment Environment,
    IClock Clock,
    CancellationToken CancellationToken = default)
{
    public ScanContext WithCancellation(CancellationToken ct) => this with { CancellationToken = ct };
}

/// <summary>A discovered package/tool/SDK item with its metadata (portable shape for plugin boundary).</summary>
public sealed record PortableItem(
    string ItemId,
    string Ecosystem,
    string Kind,
    string Name,
    string Version,
    string RootPath,
    Risk Risk,
    IReadOnlyDictionary<string, string> Facts);

/// <summary>The result of discovery, in the shape reference resolvers consume.</summary>
public sealed record ProjectSet(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<string> Ecosystems,
    IReadOnlyList<PortableScanIssue> Issues)
{
    public static ProjectSet Empty { get; } = new([], [], []);

    public bool IsEmpty => Projects.Count == 0;

    public IReadOnlyList<Project> For(string ecosystem) =>
        [.. Projects.Where(p => p.Ecosystems.Contains(ecosystem, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.RootPath, StringComparer.Ordinal)];
}

/// <summary>One discovered project and the markers that identified it (FR-06).</summary>
public sealed record Project(
    string RootPath,
    IReadOnlyList<string> MarkerIds,
    IReadOnlyList<string> Ecosystems)
{
    public IReadOnlyList<string> MarkerFiles { get; init; } = [];
}

/// <summary>An issue encountered during scanning (permission, I/O, malformed data). Portable shape for plugin boundary.</summary>
public sealed record PortableScanIssue(
    string Code,
    string Message,
    string? Path = null);

/// <summary>The three-state answer for one cached package (FR-07).</summary>
public sealed record ReferenceVerdict
{
    public required Usage Usage { get; init; }

    public IReadOnlyList<string> ReferencingProjects { get; init; } = [];

    public required string Reason { get; init; }
}

/// <summary>How the user wants things treated. Default policy admits <see cref="Risk.Safe"/> only.</summary>
public sealed record Policy
{
    public Risk MinimumRisk { get; init; } = Risk.Review;
    public Risk MaximumRisk { get; init; } = Risk.Safe;
    public int KeepLatestN { get; init; }
    public bool ProtectReferenced { get; init; } = true;
    public bool ProtectUnknownUsage { get; init; } = true;
    public IReadOnlyList<string> Exclusions { get; init; } = [];
    public required string PresetName { get; init; }
}

/// <summary>A removal action for an item.</summary>
public sealed record RemovalAction
{
    public required RemovalKind Kind { get; init; }
    public string? CommandLine { get; init; }
    public string? CanonicalPath { get; init; }
    public string? RouteNote { get; init; }
}

/// <summary>
/// A problem found by a health checker (FR-10).
/// <para>
/// Read-only by contract (C-9): a problem is an observation, never an instruction. A checker that
/// could delete as it reported would make "health check" and "cleanup" the same button.
/// </para>
/// </summary>
public sealed record Problem(
    string Code,
    Severity Severity,
    string Path,
    string Summary)
{
    /// <summary>Which location the problem is about, when it belongs to one.</summary>
    public string? LocationId { get; init; }

    /// <summary>Machine-readable detail. Values are primitives (C-12) and keys are ASCII identifiers.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        ReadOnlyDictionaryExtensions.Empty;
}

/// <summary>Identifies a scannable root with its profile and resolved path.</summary>
public sealed record ScanRoot(
    string LocationId,
    string ResolvedPath,
    string Kind,
    RootRole Role,
    FingerprintSpec Fingerprint,
    string Owner,
    Risk Tier);

/// <summary>
/// An ecosystem plugin contributes knowledge only — never scans, deletes, or renders.
/// </summary>
public interface IEcosystem
{
    /// <summary>Stable identifier (e.g., "dotnet", "go", "cargo", "npm").</summary>
    string Id { get; }

    /// <summary>Quick presence check used to decide whether to activate this ecosystem.</summary>
    bool IsPresent(ScanContext ctx);

    /// <summary>Plugin-owned version ordering; must be a total order (C-6).</summary>
    IVersionOrdering Versions { get; }
}

/// <summary>Total ordering for versions; property-tested for consistency (C-6).</summary>
public interface IVersionOrdering : IComparer<string>
{
    /// <summary>Parse a version string into comparable components; returns null if unrecognized.</summary>
    VersionComponents? TryParse(string version);

    /// <summary>Compare two version strings; throws if either is unrecognized.</summary>
    new int Compare(string left, string right);
}

/// <summary>Parsed version components for ordering logic.</summary>
public readonly record struct VersionComponents(
    int Major,
    int Minor,
    int Patch,
    int Build,
    string? Prerelease,
    string? Metadata);

/// <summary>Discovers inventory items (packages, SDKs, tools, runtimes) within the given roots.</summary>
public interface IInventoryProvider
{
    /// <summary>Enumerates items found in the scan roots. Read-only via ports.</summary>
    IAsyncEnumerable<PortableItem> Discover(ScanContext ctx);
}

/// <summary>Resolves the usage of an item given the set of discovered projects.</summary>
public interface IReferenceResolver
{
    /// <summary>Classifies an item as Referenced, Unreferenced, or Unknown with a reason.</summary>
    ReferenceVerdict Resolve(string itemName, string itemVersion, ProjectSet projects);
}

/// <summary>Plans the removal action for an item (v0.2+).</summary>
public interface IRemovalStrategy
{
    /// <summary>Returns the removal action for the item; null = not supported.</summary>
    RemovalAction? Plan(PortableItem item);
}

/// <summary>Performs read-only health checks on the ecosystem's locations.</summary>
public interface IHealthChecker
{
    /// <summary>Returns problems found in the scan context.</summary>
    Task<IReadOnlyList<Problem>> CheckAsync(ScanContext ctx);
}

/// <summary>Capability bundle — plugins implement the interfaces they support.</summary>
public interface IPluginCapabilities
{
    IInventoryProvider? Inventory { get; }
    IReferenceResolver? References { get; }
    IRemovalStrategy? Removal { get; }
    IHealthChecker? Health { get; }
}

/// <summary>A plugin is an ecosystem + its optional capabilities.</summary>
public interface IPlugin : IEcosystem, IPluginCapabilities
{
}

using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Plugins.Net;

/// <summary>
/// Who installed something, which decides how it is removed (05). Anything not owned by
/// <see cref="User"/> is report-only in v1 (ADR-012): the tool will not delete an MSI's files.
/// </summary>
public enum InstallOwner
{
    /// <summary>Unknown owner — treated conservatively as not-user.</summary>
    Unknown = 0,

    /// <summary>Installed by the user (a script, a tarball, dotnet-install). Gateway-removable.</summary>
    User = 1,

    /// <summary>Windows Installer / Visual Studio Installer.</summary>
    Msi = 2,

    /// <summary>Debian/Ubuntu package.</summary>
    Apt = 3,

    /// <summary>Fedora/RHEL package.</summary>
    Dnf = 4,

    /// <summary>Homebrew.</summary>
    Brew = 5,

    /// <summary>Snap.</summary>
    Snap = 6,

    /// <summary>Windows Package Manager.</summary>
    Winget = 7,
}

/// <summary>The kind of thing a location holds, which determines what may be done with it.</summary>
public enum LocationKind
{
    PackageCache = 0,
    HttpCache = 1,
    Temp = 2,
    Toolchain = 3,
    GlobalTools = 4,
    WorkloadPacks = 5,
    InstallerCache = 6,
}

/// <summary>How a whole-cache removal must be performed for a location.</summary>
public sealed record RemovalStrategy
{
    /// <summary>The native command that clears the cache, e.g. <c>dotnet nuget locals --clear</c>.</summary>
    public string? NativeCommand { get; init; }

    /// <summary>True when individual entries may be removed through the gateway.</summary>
    public bool SupportsSelectiveRemoval { get; init; }

    /// <summary>True when the tool refuses to remove anything and only explains where to look.</summary>
    public bool ReportOnly { get; init; }
}

/// <summary>
/// One row of the location table from 09-ecosystems. This is DATA, not logic: adding a .NET
/// location, changing a default, or adjusting a tier is an edit to this record, never to the kernel
/// (OCP, 04-plugin-contract).
/// </summary>
public sealed record NetLocation
{
    public required string Id { get; init; }

    public required LocationKind Kind { get; init; }

    /// <summary>Environment variables consulted for this location, in order.</summary>
    public IReadOnlyList<string> EnvVariables { get; init; } = [];

    /// <summary>
    /// The tool query that answers for this location, in the spec's own <c>label-value</c> form,
    /// e.g. <c>dotnet nuget locals global-packages --list</c>.
    /// </summary>
    public string? ToolQuery { get; init; }

    /// <summary>Per-OS default paths. Some locations differ three ways — see the NuGet temp row.</summary>
    public IReadOnlyDictionary<OperatingSystemKind, string> OsDefaults { get; init; } =
        new Dictionary<OperatingSystemKind, string>();

    /// <summary>Substrings the default must contain for this OS, used to expand <c>~</c> and vars.</summary>
    public required FingerprintSpec Fingerprint { get; init; }

    public required InstallOwner Owner { get; init; }

    /// <summary>The risk tier a default-policy scan may act on. Never <see cref="Risk.Manual"/>.</summary>
    public required Risk Tier { get; init; }

    public required RemovalStrategy Removal { get; init; }

    /// <summary>Multi-root locations collect every distinct root (SDK roots, fallback folders).</summary>
    public bool ResolveAll { get; init; }

    /// <summary>Only present on some OSes (the VS installer cache is Windows-only).</summary>
    public IReadOnlyList<OperatingSystemKind> SupportedOperatingSystems { get; init; } =
        [OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS];

    public bool AppliesTo(OperatingSystemKind os) => SupportedOperatingSystems.Contains(os);
}

/// <summary>What a valid .NET root must look like (05).</summary>
public sealed record FingerprintSpec
{
    /// <summary>Entry subdirectories that must all be present, e.g. <c>host/fxr</c> and <c>sdk</c>.</summary>
    public IReadOnlyList<string> RequiredPaths { get; init; } = [];

    /// <summary>The default layout ratio when a ratio check applies.</summary>
    public double LayoutRatio { get; init; } = 0.8;

    public bool IsEmpty => RequiredPaths.Count == 0 && LayoutRatio <= 0;
}

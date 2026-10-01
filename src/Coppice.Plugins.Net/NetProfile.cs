using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Plugins.Net;

/// <summary>
/// The .NET location table from SDD/09-ecosystems, as data.
/// <para>
/// This is the whole of the .NET profile's location knowledge. Everything the kernel knows about
/// .NET lives in this table, which is what lets a location, a default, or a tier change without a
/// core edit (OCP, 04-plugin-contract). Where the spec's table and this one disagree, the spec wins
/// and this file is wrong.
/// </para>
/// <para>
/// Placeholders: <c>~</c> is the user home, <c>%temp%</c> the OS temp directory, and
/// <c>{user}</c> the OS user name. They are left UNEXPANDED here and resolved at scan time by
/// <see cref="NetDefaults"/>, because a table baked against one machine's environment is a table
/// that lies on the next one.
/// </para>
/// </summary>
public static class NetProfile
{
    public const string EcosystemId = "dotnet";

    /// <summary>Every location, in the spec's table order.</summary>
    public static IReadOnlyList<NetLocation> Locations { get; } =
    [
        new NetLocation
        {
            Id = "nuget-packages",
            Kind = LocationKind.PackageCache,
            ToolQuery = "dotnet nuget locals global-packages --list",
            EnvVariables = ["NUGET_PACKAGES"],
            // `~/.nuget/packages` on every OS — the one default that does NOT diverge.
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "~\\.nuget\\packages",
                [OperatingSystemKind.Linux] = "~/.nuget/packages",
                [OperatingSystemKind.MacOS] = "~/.nuget/packages",
            },
            Fingerprint = new FingerprintSpec { LayoutRatio = 0.8, EntryPatterns = ["*/version"] },
            Owner = InstallOwner.User,
            Tier = Risk.Safe,
            Removal = new RemovalStrategy
            {
                SupportsSelectiveRemoval = true,
                NativeCommand = "dotnet nuget locals global-packages --clear",
            },
        },

        new NetLocation
        {
            Id = "nuget-http-cache",
            Kind = LocationKind.HttpCache,
            ToolQuery = "dotnet nuget locals http-cache --list",
            EnvVariables = ["NUGET_HTTP_CACHE_PATH"],
            // Diverges: %localappdata% on Windows, XDG data on the Unixes.
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "%LOCALAPPDATA%\\NuGet\\v3-cache",
                [OperatingSystemKind.Linux] = "~/.local/share/NuGet/v3-cache",
                [OperatingSystemKind.MacOS] = "~/.local/share/NuGet/v3-cache",
            },
            // 09 grants gateway removal here, but specifies the layout only in prose ("v3-cache
            // layout"). 10-formats requires a fingerprint for any location with delete rights, and
            // a delete right we cannot validate is a delete right we must not exercise. Shipped
            // report-only until T-013 supplies a layout we can actually check.
            Fingerprint = new FingerprintSpec(),
            Owner = InstallOwner.User,
            Tier = Risk.Safe,
            Removal = new RemovalStrategy { SupportsSelectiveRemoval = false, ReportOnly = true },
        },

        new NetLocation
        {
            Id = "nuget-temp",
            Kind = LocationKind.Temp,
            ToolQuery = "dotnet nuget locals temp --list",
            // The three-way divergence the spec calls out: the Windows form has no user name and
            // the Linux one appends it, so a single "temp" default would be wrong on two of three
            // platforms. The {user} token is what makes that expressible.
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "%TEMP%\\NuGetScratch",
                [OperatingSystemKind.MacOS] = "/tmp/NuGetScratch",
                [OperatingSystemKind.Linux] = "/tmp/NuGetScratch{user}",
            },
            // Same reasoning as nuget-http-cache: "scratch" is prose, not a checkable layout.
            Fingerprint = new FingerprintSpec(),
            Owner = InstallOwner.User,
            Tier = Risk.Safe,
            Removal = new RemovalStrategy { SupportsSelectiveRemoval = false, ReportOnly = true },
        },

        new NetLocation
        {
            Id = "dotnet-root",
            Kind = LocationKind.Toolchain,
            EnvVariables = ["DOTNET_ROOT"],
            ToolQuery = "dotnet --list-sdks",
            // The system-wide install locations differ per OS and per install owner; the per-user
            // one does not.
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "C:\\Program Files\\dotnet",
                [OperatingSystemKind.Linux] = "~/.dotnet",
                [OperatingSystemKind.MacOS] = "~/.dotnet",
            },
            // A .NET root without both markers is a directory that merely has those names in it.
            Fingerprint = new FingerprintSpec { RequiredPaths = ["host/fxr", "sdk"] },
            Owner = InstallOwner.Unknown,
            Tier = Risk.Review,
            ResolveAll = true,
            Removal = new RemovalStrategy { SupportsSelectiveRemoval = true },
        },

        new NetLocation
        {
            Id = "dotnet-tools",
            Kind = LocationKind.GlobalTools,
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "~\\.dotnet\\tools",
                [OperatingSystemKind.Linux] = "~/.dotnet/tools",
                [OperatingSystemKind.MacOS] = "~/.dotnet/tools",
            },
            Fingerprint = new FingerprintSpec(),
            Owner = InstallOwner.User,
            Tier = Risk.Review,
            // Global tools are removed through the tool, not by deleting a shim: the shim is
            // regenerated and a raw delete leaves `dotnet tool` thinking it is still installed.
            Removal = new RemovalStrategy
            {
                SupportsSelectiveRemoval = false,
                NativeCommand = "dotnet tool uninstall --global <id>",
            },
        },

        new NetLocation
        {
            Id = "dotnet-workloads",
            Kind = LocationKind.WorkloadPacks,
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "~\\.dotnet\\packs",
                [OperatingSystemKind.Linux] = "~/.dotnet/packs",
                [OperatingSystemKind.MacOS] = "~/.dotnet/packs",
            },
            Fingerprint = new FingerprintSpec(),
            Owner = InstallOwner.User,
            Tier = Risk.Review,
            Removal = new RemovalStrategy
            {
                SupportsSelectiveRemoval = false,
                NativeCommand = "dotnet workload clean",
            },
        },

        new NetLocation
        {
            Id = "vs-installer-cache",
            Kind = LocationKind.InstallerCache,
            OsDefaults = new Dictionary<OperatingSystemKind, string>
            {
                [OperatingSystemKind.Windows] = "C:\\ProgramData\\Microsoft\\VisualStudio\\Packages",
            },
            Fingerprint = new FingerprintSpec(),
            Owner = InstallOwner.Msi,
            Tier = Risk.Manual,
            // Installer-owned: the tool explains where the bytes are and stops there.
            Removal = new RemovalStrategy { ReportOnly = true, SupportsSelectiveRemoval = false },
            SupportedOperatingSystems = [OperatingSystemKind.Windows],
        },
    ];

    public static NetLocation? Find(string id) =>
        Locations.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));

    /// <summary>Locations that exist on a given OS. The VS installer cache is Windows-only.</summary>
    public static IReadOnlyList<NetLocation> ForOperatingSystem(OperatingSystemKind os) =>
        [.. Locations.Where(l => l.AppliesTo(os))];
}

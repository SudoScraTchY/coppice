using Coppice.Core.Domain;
using Coppice.Plugins.Net;
using Net = Coppice.Plugins.Net;

namespace Coppice.Cli;

/// <summary>
/// The one translation from a doctor report to the portable roots a scan reads. Shared by
/// <c>scan</c> and the TUI so the screen and the command cannot scan different sets of locations.
/// </summary>
public static class ScanWiring
{
    public static IReadOnlyList<Ports.ScanRoot> FromDoctor(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return
        [
            .. report.Roots
                .Where(r => r.Root.Validity == RootValidity.Ok && r.Root.Role != RootRole.Inactive)
                .Select(r => new Ports.ScanRoot(
                    r.LocationId,
                    r.Root.RealPath,
                    NetProfile.Find(r.LocationId)?.Kind.ToString() ?? "unknown",
                    ToPortRole(r.Root.Role),
                    FingerprintSpecFor(r.LocationId),
                    NetProfile.Find(r.LocationId)?.Owner.ToString() ?? "unknown",
                    ToPortRisk(NetProfile.Find(r.LocationId)?.Tier ?? Risk.Review))),
        ];
    }

    /// <summary>
    /// Core owns the domain enums; Ports declares its own so the plugin boundary stays free of
    /// Core types (NFR-08). These two switches are the whole translation cost, kept in one place
    /// so adding a member to either enum fails the build here rather than silently in a plugin.
    /// </summary>
    public static Ports.RootRole ToPortRole(RootRole role) => role switch
    {
        RootRole.Active => Ports.RootRole.Active,
        RootRole.Additional => Ports.RootRole.Additional,
        RootRole.Inactive => Ports.RootRole.Inactive,
        _ => Ports.RootRole.Inactive,
    };

    public static Ports.Risk ToPortRisk(Risk risk) => risk switch
    {
        Risk.Safe => Ports.Risk.Safe,
        Risk.Review => Ports.Risk.Review,
        Risk.Manual => Ports.Risk.Manual,
        _ => Ports.Risk.Manual,
    };

    /// <summary>
    /// The fingerprint a location id declares in the 09 table, in the portable shape plugins see.
    /// Unknown ids get an empty spec, which means "no layout gate" — never a reject.
    /// </summary>
    public static Ports.FingerprintSpec FingerprintSpecFor(string locationId)
    {
        Net.FingerprintSpec? fingerprint = NetProfile.Find(locationId)?.Fingerprint;
        return fingerprint is null
            ? new Ports.FingerprintSpec()
            : new Ports.FingerprintSpec
            {
                RequiredPaths = fingerprint.RequiredPaths,
                EntryPatterns = fingerprint.EntryPatterns,
                LayoutRatio = fingerprint.LayoutRatio,
            };
    }
}

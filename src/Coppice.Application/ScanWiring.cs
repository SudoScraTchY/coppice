using Coppice.Core.Domain;

namespace Coppice.Application;

/// <summary>
/// The one translation from a doctor report to the portable roots a scan reads. Shared by
/// <c>scan</c> and the TUI so the screen and the command cannot scan different sets of locations.
/// </summary>
public static class ScanWiring
{
    public static IReadOnlyList<Ports.ScanRoot> FromDoctor(DoctorReport report, EcosystemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(registry);

        return
        [
            .. report.Roots
                // The cleanability floor, applied twice on purpose: the domain says an Ok root must
                // also be in use, and this filter says it in the one place a plan is born. An
                // ambiguous, denied, missing or inactive candidate can therefore never become a
                // scan root — and a scan root is the only thing a plan step can bind to.
                .Where(r => r.Root.IsCleanable && r.Root.Role != RootRole.Inactive)
                .Select(r =>
                {
                    // Every location the registry knows carries its own kind, owner, tier and
                    // fingerprint. A location that is not registered is still reported by doctor,
                    // but it is deliberately scanned by nobody: unknown metadata must not be guessed.
                    EcosystemLocation? location = registry.FindLocation(r.LocationId)?.Location;

                    return new Ports.ScanRoot(
                        r.LocationId,
                        r.Root.RealPath,
                        location?.Kind ?? "unknown",
                        ToPortRole(r.Root.Role),
                        location?.Fingerprint ?? new Ports.FingerprintSpec(),
                        location?.Owner ?? "unknown",
                        ToPortRisk(location?.Tier ?? Risk.Review));
                }),
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
}

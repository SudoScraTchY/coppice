using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Core.Gateway;
using Coppice.Core.Quarantine;
using Coppice.Ports;
using GatewayEngine = Coppice.Core.Gateway.Gateway;

namespace Coppice.Application;

/// <summary>The single mutation wiring: one gateway, one audit stream, one journal.</summary>
public sealed record ApplyServices(
    QuarantineIndex Index,
    AuditLog Audit,
    GatewayEngine Gateway,
    ApplyRunner Runner);

/// <summary>The same index and audit stream, without the gateway apply needs. Quarantine verbs use this.</summary>
public sealed record QuarantineServices(
    QuarantineIndex Index,
    AuditLog Audit,
    QuarantineRestore Restore);

/// <summary>
/// Builds the objects <c>apply</c> and the TUI both mutate through, and maps live roots into the shape the
/// binder accepts (T-034, S-1).
/// <para>
/// This exists so there is exactly ONE place that decides which roots a plan may bind to and which audit
/// stream records the result. A TUI that assembled its own gateway could bind a plan to a root the
/// command would have refused, and the two frontends would disagree about what S-1 means.
/// </para>
/// </summary>
public static class ApplyWiring
{
    /// <summary>
    /// The entry shapes a location's cache may have, for the gateway's allowlist. The catalog declares
    /// these next to the location they describe (T-048), so a manifest-backed ecosystem's path deletes
    /// are validated by exactly the same gate as NuGet's.
    /// </summary>
    public static ApplyServices Create(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        IStateStore state,
        IAppendLog log,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        QuarantineServices quarantine = CreateQuarantine(fs, env, state, log, clock);
        var gateway = new GatewayEngine(fs, env.OS, quarantine.Index, clock, runner, quarantine.Audit);
        var apply = new ApplyRunner(gateway, fs, log, quarantine.Index, clock, quarantine.Audit);

        return new ApplyServices(quarantine.Index, quarantine.Audit, gateway, apply);
    }

    /// <summary>
    /// The quarantine half of the wiring. Built from the same state and append log as <see cref="Create"/>
    /// so a restore or prune is recorded in the same audit stream as the quarantines it undoes.
    /// </summary>
    public static QuarantineServices CreateQuarantine(
        IFileSystem fs,
        IEnvironment env,
        IStateStore state,
        IAppendLog log,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        var index = new QuarantineIndex(state);
        var audit = new AuditLog(log, clock);
        var restore = new QuarantineRestore(fs, env.OS, index, audit, clock);

        return new QuarantineServices(index, audit, restore);
    }

    public static IReadOnlyList<BindingRoot> BindingRoots(DoctorReport report, EcosystemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(registry);

        var roots = new List<BindingRoot>();

        foreach (RootReport root in report.Roots)
        {
            EcosystemLocation? location = registry.FindLocation(root.LocationId)?.Location;

            roots.Add(new BindingRoot(
                root.LocationId,
                root.Root.RealPath,
                root.Root.IsCleanable,
                location?.InstallerOwned ?? false,
                location?.AllowlistPattern));
        }

        return roots;
    }
}

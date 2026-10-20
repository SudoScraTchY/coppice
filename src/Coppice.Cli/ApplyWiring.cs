using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Core.Gateway;
using Coppice.Core.Quarantine;
using Coppice.Plugins.Net;
using Coppice.Ports;
using GatewayEngine = Coppice.Core.Gateway.Gateway;

namespace Coppice.Cli;

/// <summary>The single mutation wiring: one gateway, one audit stream, one journal.</summary>
public sealed record ApplyServices(
    QuarantineIndex Index,
    AuditLog Audit,
    GatewayEngine Gateway,
    ApplyRunner Runner);

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
    /// The entry shapes a location's cache may have, for the gateway's allowlist. Only the NuGet
    /// global-packages cache declares one: it is the only location whose contents are re-downloadable, which
    /// is what makes it the only tier a default policy may act on. A location with no pattern here binds no
    /// path delete at all.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Allowlists =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["nuget-packages"] = "*/*" };

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

        var index = new QuarantineIndex(state);
        var audit = new AuditLog(log, clock);
        var gateway = new GatewayEngine(fs, env.OS, index, clock, runner, audit);
        var apply = new ApplyRunner(gateway, fs, log, index, clock, audit);

        return new ApplyServices(index, audit, gateway, apply);
    }

    public static IReadOnlyList<BindingRoot> BindingRoots(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var roots = new List<BindingRoot>();

        foreach (RootReport root in report.Roots)
        {
            NetLocation? location = NetProfile.Find(root.LocationId);

            roots.Add(new BindingRoot(
                root.LocationId,
                root.Root.RealPath,
                root.Root.IsCleanable,
                location?.Owner == InstallOwner.Msi,
                Allowlists.GetValueOrDefault(root.LocationId)));
        }

        return roots;
    }
}

using Coppice.Adapters;
using Coppice.Application;
using Coppice.Core.Quarantine;
using Coppice.Ports;

namespace Coppice.Gui.Services;

/// <summary>
/// The GUI's composition root. It wires the same adapters and application services the CLI uses;
/// nothing here decides policy, binds a plan, or moves a file.
/// <para>
/// <see cref="Create"/> is the production wiring. The public constructor takes the six application
/// services directly, so a headless test can hand the view model a fake-backed graph without the GUI
/// referencing an adapter or growing a test-only branch.
/// </para>
/// </summary>
public sealed class GuiServices : IDisposable
{
    public GuiServices(
        EcosystemRegistry registry,
        DoctorService doctor,
        ScanService scan,
        SnapshotLibrary snapshots,
        IPlanApplyService apply,
        QuarantineLibrary quarantine)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(doctor);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(quarantine);

        Registry = registry;
        Doctor = doctor;
        Scan = scan;
        Snapshots = snapshots;
        Apply = apply;
        Quarantine = quarantine;
    }

    public EcosystemRegistry Registry { get; }

    public DoctorService Doctor { get; }

    public ScanService Scan { get; }

    public SnapshotLibrary Snapshots { get; }

    public IPlanApplyService Apply { get; }

    public QuarantineLibrary Quarantine { get; }

    public static GuiServices Create()
    {
        var fs = new PhysicalFileSystem();
        var env = new SystemEnvironment();
        var runner = new SystemProcessRunner();
        var state = new FileSystemStateStore(fs, env);
        var log = new FileAppendLog(env);
        var clock = new SystemClock();

        QuarantineServices quarantine = ApplyWiring.CreateQuarantine(fs, env, state, log, clock);
        EcosystemRegistry registry = EcosystemRegistry.CreateDefault(fs);

        return new GuiServices(
            registry,
            new DoctorService(fs, runner, env, env.OS, registry),
            new ScanService(fs, runner, env, state, clock, registry),
            new SnapshotLibrary(state, clock),
            new PlanApplyService(fs, runner, env, state, log, clock, registry),
            new QuarantineLibrary(fs, quarantine.Index, quarantine.Restore));
    }

    /// <summary>
    /// The production preferences store. It is separate from <see cref="Create"/> because the window
    /// reads its theme before it builds the service graph, and the store owns no service state.
    /// </summary>
    public static IShellPreferencesStore CreatePreferences() =>
        new JsonFileShellPreferencesStore(new SystemEnvironment());

    public void Dispose()
    {
        // The adapters own no unmanaged state; this hook exists so the composition root has a
        // shutdown seam if one ever does.
    }
}

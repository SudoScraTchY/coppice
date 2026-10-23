using Coppice.Adapters;
using Coppice.Application;
using Coppice.Core.Quarantine;

namespace Coppice.Gui.Services;

/// <summary>
/// The GUI's composition root. It wires the same adapters and application services the CLI uses;
/// nothing here decides policy, binds a plan, or moves a file.
/// </summary>
public sealed class GuiServices : IDisposable
{
    private GuiServices(
        DoctorService doctor,
        ScanService scan,
        SnapshotLibrary snapshots,
        IPlanApplyService apply,
        QuarantineLibrary quarantine)
    {
        Doctor = doctor;
        Scan = scan;
        Snapshots = snapshots;
        Apply = apply;
        Quarantine = quarantine;
    }

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

        return new GuiServices(
            new DoctorService(fs, runner, env),
            new ScanService(fs, runner, env, state, clock),
            new SnapshotLibrary(state, clock),
            new PlanApplyService(fs, runner, env, state, log, clock),
            new QuarantineLibrary(fs, quarantine.Index, quarantine.Restore));
    }

    public void Dispose()
    {
        // The adapters own no unmanaged state; this hook exists so the composition root has a
        // shutdown seam if one ever does.
    }
}

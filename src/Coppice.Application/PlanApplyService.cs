using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Snapshots;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>What an apply attempt did. A refusal to PREPARE is distinct from a run that completed with failures.</summary>
public sealed record PlanApplyOutcome(
    bool Prepared,
    string Message,
    Snapshot? Snapshot,
    IReadOnlyList<BoundStep> Bound,
    ApplySummary? Summary)
{
    public bool Ran => Summary is { Ran: true };
}

/// <summary>
/// The apply path shared by a front-end that has already decided to run a plan: load the snapshot,
/// resolve live roots, bind, then run through the gateway and journal.
/// <para>
/// The GUI uses this instead of growing its own binder. The CLI keeps its confirmation and plan-file
/// policy in the command; everything after "yes" is the same sequence here.
/// </para>
/// </summary>
public interface IPlanApplyService
{
    Task<PlanApplyOutcome> ApplyAsync(
        Plan plan,
        bool strict,
        bool resume,
        bool confirmed,
        IProgress<ApplyStepProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class PlanApplyService : IPlanApplyService
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly IStateStore _state;
    private readonly IAppendLog _log;
    private readonly IClock _clock;
    private readonly EcosystemRegistry _registry;

    public PlanApplyService(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        IStateStore state,
        IAppendLog log,
        IClock clock,
        EcosystemRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _fs = fs;
        _runner = runner;
        _env = env;
        _state = state;
        _log = log;
        _clock = clock;
        _registry = registry ?? EcosystemRegistry.CreateDefault(fs);
    }

    public async Task<PlanApplyOutcome> ApplyAsync(
        Plan plan,
        bool strict,
        bool resume,
        bool confirmed,
        IProgress<ApplyStepProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        SnapshotLoadResult loaded = await new SnapshotStore(_state, _clock)
            .LoadAsync(plan.SnapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (!loaded.Ok)
        {
            return new PlanApplyOutcome(
                false,
                $"The plan's snapshot could not be loaded: {loaded.Message}",
                null,
                [],
                null);
        }

        Snapshot snapshot = loaded.Snapshot!;

        DoctorReport roots = new DoctorService(_fs, _runner, _env, _env.OS, _registry).Inspect();
        IReadOnlyList<BoundStep> bound = PlanBinder.Bind(
            plan, snapshot, ApplyWiring.BindingRoots(roots, _registry), _env.OS, strict);

        ApplyServices services = ApplyWiring.Create(_fs, _runner, _env, _state, _log, _clock);
        ApplySummary summary = await services.Runner
            .RunAsync(plan, bound, new ApplyOptions(strict, resume, confirmed), progress, cancellationToken)
            .ConfigureAwait(false);

        return new PlanApplyOutcome(true, summary.Message, snapshot, bound, summary);
    }
}

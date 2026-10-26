using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Ports;

namespace Coppice.Cli.Tui;

/// <summary>A plan saved and bound to the live roots the session will act under, or why it was not.</summary>
public sealed record ApplyPreparation(Plan? Plan, IReadOnlyList<BoundStep> Bound, string? Error);

/// <summary>
/// What the TUI needs from the machine. An interface so the state machine and the loop can be tested
/// without a terminal, and so no screen can reach a service directly.
/// </summary>
public interface ITuiBackend
{
    Task<TuiData> LoadAsync();

    Task<TuiData> ScanAsync();

    Task<ApplyPreparation> PrepareApplyAsync(Plan plan);

    Task<ApplySummary> ApplyAsync(
        Plan plan,
        IReadOnlyList<BoundStep> bound,
        IProgress<ApplyStepProgress> progress,
        CancellationToken cancellationToken);

    Task<RestoreResult> RestoreAsync(string entryId);
}

/// <summary>
/// The production backend: the same doctor, scanner, plan store, binder, apply wiring and restore the
/// commands use. It holds no policy of its own — every safety decision is made by the service it calls.
/// </summary>
public sealed class TuiBackend : ITuiBackend
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly IStateStore _state;
    private readonly IAppendLog _log;
    private readonly IClock _clock;
    private readonly IReadOnlyList<string> _projectRoots;
    private readonly EcosystemRegistry _registry;

    public TuiBackend(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        IStateStore state,
        IAppendLog log,
        IClock clock,
        IReadOnlyList<string>? projectRoots = null)
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
        _projectRoots = projectRoots ?? [];
        _registry = EcosystemRegistry.CreateDefault(fs);
    }

    public async Task<TuiData> LoadAsync()
    {
        DoctorReport roots = new DoctorService(_fs, _runner, _env, _env.OS, _registry).Inspect();
        SnapshotLoadResult latest = await new SnapshotStore(_state, _clock).LoadLatestAsync().ConfigureAwait(false);
        IReadOnlyList<QuarantineEntry> quarantine = await new QuarantineIndex(_state).ListAsync().ConfigureAwait(false);

        Snapshot? snapshot = latest.Ok ? latest.Snapshot : null;

        return new TuiData(
            roots,
            snapshot,
            BuildProblems(roots, snapshot, latest),
            quarantine,
            _env.HomeDirectory,
            ConfigPathFor(_env));
    }

    public async Task<TuiData> ScanAsync()
    {
        // Resolve first so the scan reads exactly the roots `coppice scan` would, then use the same service.
        DoctorReport roots = new DoctorService(_fs, _runner, _env, _env.OS, _registry).Inspect();
        var scan = new ScanService(_fs, _runner, _env, _state, _clock, _registry);
        await scan.RunAsync(ScanWiring.FromDoctor(roots, _registry), _projectRoots).ConfigureAwait(false);

        return await LoadAsync().ConfigureAwait(false);
    }

    public async Task<ApplyPreparation> PrepareApplyAsync(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        SnapshotLoadResult snapshot = await new SnapshotStore(_state, _clock)
            .LoadAsync(plan.SnapshotId)
            .ConfigureAwait(false);

        if (!snapshot.Ok)
        {
            return new ApplyPreparation(null, [], $"The plan's snapshot could not be loaded: {snapshot.Message}");
        }

        DoctorReport roots = new DoctorService(_fs, _runner, _env, _env.OS, _registry).Inspect();
        IReadOnlyList<BoundStep> bound = PlanBinder.Bind(
            plan,
            snapshot.Snapshot!,
            ApplyWiring.BindingRoots(roots, _registry),
            _env.OS,
            strict: false);

        await new PlanStore(_state).SaveAsync(plan).ConfigureAwait(false);

        return new ApplyPreparation(plan, bound, null);
    }

    public Task<ApplySummary> ApplyAsync(
        Plan plan,
        IReadOnlyList<BoundStep> bound,
        IProgress<ApplyStepProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(progress);

        ApplyServices services = ApplyWiring.Create(_fs, _runner, _env, _state, _log, _clock);

        // The plan review's second `a` is the confirmation; `Confirmed` is what the native branch needs.
        return services.Runner.RunAsync(
            plan,
            bound,
            new ApplyOptions(Strict: false, Resume: false, Confirmed: true),
            progress,
            cancellationToken);
    }

    public Task<RestoreResult> RestoreAsync(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        // The same index + audit wiring as the CLI's quarantine verb, so a TUI restore and a CLI restore
        // produce the same record in the same stream.
        return ApplyWiring.CreateQuarantine(_fs, _env, _state, _log, _clock).Restore.RestoreAsync(entryId);
    }

    /// <summary>
    /// Findings the TUI can show without inventing a health check: the roots that did not validate, the
    /// resolution issues, and whatever the snapshot recorded. The snapshot is evidence from a past scan —
    /// the screen says so rather than implying it is live.
    /// </summary>
    private static IReadOnlyList<TuiProblem> BuildProblems(
        DoctorReport roots,
        Snapshot? snapshot,
        SnapshotLoadResult latest)
    {
        var problems = new List<TuiProblem>();

        foreach (RootReport root in roots.Roots)
        {
            if (root.Root.Validity == RootValidity.Ok)
            {
                continue;
            }

            string severity = root.Root.Validity is RootValidity.Ambiguous or RootValidity.Denied ? "error" : "warning";
            problems.Add(new TuiProblem(
                severity,
                "roots",
                TextReport.Validity(root.Root.Validity),
                $"{root.LocationId}: {TextReport.Validity(root.Root.Validity)}",
                root.Root.RealPath,
                string.IsNullOrEmpty(root.Problem) ? "This root will not be scanned or cleaned." : root.Problem));
        }

        foreach (string issue in roots.Issues)
        {
            problems.Add(new TuiProblem("info", "resolution", "resolution", issue, null, issue));
        }

        foreach (ScanIssue issue in snapshot?.Issues ?? [])
        {
            problems.Add(new TuiProblem(
                "warning",
                "snapshot",
                issue.Code,
                issue.Summary,
                issue.Path,
                $"From snapshot {snapshot!.SnapshotId}, location '{issue.LocationId}'. Run s to rescan."));
        }

        if (snapshot is null && latest.Failure is not SnapshotLoadFailure.NotFound)
        {
            problems.Add(new TuiProblem(
                "error",
                "snapshot",
                latest.Failure.ToString(),
                "The newest snapshot could not be loaded.",
                null,
                latest.Message));
        }

        return problems;
    }

    /// <summary>
    /// The config path from 10-formats. Displayed by the pin hint; the TUI never writes it.
    /// </summary>
    public static string ConfigPathFor(IEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);

        return UserConfigPath.For(env);
    }
}

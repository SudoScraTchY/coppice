using System.Text.Json;
using Coppice.Adapters;
using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Plugins.Net;
using Coppice.Ports;
using GatewayEngine = Coppice.Core.Gateway.Gateway;

namespace Coppice.Cli;

/// <summary>Exit codes from 07, in one place so a command cannot invent its own.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int RuntimeError = 1;
    public const int UsageError = 2;
    public const int SafetyAbort = 3;
    public const int PartialApply = 4;
}

/// <summary>
/// The console an apply talks to. Injectable because the confirmation gate is the difference between a tool
/// that asks and one that does not, and that gate has to be testable without a terminal.
/// </summary>
public interface IApplyConsole
{
    /// <summary>True only when a human can read the summary and answer it (07's non-TTY rule).</summary>
    bool IsInteractive { get; }

    void WriteLine(string line);

    /// <summary>The confirmation prompt. Separate from <see cref="WriteLine"/> so a test can assert it was asked.</summary>
    void WritePrompt(string text);

    void WriteError(string line);

    /// <summary>The confirmation answer, or null at end of input.</summary>
    string? ReadLine();
}

public sealed class SystemApplyConsole : IApplyConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public void WriteLine(string line) => Console.Out.WriteLine(line);

    public void WritePrompt(string text) => Console.Out.Write(text);

    public void WriteError(string line) => Console.Error.WriteLine(line);

    public string? ReadLine() => Console.In.ReadLine();
}

public sealed record ApplyFlags(string? PlanFile, bool Yes, bool Strict, bool Resume, bool Json)
{
    public static ApplyFlags Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? planFile = null;
        bool yes = false;
        bool strict = false;
        bool resume = false;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plan":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--plan needs a file path");
                    }

                    planFile = args[++i];
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--resume":
                    resume = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--no-color":
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        return new ApplyFlags(planFile, yes, strict, resume, json);
    }
}

/// <summary>
/// <c>coppice apply</c> — the one verb that changes the machine (T-034, FR-12, FR-16, S-1).
/// <para>
/// It owns three decisions and delegates the rest: WHICH plan runs (a file, or the newest in the state
/// store), WHETHER it may run (the confirmation gate), and what the run means as an exit code. Binding the
/// plan to live roots, the gateway's safety chain and the journal all live in Core, so this class contains no
/// path arithmetic of its own — a command that re-derived a root here would be a second source of truth for
/// the one question S-1 asks.
/// </para>
/// </summary>
public sealed class ApplyCommand
{
    /// <summary>
    /// The entry shapes a location's cache may have, for the gateway's allowlist. Only the NuGet
    /// global-packages cache declares one: it is the only location whose contents are re-downloadable, which
    /// is what makes it the only tier a default policy may act on. A location with no pattern here binds no
    /// path delete at all.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Allowlists =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["nuget-packages"] = "*/*" };

    /// <summary>A plan file is JSON and small; 8 MB is generous and keeps the read bounded.</summary>
    private const int PlanFileLimit = 8 * 1024 * 1024;

    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly IStateStore _state;
    private readonly IAppendLog _log;
    private readonly IApplyConsole _console;
    private readonly IClock _clock;

    public ApplyCommand(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        IStateStore state,
        IAppendLog log,
        IApplyConsole console,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(console);

        _fs = fs;
        _runner = runner;
        _env = env;
        _state = state;
        _log = log;
        _console = console;
        _clock = clock ?? new SystemClock();
    }

    public int Run(string[] args)
    {
        ApplyFlags flags;
        try
        {
            flags = ApplyFlags.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }

        // 07: in a non-TTY, apply without BOTH --yes and a plan file is a usage error. Checked before any
        // state is read and before a gateway exists, so a scripted run cannot reach a mutation by accident —
        // at this point there is nothing to reach.
        if (!_console.IsInteractive && (!flags.Yes || flags.PlanFile is null))
        {
            return Usage("apply needs --yes and --plan <file> when it cannot ask (stdin or stdout is redirected).");
        }

        try
        {
            return Execute(flags);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
    }

    private int Execute(ApplyFlags flags)
    {
        PlanDecision decision = LoadPlan(flags);
        if (decision.Plan is null)
        {
            _console.WriteError($"coppice: {decision.Message}");
            return decision.Exit;
        }

        Plan plan = decision.Plan;

        SnapshotStore snapshots = new(_state, _clock);
        SnapshotLoadResult snapshotResult = snapshots.LoadAsync(plan.SnapshotId).GetAwaiter().GetResult();

        if (!snapshotResult.Ok)
        {
            return Abort($"The plan's snapshot could not be loaded: {snapshotResult.Message}");
        }

        Snapshot snapshot = snapshotResult.Snapshot!;

        DoctorReport roots = new DoctorService(_fs, _runner, _env, _env.OS).Inspect();
        IReadOnlyList<BoundStep> bound = PlanBinder.Bind(plan, snapshot, BindingRoots(roots), _env.OS, flags.Strict);

        if (!flags.Yes && !Confirm(plan, snapshot, bound))
        {
            _console.WriteLine("Declined. Nothing was moved.");
            return ExitCodes.Success;
        }

        var index = new QuarantineIndex(_state);

        // One audit stream for the whole run: the gateway appends a line per mutation, restore and prune
        // append theirs, and `coppice audit verify` (T-035) later replays it against the index.
        var audit = new AuditLog(_log, _clock);
        var gateway = new GatewayEngine(_fs, _env.OS, index, _clock, _runner, audit);
        var runner = new ApplyRunner(gateway, _fs, _log, index, _clock, audit);

        ApplySummary summary = runner
            .RunAsync(plan, bound, new ApplyOptions(flags.Strict, flags.Resume, flags.Yes))
            .GetAwaiter()
            .GetResult();

        return Report(summary, flags);
    }

    /// <summary>
    /// The plan to run: the file named by <c>--plan</c>, or the newest plan in the state store. A plan read
    /// from a file goes through exactly the store's own validation, so a hand-edited plan is refused for the
    /// same reason a hand-edited stored plan is.
    /// </summary>
    private PlanDecision LoadPlan(ApplyFlags flags)
    {
        if (flags.PlanFile is { } path)
        {
            string json;
            try
            {
                json = _fs.ReadSmallText(path, PlanFileLimit);
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException or System.IO.IOException)
            {
                return PlanDecision.Refuse(ExitCodes.UsageError, $"no plan file at '{path}' ({ex.Message})");
            }

            PlanLoadResult parsed = PlanStore.Parse(json, $"Plan file '{path}'");
            return parsed.IsSuccess
                ? PlanDecision.Use(parsed.Plan!)
                : PlanDecision.Refuse(ExitCodes.SafetyAbort, parsed.Message ?? "The plan file was refused.");
        }

        var plans = new PlanStore(_state);
        string? newest = null;
        DateTimeOffset newestAt = DateTimeOffset.MinValue;

        foreach (string id in plans.ListIds())
        {
            StateDocument? document = _state.ReadAsync($"plans/{id}.json").GetAwaiter().GetResult();
            if (document is not null && document.UpdatedAtUtc > newestAt)
            {
                newestAt = document.UpdatedAtUtc;
                newest = id;
            }
        }

        if (newest is null)
        {
            return PlanDecision.Refuse(ExitCodes.UsageError, "no plan to apply. Pass --plan <file>, or store one first.");
        }

        PlanLoadResult loaded = plans.LoadAsync(newest).GetAwaiter().GetResult();
        return loaded.IsSuccess
            ? PlanDecision.Use(loaded.Plan!)
            : PlanDecision.Refuse(ExitCodes.SafetyAbort, loaded.Message ?? $"Plan '{newest}' was refused.");
    }

    private static IReadOnlyList<BindingRoot> BindingRoots(DoctorReport report)
    {
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

    /// <summary>
    /// 07's confirmation gate: the summary a human reads before agreeing, and one word that has to be typed
    /// in full. Anything else — including an empty line and end of input — is a decline, because the cost of
    /// a misread confirmation is a cache the user wanted.
    /// </summary>
    private bool Confirm(Plan plan, Snapshot snapshot, IReadOnlyList<BoundStep> bound)
    {
        _console.WriteLine($"coppice apply — {plan.PlanId}");
        _console.WriteLine(
            $"plan: {plan.Policy.PresetName} policy over snapshot {plan.SnapshotId} ({snapshot.Items.Count} item(s) scanned)");
        _console.WriteLine(string.Empty);

        var byTier = new Dictionary<Risk, ulong>();
        var byEcosystem = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (BoundStep step in bound)
        {
            byTier[step.PlanStep.Risk] = byTier.GetValueOrDefault(step.PlanStep.Risk) + step.PlanStep.ExpectedBytes;
            string ecosystem = step.Item?.Ecosystem ?? "unknown";
            byEcosystem[ecosystem] = byEcosystem.GetValueOrDefault(ecosystem) + 1;
        }

        _console.WriteLine("Bytes by tier:");
        foreach (Risk tier in byTier.Keys.Order())
        {
            _console.WriteLine($"  {tier,-8} {ReportTotals.FormatBytes(byTier[tier])}");
        }

        _console.WriteLine(string.Empty);
        _console.WriteLine("Steps by ecosystem:");
        foreach ((string ecosystem, int count) in byEcosystem.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            _console.WriteLine($"  {ecosystem,-16} {count}");
        }

        int removable = bound.Count(s => !s.IsReportOnly);
        _console.WriteLine(string.Empty);
        _console.WriteLine(
            $"{removable} step(s) would be removed, {ReportTotals.FormatBytes(plan.ExpectedReclaimBytes)} expected. "
            + "Path steps are MOVED to quarantine, not deleted, and can be restored.");

        _console.WritePrompt("Type 'yes' to continue: ");

        string? answer = _console.ReadLine();
        return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private int Report(ApplySummary summary, ApplyFlags flags)
    {
        if (summary.Status is not (ApplyStatus.Completed or ApplyStatus.Partial))
        {
            return summary.Status is ApplyStatus.AlreadyApplied or ApplyStatus.NothingToResume
                ? Usage(summary.Message)
                : Abort(summary.Message);
        }

        if (flags.Json)
        {
            _console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    status = summary.Status.ToString(),
                    ok = summary.Ok,
                    failed = summary.Failed,
                    skippedUnsafe = summary.SkippedUnsafe,
                    reported = summary.Reported,
                    reclaimedBytes = summary.ReclaimedBytes,
                    steps = summary.Steps.Select(s => new
                    {
                        stepId = s.StepId,
                        result = s.Result,
                        refusal = s.Refusal.ToString(),
                        bytes = s.Bytes,
                        message = s.Message,
                    }),
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            _console.WriteLine(
                $"{summary.Ok} steps ok · {summary.Failed} failed{FailureNote(summary)} · {summary.SkippedUnsafe} skipped-unsafe");

            _console.WriteLine(
                $"Harvested {ReportTotals.FormatBytes(summary.ReclaimedBytes)} — moved to quarantine and restorable. "
                + "The `coppice quarantine` verb is not in this build yet; entries live under the state root.");

            foreach (ApplyStepReport step in summary.Steps.Where(s => s.Class is ApplyStepClass.Failed or ApplyStepClass.SkippedUnsafe))
            {
                _console.WriteLine($"  ! {step.StepId}: {step.Message}");
            }
        }

        return summary.Status == ApplyStatus.Partial ? ExitCodes.PartialApply : ExitCodes.Success;
    }

    /// <summary>The dominant failure reason, so "2 failed (locked)" says something a user can act on.</summary>
    private static string FailureNote(ApplySummary summary)
    {
        IGrouping<string, ApplyStepReport>? top = summary.Steps
            .Where(s => s.Class == ApplyStepClass.Failed)
            .GroupBy(s => s.Refusal.ToString().ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        return top is null ? string.Empty : $" ({top.Key})";
    }

    private int Usage(string message)
    {
        _console.WriteError($"coppice: {message}");
        _console.WriteError("Run 'coppice help' for usage.");
        return ExitCodes.UsageError;
    }

    private int Abort(string message)
    {
        _console.WriteError($"coppice: {message}");
        return ExitCodes.SafetyAbort;
    }

    /// <summary>Either the plan to run, or the exit code and reason it could not be run.</summary>
    private sealed record PlanDecision(Plan? Plan, int Exit, string Message)
    {
        public static PlanDecision Use(Plan plan) => new(plan, ExitCodes.Success, string.Empty);

        public static PlanDecision Refuse(int exit, string message) => new(null, exit, message);
    }
}

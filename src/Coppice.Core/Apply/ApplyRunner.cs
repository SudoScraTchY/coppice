using System.Text.RegularExpressions;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Quarantine;
using Coppice.Ports;
using GatewayEngine = Coppice.Core.Gateway.Gateway;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Core.Apply;

public sealed record ApplyOptions(bool Strict, bool Resume, bool Confirmed);

public enum ApplyStatus
{
    /// <summary>Every step ended ok or report-only.</summary>
    Completed,

    /// <summary>The run finished, but a step failed or was refused as unsafe (exit 4).</summary>
    Partial,

    /// <summary>The journal says this plan already ran to the end. Plans are single-use.</summary>
    AlreadyApplied,

    /// <summary>A previous run of this plan stopped part-way; only <c>--resume</c> may continue it.</summary>
    Unfinished,

    /// <summary><c>--resume</c> was asked for, but this plan never started.</summary>
    NothingToResume,

    /// <summary>A journal line other than a torn tail is unreadable. Nothing was done.</summary>
    JournalCorrupt,

    /// <summary>The journal was written for a plan with a different checksum. Nothing was done.</summary>
    JournalMismatch,

    /// <summary>The plan id is not a safe state key. Nothing was done.</summary>
    InvalidPlanId,
}

public enum ApplyStepClass
{
    Ok,
    Failed,
    SkippedUnsafe,
    Reported,
}

/// <summary>What became of one step, as reported to the user and recorded as its journal outcome.</summary>
public sealed record ApplyStepReport(
    string StepId,
    string Result,
    GatewayRefusal Refusal,
    string Message,
    ulong Bytes,
    ApplyStepClass Class);

public sealed record ApplySummary(ApplyStatus Status, string Message, IReadOnlyList<ApplyStepReport> Steps)
{
    public int Ok => Steps.Count(s => s.Class == ApplyStepClass.Ok);

    public int Failed => Steps.Count(s => s.Class == ApplyStepClass.Failed);

    public int SkippedUnsafe => Steps.Count(s => s.Class == ApplyStepClass.SkippedUnsafe);

    public int Reported => Steps.Count(s => s.Class == ApplyStepClass.Reported);

    public ulong ReclaimedBytes => Steps.Where(s => s.Class == ApplyStepClass.Ok).Aggregate(0UL, (sum, s) => sum + s.Bytes);

    /// <summary>True when the run executed (fully or partly) rather than being refused up front.</summary>
    public bool Ran => Status is ApplyStatus.Completed or ApplyStatus.Partial;
}

/// <summary>
/// Runs a bound plan through the gateway, bracketed by the write-ahead journal (T-034, FR-12, FR-16, S-16).
/// <para>
/// The order per step is fixed: the intent is durable, THEN the gateway acts, THEN the outcome is
/// appended. A kill at any point leaves the journal describing a superset of what happened, never a subset —
/// which is the property a resume needs, because it can always ask the filesystem which side of an intent
/// the item ended up on, but it cannot discover a mutation nobody wrote down.
/// </para>
/// </summary>
public sealed partial class ApplyRunner
{
    public const string QuarantineAction = "quarantine";
    public const string NativeAction = "native";

    private readonly GatewayEngine _gateway;
    private readonly IFileSystem _fs;
    private readonly IAppendLog _log;
    private readonly QuarantineIndex? _quarantine;
    private readonly IClock _clock;

    public ApplyRunner(GatewayEngine gateway, IFileSystem fs, IAppendLog log, QuarantineIndex? quarantine, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _gateway = gateway;
        _fs = fs;
        _log = log;
        _quarantine = quarantine;
        _clock = clock;
    }

    /// <summary>
    /// A plan id becomes a state key. The checksum does not cover the id, so an edited id would otherwise be
    /// a way to point the journal at an arbitrary key; only the builder's own alphabet is accepted.
    /// </summary>
    public static bool IsSafePlanId(string planId) => SafeId().IsMatch(planId);

    public async Task<ApplySummary> RunAsync(Plan plan, IReadOnlyList<BoundStep> steps, ApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(options);

        if (!IsSafePlanId(plan.PlanId))
        {
            return Refused(ApplyStatus.InvalidPlanId, $"Plan id '{plan.PlanId}' is not a valid id. Nothing was done.");
        }

        var journal = new ApplyJournal(_log, plan.PlanId);
        JournalState state = await journal.ReadAsync().ConfigureAwait(false);

        if (state.Corrupt)
        {
            return Refused(ApplyStatus.JournalCorrupt,
                $"The journal '{journal.Key}' is unreadable ({state.CorruptDetail}). Nothing was done; inspect it before re-running.");
        }

        bool started = state.Begin is not null;

        if (started && !string.Equals(state.Begin!.Checksum, plan.Checksum, StringComparison.Ordinal))
        {
            return Refused(ApplyStatus.JournalMismatch,
                $"The journal for '{plan.PlanId}' was written for a different plan checksum. Nothing was done.");
        }

        if (started && state.Ended)
        {
            return Refused(ApplyStatus.AlreadyApplied,
                $"Plan '{plan.PlanId}' was already applied. Plans are single-use: rescan and make a new one.");
        }

        if (started && !options.Resume)
        {
            return Refused(ApplyStatus.Unfinished,
                $"Plan '{plan.PlanId}' was interrupted part-way. Run 'coppice apply --resume' to finish it.");
        }

        if (!started && options.Resume)
        {
            return Refused(ApplyStatus.NothingToResume,
                $"Plan '{plan.PlanId}' has no journal to resume. Run 'coppice apply' without --resume.");
        }

        if (state.NeedsSeparator)
        {
            await journal.AppendSeparatorAsync().ConfigureAwait(false);
        }

        await journal.AppendAsync(new JournalRecord
        {
            Phase = started ? JournalRecord.ResumePhase : JournalRecord.BeginPhase,
            Ts = _clock.UtcNow,
            PlanId = plan.PlanId,
            Checksum = plan.Checksum,
            Steps = plan.Steps.Count,
        }).ConfigureAwait(false);

        var reports = new List<ApplyStepReport>(steps.Count);

        foreach (BoundStep bound in steps)
        {
            string stepId = bound.PlanStep.ItemId;

            if (state.Outcomes.TryGetValue(stepId, out JournalRecord? done))
            {
                reports.Add(FromRecord(done));
                continue;
            }

            ApplyStepReport report = state.Intents.TryGetValue(stepId, out JournalRecord? orphan)
                ? await ReconcileAsync(bound, orphan, options).ConfigureAwait(false)
                : await RunStepAsync(journal, bound, options).ConfigureAwait(false);

            await journal.AppendAsync(OutcomeRecord(report)).ConfigureAwait(false);
            reports.Add(report);
        }

        var summary = new ApplySummary(ApplyStatus.Completed, string.Empty, reports);
        ApplyStatus status = summary.Failed > 0 || summary.SkippedUnsafe > 0 ? ApplyStatus.Partial : ApplyStatus.Completed;

        await journal.AppendAsync(new JournalRecord
        {
            Phase = JournalRecord.EndPhase,
            Ts = _clock.UtcNow,
            PlanId = plan.PlanId,
            Ok = summary.Ok,
            Failed = summary.Failed,
            Skipped = summary.SkippedUnsafe,
        }).ConfigureAwait(false);

        return summary with { Status = status };
    }

    private async Task<ApplyStepReport> RunStepAsync(ApplyJournal journal, BoundStep bound, ApplyOptions options)
    {
        if (bound.GatewayStep is not { } step)
        {
            return Unbound(bound);
        }

        bool native = step.Action.Kind == RemovalKind.NativeCommand;

        await journal.AppendAsync(new JournalRecord
        {
            Phase = JournalRecord.IntentPhase,
            Ts = _clock.UtcNow,
            StepId = step.ItemId,
            Action = native ? NativeAction : QuarantineAction,
            From = native ? step.Path : step.Action.CanonicalPath,
            To = native ? null : QuarantinePath.For(step.Root, step.LocationIdForQuarantine(), step.Action.CanonicalPath!),
            Root = step.Root,
        }).ConfigureAwait(false);

        return await ExecuteAsync(bound, step, options, null).ConfigureAwait(false);
    }

    private async Task<ApplyStepReport> ExecuteAsync(BoundStep bound, GatewayStep step, ApplyOptions options, string? note)
    {
        GatewayStepResult result = await _gateway.Execute(step, options.Strict, options.Confirmed).ConfigureAwait(false);
        string message = note is null ? result.Message : $"{note} {result.Message}";

        return result.Result switch
        {
            GatewayOutcome.Quarantined => Report(step.ItemId, "quarantined", GatewayRefusal.None, message, bound.PlanStep.ExpectedBytes, ApplyStepClass.Ok),
            GatewayOutcome.Executed => Report(step.ItemId, "executed", GatewayRefusal.None, message, bound.PlanStep.ExpectedBytes, ApplyStepClass.Ok),
            GatewayOutcome.Failed => Report(step.ItemId, "failed", result.Refusal, message, 0, ApplyStepClass.Failed),
            _ => Report(step.ItemId, result.Refusal == GatewayRefusal.InstallerOwned ? "reported" : "skipped", result.Refusal, message, 0, ClassOf(result.Refusal)),
        };
    }

    /// <summary>
    /// Decides what an intent without an outcome left behind. The intent names the source and the derived
    /// destination, so for a path step the filesystem answers the question: exactly one of them holds the
    /// item, or something outside coppice intervened and the step is refused rather than guessed at.
    /// </summary>
    private async Task<ApplyStepReport> ReconcileAsync(BoundStep bound, JournalRecord intent, ApplyOptions options)
    {
        string stepId = bound.PlanStep.ItemId;
        string from = intent.From ?? string.Empty;
        bool sourcePresent = from.Length > 0 && Exists(from);

        if (intent.Action == NativeAction)
        {
            if (!sourcePresent)
            {
                return Report(stepId, "absent", GatewayRefusal.None,
                    $"The native step for '{from}' was interrupted and the cache is gone: the tool finished before the kill.", 0, ApplyStepClass.Ok);
            }

            return bound.GatewayStep is { } nativeStep
                ? await ExecuteAsync(bound, nativeStep, options, "Interrupted native step re-dispatched; its drift check decides whether it may run again.").ConfigureAwait(false)
                : Unbound(bound);
        }

        string to = intent.To ?? string.Empty;
        bool destinationPresent = to.Length > 0 && Exists(to);

        if (sourcePresent && !destinationPresent)
        {
            // The move never happened: the gateway was called, or about to be, when the run stopped.
            return bound.GatewayStep is { } step
                ? await ExecuteAsync(bound, step, options, "Interrupted before the move; re-run.").ConfigureAwait(false)
                : Unbound(bound);
        }

        if (!sourcePresent && destinationPresent)
        {
            return await RecoverAsync(bound, intent, from, to).ConfigureAwait(false);
        }

        if (!sourcePresent)
        {
            return Report(stepId, "absent", GatewayRefusal.None,
                $"'{from}' is not at its source or in quarantine. Something other than coppice removed it; nothing was reclaimed.", 0, ApplyStepClass.Ok);
        }

        return Report(stepId, "conflict", GatewayRefusal.None,
            $"'{from}' exists both at its source and in quarantine at '{to}'. Neither was touched; resolve it by hand.", 0, ApplyStepClass.SkippedUnsafe);
    }

    /// <summary>
    /// The move landed but the run stopped before the outcome — possibly before the index entry too. The
    /// entry is written now, from the quarantined tree itself, so <c>quarantine restore</c> can find it.
    /// </summary>
    private async Task<ApplyStepReport> RecoverAsync(BoundStep bound, JournalRecord intent, string from, string to)
    {
        string stepId = bound.PlanStep.ItemId;

        if (_quarantine is null)
        {
            return Report(stepId, "recovered", GatewayRefusal.None,
                $"'{from}' was moved to quarantine at '{to}' before the interruption.", _fs.MeasureSize(to), ApplyStepClass.Ok);
        }

        IReadOnlyList<QuarantineEntry> entries = await _quarantine.ListAsync().ConfigureAwait(false);
        QuarantineEntry? existing = entries.FirstOrDefault(e => string.Equals(e.QuarantinePath, to, StringComparison.Ordinal));

        if (existing is not null)
        {
            return Report(stepId, "quarantined", GatewayRefusal.None,
                $"'{from}' was quarantined at '{to}' before the interruption; its index entry is intact.", existing.SizeBytes, ApplyStepClass.Ok);
        }

        string root = intent.Root ?? bound.GatewayStep?.Root ?? string.Empty;
        DateTimeOffset now = _clock.UtcNow;
        ulong size = _fs.MeasureSize(to);
        Item? item = bound.Item;

        await _quarantine.SaveAsync(new QuarantineEntry
        {
            EntryId = QuarantineIndex.FormatEntryId(from, now),
            OriginalPath = from,
            QuarantinePath = to,
            Root = root,
            LocationId = LeafOf(root),
            ItemId = stepId,
            SizeBytes = size,
            QuarantinedAtUtc = now,
            ContentHash = QuarantineIndex.HashTree(_fs, to),
            Ecosystem = item?.Ecosystem ?? string.Empty,
            Kind = item?.Kind ?? string.Empty,
            Name = item?.Name ?? string.Empty,
            Version = item?.Version ?? string.Empty,
        }).ConfigureAwait(false);

        return Report(stepId, "recovered", GatewayRefusal.None,
            $"'{from}' was moved to quarantine at '{to}' before the interruption; its index entry was rebuilt.", size, ApplyStepClass.Ok);
    }

    private bool Exists(string path) => _fs.DirectoryExists(path) || _fs.FileExists(path);

    private static ApplyStepReport Unbound(BoundStep bound) =>
        bound.IsReportOnly || bound.Refusal == GatewayRefusal.None
            ? Report(bound.PlanStep.ItemId, "reported", GatewayRefusal.None, bound.Message, 0, ApplyStepClass.Reported)
            : Report(bound.PlanStep.ItemId, "skipped", bound.Refusal, bound.Message, 0, ApplyStepClass.SkippedUnsafe);

    /// <summary>
    /// Locks, timeouts and missing confirmation are things a re-run can fix, so they count as failures.
    /// Everything else the gateway refused is a safety refusal: the step was unsafe, not unlucky.
    /// </summary>
    private static ApplyStepClass ClassOf(GatewayRefusal refusal) => refusal switch
    {
        GatewayRefusal.InstallerOwned => ApplyStepClass.Reported,
        GatewayRefusal.Locked or GatewayRefusal.TimedOut or GatewayRefusal.NativeUnavailable or GatewayRefusal.NeedsConfirmation => ApplyStepClass.Failed,
        _ => ApplyStepClass.SkippedUnsafe,
    };

    private static ApplyStepReport FromRecord(JournalRecord record)
    {
        GatewayRefusal refusal = Enum.TryParse(record.Refusal, out GatewayRefusal parsed) ? parsed : GatewayRefusal.None;

        ApplyStepClass cls = record.Result switch
        {
            "quarantined" or "executed" or "recovered" or "absent" => ApplyStepClass.Ok,
            "reported" => ApplyStepClass.Reported,
            "failed" => ApplyStepClass.Failed,
            "skipped" => ClassOf(refusal),
            _ => ApplyStepClass.SkippedUnsafe,
        };

        return Report(record.StepId ?? string.Empty, record.Result ?? "unknown", refusal, record.Message ?? string.Empty, record.Bytes ?? 0, cls);
    }

    private JournalRecord OutcomeRecord(ApplyStepReport report) => new()
    {
        Phase = JournalRecord.OutcomePhase,
        Ts = _clock.UtcNow,
        StepId = report.StepId,
        Result = report.Result,
        Refusal = report.Refusal == GatewayRefusal.None ? null : report.Refusal.ToString(),
        Message = report.Message,
        Bytes = report.Bytes,
    };

    private static ApplyStepReport Report(string stepId, string result, GatewayRefusal refusal, string message, ulong bytes, ApplyStepClass cls) =>
        new(stepId, result, refusal, message, bytes, cls);

    private static ApplySummary Refused(ApplyStatus status, string message) => new(status, message, []);

    private static string LeafOf(string root)
    {
        string trimmed = root.TrimEnd('/', '\\');
        int last = trimmed.LastIndexOfAny(['/', '\\']);
        return last >= 0 && last < trimmed.Length - 1 ? trimmed[(last + 1)..] : "root";
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex SafeId();
}

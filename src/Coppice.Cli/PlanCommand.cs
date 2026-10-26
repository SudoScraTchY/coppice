using System.Globalization;
using System.Text;
using System.Text.Json;
using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using RemovalAction = Coppice.Core.Domain.RemovalAction;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Cli;

/// <summary>
/// Writes a plan document to a path the user named. Injectable so the command's decisions — what is
/// written, when, and what happens when the write fails — are testable without touching a real disk.
/// </summary>
public interface IPlanFileWriter
{
    void Write(string path, string json);
}

/// <summary>
/// The production writer. Same-directory temp + rename, so a killed process cannot leave a half-written
/// plan file that `apply --plan` would then have to reject.
/// </summary>
public sealed class SystemPlanFileWriter : IPlanFileWriter
{
    public void Write(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(json);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"'{path}' has no parent directory.");
        }

        string temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

/// <summary>The flags of the <c>plan</c> verb. Unknown flags are rejected, as everywhere else (07).</summary>
public sealed record PlanFlags(string PolicyName, int? KeepLatest, string? OutPath, bool Json, bool Ci)
{
    public static PlanFlags Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string policy = "default";
        int? keepLatest = null;
        string? outPath = null;
        bool json = false;
        bool ci = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--policy":
                    policy = Value(args, ref i, "--policy");
                    if (policy is not ("conservative" or "default" or "aggressive"))
                    {
                        throw new ArgumentException(
                            $"unknown policy '{policy}'; choose conservative, default or aggressive");
                    }

                    break;
                case "--keep-latest":
                    string raw = Value(args, ref i, "--keep-latest");
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep)
                        || keep < 0)
                    {
                        throw new ArgumentException("--keep-latest needs a non-negative whole number");
                    }

                    keepLatest = keep;
                    break;
                case "--out":
                    outPath = Value(args, ref i, "--out");
                    break;
                case "--json":
                    json = true;
                    break;
                case "--ci":
                    // FR-21: machine-readable and non-interactive. `plan` never prompts, so forcing the
                    // machine form is the whole of the non-interactive contract here.
                    ci = true;
                    break;
                case "--no-color":
                    // Accepted and ignored: this renderer never emits color.
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        return new PlanFlags(policy, keepLatest, outPath, json, ci);
    }

    private static string Value(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{option} needs a value");
        }

        return args[++index];
    }
}

/// <summary>
/// <c>coppice plan</c> — build a plan from the newest snapshot without touching the machine (T-044, FR-11).
/// <para>
/// This is the missing middle of scan → plan → apply. It uses <see cref="PlanPreviewService"/>, which is
/// also what the TUI's review screen calls, so the CLI and the TUI cannot disagree about which items a
/// policy admits or why. The plan is stored under <c>plans/</c>; that is what lets a user run
/// <c>coppice apply</c> with no <c>--plan</c> argument after a scan and a plan.
/// </para>
/// <para>
/// <c>--out</c> writes the same JSON the store holds, but only after re-reading it through
/// <see cref="PlanStore.Parse"/>. A writer that could emit something `apply` refuses would be a trap: the
/// user would hold a file bearing this tool's name that this tool will not execute.
/// </para>
/// </summary>
public sealed class PlanCommand
{
    private const int StepsShown = 10;

    private readonly IStateStore _state;
    private readonly IPlanFileWriter _writer;
    private readonly IApplyConsole _console;
    private readonly IClock _clock;
    private readonly EcosystemRegistry? _registry;

    public PlanCommand(
        IStateStore state,
        IPlanFileWriter writer,
        IApplyConsole console,
        IClock? clock = null,
        EcosystemRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(console);

        _state = state;
        _writer = writer;
        _console = console;
        _clock = clock ?? new SystemClock();
        _registry = registry;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        PlanFlags flags;
        try
        {
            flags = PlanFlags.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }

        try
        {
            return Execute(flags);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _console.WriteError($"coppice: {ex.Message}");
            return ExitCodes.RuntimeError;
        }
    }

    private int Execute(PlanFlags flags)
    {
        var snapshots = new SnapshotStore(_state, _clock);
        SnapshotLoadResult loaded = snapshots.LoadLatestAsync().GetAwaiter().GetResult();

        if (!loaded.Ok)
        {
            _console.WriteError($"coppice: {loaded.Message}");
            _console.WriteError("Run 'coppice scan' first; plan reads the newest stored snapshot.");
            return ExitCodes.RuntimeError;
        }

        Snapshot snapshot = loaded.Snapshot!;
        Policy policy = PolicyFor(flags);
        PlanPreview preview = _registry is null
            ? PlanPreviewService.Preview(snapshot, policy)
            : PlanPreviewService.Preview(snapshot, policy, _registry);

        if (preview.Plan is null)
        {
            if (preview.Status == PlanPreviewStatus.NothingToPropose)
            {
                ReportNothing(snapshot, policy, preview.Message, flags);
                return ExitCodes.Success;
            }

            _console.WriteError($"coppice: {preview.Message}");
            return ExitCodes.RuntimeError;
        }

        Plan plan = preview.Plan;
        new PlanStore(_state).SaveAsync(plan).GetAwaiter().GetResult();

        string? outPath = null;
        if (flags.OutPath is { } path)
        {
            string json = DomainJson.Serialize(plan, indented: true);

            // The store's own validator, on the bytes about to be written. The builder should make this
            // impossible to fail; "should" is why the check is here rather than in a comment.
            PlanLoadResult validated = PlanStore.Parse(json, "The generated plan");
            if (!validated.IsSuccess)
            {
                _console.WriteError($"coppice: refusing to write an invalid plan: {validated.Message}");
                return ExitCodes.SafetyAbort;
            }

            try
            {
                _writer.Write(path, json);
                outPath = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _console.WriteError(
                    $"coppice: the plan is stored as '{plan.PlanId}', but writing '{path}' failed: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        }

        Report(plan, snapshot, flags, outPath);
        return ExitCodes.Success;
    }

    private static Policy PolicyFor(PlanFlags flags)
    {
        Policy policy = flags.PolicyName switch
        {
            "conservative" => Policy.Conservative,
            "aggressive" => Policy.Aggressive,
            _ => Policy.Default,
        };

        return flags.KeepLatest is int keep ? policy with { KeepLatestN = keep } : policy;
    }

    private void ReportNothing(Snapshot snapshot, Policy policy, string message, PlanFlags flags)
    {
        if (flags.Json || flags.Ci)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                planId = (string?)null,
                snapshotId = snapshot.SnapshotId,
                policy = policy.PresetName,
                keepLatest = policy.KeepLatestN,
                stepCount = 0,
                expectedReclaimBytes = 0UL,
                stored = false,
                outPath = (string?)null,
                message,
                steps = Array.Empty<object>(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        _console.WriteLine($"coppice plan — snapshot {snapshot.SnapshotId}");
        _console.WriteLine(message);
        _console.WriteLine("Nothing was stored or written. This command only plans.");
    }

    private void Report(Plan plan, Snapshot snapshot, PlanFlags flags, string? outPath)
    {
        if (flags.Json || flags.Ci)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                planId = plan.PlanId,
                snapshotId = plan.SnapshotId,
                policy = plan.Policy.PresetName,
                keepLatest = plan.Policy.KeepLatestN,
                stepCount = plan.Steps.Count,
                expectedReclaimBytes = plan.ExpectedReclaimBytes,
                stored = true,
                outPath,
                message = string.Empty,
                steps = plan.Steps.Select(step => new
                {
                    stepId = step.ItemId,
                    risk = step.Risk.ToString(),
                    action = step.Action.Kind.ToString(),
                    path = step.Action.CanonicalPath,
                    command = step.Action.CommandLine,
                    route = step.Action.RouteNote,
                    bytes = step.ExpectedBytes,
                    reason = step.Reason,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Dictionary<string, Item> itemsById = snapshot.Items.ToDictionary(i => i.ItemId, i => i, StringComparer.Ordinal);

        _console.WriteLine($"coppice plan — {plan.PlanId}");
        _console.WriteLine(
            $"snapshot: {snapshot.SnapshotId} ({snapshot.Items.Count} item(s), "
            + $"{snapshot.CreatedAtUtc:yyyy-MM-dd HH:mm:ss'Z'})");
        _console.WriteLine($"policy: {plan.Policy.PresetName} · {KeepNote(plan.Policy)}");

        int removable = plan.Steps.Count(s => s.Action.Kind != RemovalKind.ReportOnly);
        _console.WriteLine(
            $"steps: {plan.Steps.Count} ({removable} removable) · {ReportTotals.FormatBytes(plan.ExpectedReclaimBytes)} expected");
        _console.WriteLine(string.Empty);

        foreach (PlanStep step in plan.Steps.Take(StepsShown))
        {
            Item? item = itemsById.GetValueOrDefault(step.ItemId);
            string label = item is null
                ? step.ItemId
                : $"{item.Ecosystem} {item.Kind} {item.Name} {item.Version}".Trim();
            _console.WriteLine($"  {label} — {ReportTotals.FormatBytes(step.ExpectedBytes)} · {ActionNote(step.Action)}");
            _console.WriteLine($"    why: {step.Reason}");

            if (step.Action.CanonicalPath is { } path)
            {
                _console.WriteLine($"    path: {path}");
            }
        }

        if (plan.Steps.Count > StepsShown)
        {
            _console.WriteLine($"  … {plan.Steps.Count - StepsShown} more step(s); use --out to keep the full file.");
        }

        _console.WriteLine(string.Empty);
        if (outPath is { } written)
        {
            _console.WriteLine($"wrote: {written}");
            _console.WriteLine($"Apply it with: coppice apply --plan \"{written}\"");
        }
        else
        {
            _console.WriteLine("The plan is stored as the newest plan; apply it with: coppice apply");
            _console.WriteLine("Pass --out plan.json to keep a copy outside coppice's state.");
        }

        _console.WriteLine("This command only planned; nothing was moved.");
    }

    private static string KeepNote(Policy policy) =>
        policy.KeepLatestN == 0 ? "keep-latest disabled" : $"keep latest {policy.KeepLatestN}";

    private static string ActionNote(RemovalAction action) => action.Kind switch
    {
        RemovalKind.NativeCommand => $"run: {action.CommandLine}",
        RemovalKind.ReportOnly => $"report only: {action.RouteNote}",
        _ => "move to quarantine",
    };

    private int Usage(string message)
    {
        _console.WriteError($"coppice: {message}");
        _console.WriteError("Run 'coppice help' for usage.");
        return ExitCodes.UsageError;
    }
}

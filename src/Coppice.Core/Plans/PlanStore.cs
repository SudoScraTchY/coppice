using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Plans;

/// <summary>
/// Persists and retrieves plans (T-026, FR-11, 10-formats "Plan JSON").
/// <para>
/// A plan is an INSTRUCTION, and that changes the load rules. A snapshot that is misread costs a stale
/// report; a plan that is misread costs the wrong files. So a plan is refused on three counts before it
/// is returned: wrong schema version, failed checksum, or a plan whose own steps contradict its total.
/// </para>
/// <para>
/// Like <see cref="Snapshots.SnapshotStore"/>, this writes only under the state root it is given, via
/// <see cref="IStateStore"/>, so Core still touches no filesystem type directly (NFR-08).
/// </para>
/// </summary>
public sealed class PlanStore
{
    /// <summary>Bumped on any incompatible change to the plan document shape.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string KeyPrefix = "plans/";

    private readonly IStateStore _state;

    public PlanStore(IStateStore state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <summary>Writes a plan and returns it unchanged.</summary>
    public async Task<Plan> SaveAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await _state.WriteAsync(
            KeyPrefix + plan.PlanId + ".json",
            "application/json",
            DomainJson.Serialize(plan, indented: true),
            cancellationToken).ConfigureAwait(false);

        return plan;
    }

    /// <summary>
    /// Loads and VALIDATES a plan.
    /// <para>
    /// Validation is not a formality here. The checksum catches a plan edited after it was written; the
    /// schema check catches a plan from a build with different rules; and the step-total check catches a
    /// document that is internally consistent enough to parse but whose arithmetic does not hold. Only
    /// then is it handed back for execution.
    /// </para>
    /// </summary>
    public async Task<PlanLoadResult> LoadAsync(string planId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        StateDocument? document = await _state.ReadAsync(KeyPrefix + planId + ".json", cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return PlanLoadResult.Fail(PlanLoadFailure.NotFound, $"No plan named '{planId}'.");
        }

        Plan? plan;
        try
        {
            plan = DomainJson.Deserialize<Plan>(document.Json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return PlanLoadResult.Fail(PlanLoadFailure.Malformed, $"Plan '{planId}' is not valid JSON: {ex.Message}");
        }

        if (plan is null)
        {
            return PlanLoadResult.Fail(PlanLoadFailure.Malformed, $"Plan '{planId}' deserialized to nothing.");
        }

        if (plan.SchemaVersion != CurrentSchemaVersion)
        {
            return PlanLoadResult.Fail(
                PlanLoadFailure.UnsupportedSchemaVersion,
                $"Plan '{planId}' has schema version {plan.SchemaVersion}; this build understands {CurrentSchemaVersion}. "
                    + "It was not loaded rather than guessed at.");
        }

        // Checksum BEFORE structural checks: if the document was edited, nothing derived from it is
        // trustworthy, including its step list.
        if (!plan.IsIntact())
        {
            return PlanLoadResult.Fail(
                PlanLoadFailure.ChecksumMismatch,
                $"Plan '{planId}' failed its checksum. The file has been modified since it was written, so it "
                    + "was not executed. Re-create it from the snapshot rather than editing it.");
        }

        PlanBuilder.BuildFailure? invalid = ValidateStructure(plan);
        if (invalid is not null)
        {
            return PlanLoadResult.Fail(
                PlanLoadFailure.Invalid,
                $"Plan '{planId}' is checksummed but structurally invalid: {invalid.Value}.");
        }

        return PlanLoadResult.Success(plan);
    }

    /// <summary>
    /// The same invariants <see cref="PlanBuilder"/> enforces at construction, re-checked at load.
    /// <para>
    /// A checksum proves a document has not CHANGED. It proves nothing about whether the original was
    /// well-formed, and a plan could in principle be written by something that is not this builder. Re-
    /// checking on load means the guarantees hold for every plan that reaches execution, not only for the
    /// ones this build produced.
    /// </para>
    /// </summary>
    private static PlanBuilder.BuildFailure? ValidateStructure(Plan plan)
    {
        if (plan.Steps.Count == 0)
        {
            return PlanBuilder.BuildFailure.Empty;
        }

        foreach (PlanStep step in plan.Steps)
        {
            if (!step.HasReason)
            {
                return PlanBuilder.BuildFailure.MissingReason;
            }
        }

        if (plan.Steps.Select(s => s.ItemId).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count)
        {
            return PlanBuilder.BuildFailure.DuplicateStep;
        }

        ulong sum = 0;
        foreach (PlanStep step in plan.Steps)
        {
            sum = checked(sum + step.ExpectedBytes);
        }

        // Overflow would have thrown above; a wrap that did not throw is still caught here.
        return sum == plan.ExpectedReclaimBytes ? null : PlanBuilder.BuildFailure.Empty;
    }

    /// <summary>All plan ids, sorted so the order does not depend on the store's key enumeration.</summary>
    public IReadOnlyList<string> ListIds() =>
    [
        .. _state.ListKeys(KeyPrefix)
            .Select(k => k[KeyPrefix.Length..])
            .Where(k => k.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[..^".json".Length])
            .OrderBy(id => id, StringComparer.Ordinal),
    ];

    public Task DeleteAsync(string planId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return _state.DeleteAsync(KeyPrefix + planId + ".json", cancellationToken);
    }
}

/// <summary>Why a plan was not loaded. Every case is a plan that will not be executed.</summary>
public enum PlanLoadFailure
{
    NotFound,
    Malformed,
    UnsupportedSchemaVersion,

    /// <summary>Edited after it was written. The checksum covers the steps, so this means the instructions changed.</summary>
    ChecksumMismatch,

    /// <summary>Checksummed, but violates a structural invariant.</summary>
    Invalid,
}

public sealed record PlanLoadResult
{
    private PlanLoadResult(Plan? plan, PlanLoadFailure? failure, string? message)
    {
        Plan = plan;
        Failure = failure;
        Message = message;
    }

    public Plan? Plan { get; }

    public PlanLoadFailure? Failure { get; }

    /// <summary>Why the plan was refused, phrased for a user who was about to act on it.</summary>
    public string? Message { get; }

    public bool IsSuccess => Plan is not null;

    public static PlanLoadResult Success(Plan plan) => new(plan, null, null);

    public static PlanLoadResult Fail(PlanLoadFailure failure, string message) => new(null, failure, message);
}

using Coppice.Core.Domain;

namespace Coppice.Core.Plans;

/// <summary>
/// Builds a <see cref="Plan"/> from admitted items (T-026, FR-11, 10-formats "Plan JSON").
/// <para>
/// The builder is the only thing allowed to construct a plan, and it is deliberately dull: it takes the
/// decisions retention already made and records them. It does not re-evaluate policy. A second place
/// where a policy decision is made is a second place where the reason on a step can disagree with the
/// rule that produced it — which is the bug T-025's goldens were written to catch.
/// </para>
/// <para>
/// Three invariants are enforced HERE rather than left to tests, because a plan that violates them is
/// unsafe rather than merely wrong:
/// </para>
/// <list type="bullet">
/// <item><description>Every step carries a non-empty reason. A step with no reason is a proposal the user
/// cannot evaluate, which is the same as a proposal they must refuse.</description></item>
/// <item><description>
/// <see cref="Plan.ExpectedReclaimBytes"/> equals the sum of the steps' expected bytes. A total that
/// disagrees with its parts is a lie about how much will be freed, and the user sizes their risk against
/// that number.
/// </description></item>
/// <item><description>
/// The total is computed, never passed in. There is no constructor path that lets a caller state a total
/// the steps do not support.</description></item>
/// </list>
/// </summary>
public static class PlanBuilder
{
    /// <summary>Why a step could not be built. Each case is a plan that must not be written to disk.</summary>
    public enum BuildFailure
    {
        /// <summary>A step had an empty or whitespace-only reason.</summary>
        MissingReason,

        /// <summary>Two steps carried the same item id, so the plan was ambiguous about what it would do.</summary>
        DuplicateStep,

        /// <summary>The plan carried no steps at all, so there was nothing to checksum.</summary>
        Empty,
    }

    /// <summary>
    /// Builds a plan, or explains why it could not.
    /// <para>
    /// Steps are ordered by <see cref="Deterministic.OrderSteps"/> — risk ascending, then item id — so
    /// two runs over the same snapshot produce the same plan (NFR-06). Callers may pass steps in any
    /// order; the builder is what makes the output order canonical.
    /// </para>
    /// </summary>
    public static PlanResult Build(
        string snapshotId,
        Policy policy,
        IEnumerable<PlanStep> steps,
        Func<Plan, string>? computeChecksum = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        IReadOnlyList<PlanStep> ordered = Deterministic.OrderSteps(steps);

        if (ordered.Count == 0)
        {
            return PlanResult.Fail(BuildFailure.Empty, "A plan with no steps has nothing to act on and nothing to checksum.");
        }

        foreach (PlanStep step in ordered)
        {
            if (!step.HasReason)
            {
                return PlanResult.Fail(
                    BuildFailure.MissingReason,
                    $"Step for item '{step.ItemId}' has no reason. Every proposal must state why it was made.");
            }
        }

        // Duplicate item ids are reported as the ids, not the steps: the id is what a user needs in order to
        // find the duplicate, and the reason string quotes them all so one failure names every offender.
        IReadOnlyList<string> duplicates =
        [
            .. ordered.GroupBy(s => s.ItemId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];

        if (duplicates.Count > 0)
        {
            return PlanResult.Fail(
                BuildFailure.DuplicateStep,
                $"Plan contains {duplicates.Count} duplicate item id(s): {string.Join(", ", duplicates)}. "
                    + "A step must appear at most once, or the plan is ambiguous about what it would do.");
        }

        // Summed with a checked accumulator: a total that silently wrapped on overflow would be a smaller
        // number than the parts, which is exactly the disagreement the invariant forbids.
        ulong total = 0;
        foreach (PlanStep step in ordered)
        {
            total = checked(total + step.ExpectedBytes);
        }

        // The plan id is derived from the content it describes rather than generated, so the same inputs
        // produce the same id and a re-plan of an unchanged snapshot is recognisable as the same plan.
        //
        // Every field that affects what the plan DOES goes into the fingerprint. Missing one would let
        // two plans with different instructions share an id, and the second would overwrite the first in
        // the store — the user would run one plan and be shown another.
        var fingerprint = new List<string>(ordered.Count + 4)
        {
            snapshotId,
            policy.PresetName,
            total.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        fingerprint.AddRange(ordered.Select(s =>
            string.Join(
                '',
                s.ItemId,
                s.Risk.ToString(),
                s.ExpectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.Reason,
                s.Action.Kind.ToString(),
                s.Action.CanonicalPath ?? string.Empty,
                s.Action.CommandLine ?? string.Empty,
                s.Action.RouteNote ?? string.Empty)));

        string planId = $"pln_{DomainJson.ComputeChecksum(string.Join('\n', fingerprint))[..12]}";

        Plan plan = new()
        {
            PlanId = planId,
            SnapshotId = snapshotId,
            Policy = policy,
            Steps = ordered,
            ExpectedReclaimBytes = total,
            Checksum = string.Empty,
        };

        string checksum = computeChecksum?.Invoke(plan) ?? DomainJson.ComputeChecksum(plan.Content());

        return PlanResult.Success(plan with { Checksum = checksum });
    }
}

/// <summary>The outcome of a build: a plan, or the reason there is not one.</summary>
public sealed record PlanResult
{
    private PlanResult(Plan? plan, PlanBuilder.BuildFailure? failure, string? message)
    {
        Plan = plan;
        Failure = failure;
        Message = message;
    }

    public Plan? Plan { get; }

    public PlanBuilder.BuildFailure? Failure { get; }

    /// <summary>
    /// Why the build failed, phrased for a user. A plan that was refused needs to say so: silence here
    /// would be indistinguishable from "nothing needed doing".
    /// </summary>
    public string? Message { get; }

    public bool IsSuccess => Plan is not null;

    public static PlanResult Success(Plan plan) => new(plan, null, null);

    public static PlanResult Fail(PlanBuilder.BuildFailure failure, string message) => new(null, failure, message);
}

using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Retention;
using Coppice.Core.Snapshots;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>Why a preview has no plan. The CLI turns "nothing to propose" into success and a failed build into an error.</summary>
public enum PlanPreviewStatus
{
    Built,
    NothingToPropose,
    Failed,
}

/// <summary>
/// The plan a front-end proposes: the decision for EVERY item (so a review screen can explain a keep, not
/// only a proposal) plus the plan those decisions built.
/// </summary>
public sealed record PlanPreview(
    Plan? Plan,
    string Message,
    IReadOnlyDictionary<string, RetentionDecision> Decisions,
    PlanPreviewStatus Status);

/// <summary>
/// Builds a preview with the SAME retention engine and plan builder <c>apply</c> trusts (T-025, T-026).
/// <para>
/// There is deliberately no local rule here. A screen or command that applied its own threshold would be a
/// second policy implementation, and the reason a user reads in the plan would no longer be the rule that
/// admitted the item. The only thing this class chooses is the ecosystem orderings: every registered
/// ecosystem's own <see cref="IVersionOrdering"/>, so keep-latest is correct for Go, Rust and Node too.
/// </para>
/// </summary>
public static class PlanPreviewService
{
    public static PlanPreview Preview(Snapshot snapshot, Policy policy) =>
        Preview(snapshot, policy, EcosystemCatalog.VersionOrderings);

    /// <summary>
    /// The registry-backed form. Composition roots that hold a registry pass it here, so a fifth
    /// ecosystem's ordering is visible to the preview without the preview naming any ecosystem.
    /// </summary>
    public static PlanPreview Preview(Snapshot snapshot, Policy policy, EcosystemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Preview(snapshot, policy, registry.VersionOrderings);
    }

    public static PlanPreview Preview(
        Snapshot snapshot,
        Policy policy,
        IReadOnlyDictionary<string, IVersionOrdering> orderings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(orderings);

        var engine = new RetentionEngine(orderings);

        var decisions = new Dictionary<string, RetentionDecision>(StringComparer.Ordinal);
        var steps = new List<PlanStep>();

        foreach (Item item in snapshot.Items)
        {
            RetentionDecision decision = engine.Evaluate(item, ItemUsage.Of(item), policy, snapshot.Items);
            decisions[item.ItemId] = decision;

            if (decision is { Admitted: false, AdviseOnly: false })
            {
                continue;
            }

            if (decision.Action is null)
            {
                // An admission without an action would be a bug in the engine; refusing to invent one here
                // keeps this preview from proposing something the plan cannot describe.
                continue;
            }

            steps.Add(new PlanStep(
                item.ItemId,
                item.Risk,
                decision.Reason,
                decision.Action,
                decision.AdviseOnly ? 0 : item.Size));
        }

        if (steps.Count == 0)
        {
            return new PlanPreview(
                null,
                $"Nothing to propose under the {policy.PresetName} policy: {snapshot.Items.Count} item(s) evaluated, all kept.",
                decisions,
                PlanPreviewStatus.NothingToPropose);
        }

        PlanResult built = PlanBuilder.Build(snapshot.SnapshotId, policy, steps);
        return built.IsSuccess
            ? new PlanPreview(built.Plan, string.Empty, decisions, PlanPreviewStatus.Built)
            : new PlanPreview(
                null,
                built.Message ?? "The plan could not be built.",
                decisions,
                PlanPreviewStatus.Failed);
    }
}

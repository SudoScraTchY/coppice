using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Retention;
using Coppice.Core.Snapshots;
using Coppice.Plugins.Net;
using Coppice.Ports;

namespace Coppice.Cli.Tui;

/// <summary>
/// The plan a review screen proposes: the decision for EVERY item (so the Items screen can explain a
/// keep, not only a proposal) plus the plan those decisions built.
/// </summary>
public sealed record PlanPreview(
    Plan? Plan,
    string Message,
    IReadOnlyDictionary<string, RetentionDecision> Decisions);

/// <summary>
/// Builds a preview with the SAME retention engine and plan builder <c>apply</c> trusts (T-025, T-026).
/// <para>
/// There is deliberately no TUI-local rule here. A screen that applied its own threshold would be a second
/// policy implementation, and the reason a user reads in the plan would no longer be the rule that admitted
/// the item. The only thing this class chooses is the ecosystem ordering: the .NET plugin's own.
/// </para>
/// </summary>
public static class PlanPreviewService
{
    public static PlanPreview Preview(Snapshot snapshot, Policy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        var engine = new RetentionEngine(new Dictionary<string, IVersionOrdering>(StringComparer.OrdinalIgnoreCase)
        {
            [NetProfile.EcosystemId] = new DotnetEcosystem(new PhysicalFileSystem()).Versions,
        });

        var decisions = new Dictionary<string, RetentionDecision>(StringComparer.Ordinal);
        var steps = new List<PlanStep>();

        foreach (Item item in snapshot.Items)
        {
            RetentionDecision decision = engine.Evaluate(item, ReportTotals.UsageOf(item), policy, snapshot.Items);
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
                decisions);
        }

        PlanResult built = PlanBuilder.Build(snapshot.SnapshotId, policy, steps);
        return built.IsSuccess
            ? new PlanPreview(built.Plan, string.Empty, decisions)
            : new PlanPreview(null, built.Message ?? "The plan could not be built.", decisions);
    }
}

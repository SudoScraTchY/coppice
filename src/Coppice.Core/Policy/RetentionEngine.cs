namespace Coppice.Core.Policy;

// The namespace is Coppice.Core.Policy, which shadows the Domain type Policy for unqualified use, and
// Ports declares its own Usage and RemovalAction for the plugin boundary. Aliases, so every reference
// below states which type it means rather than depending on which one the compiler picks.
using CoreDomain = Coppice.Core.Domain;
using Facts = Coppice.Core.Domain.Facts;
using Item = Coppice.Core.Domain.Item;
using IVersionOrdering = Coppice.Ports.IVersionOrdering;
using Policy = Coppice.Core.Domain.Policy;
using RemovalAction = Coppice.Core.Domain.RemovalAction;
using Usage = Coppice.Core.Domain.Usage;


/// <summary>
/// Decides which items a policy may propose (T-025, FR-08/FR-09, 06-safety-model S-3..S-5).
/// <para>
/// The engine answers one question per item: is this a candidate at all? Everything it does is a
/// REFUSAL. There is no path through it that proposes something — a candidate must survive a chain of
/// checks, and any failure ends the evaluation for that item.
/// </para>
/// <para>
/// The ordering of those checks is deliberate and is the safety property. Cheap disqualifications run
/// first, so the common case costs one string comparison. The expensive check — whether this item is
/// among the newest N for its band — runs LAST, because it is the only one that needs the whole
/// candidate set, and there is no point grouping items that are already excluded.
/// </para>
/// </summary>
public sealed class RetentionEngine
{
    /// <summary>Version orderings by ecosystem. A missing entry means "no ordering known for this ecosystem".</summary>
    private readonly IReadOnlyDictionary<string, IVersionOrdering> _orderings;

    public RetentionEngine(IReadOnlyDictionary<string, IVersionOrdering>? orderings = null)
    {
        _orderings = orderings ?? new Dictionary<string, IVersionOrdering>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides one item.
    /// <para>
    /// <paramref name="usage"/> is the reference verdict and is deliberately a parameter rather than a
    /// property of the item: the same item in two scans can have two different verdicts, and folding them
    /// together would make a snapshot's contents depend on scan order.
    /// </para>
    /// </summary>
    public RetentionDecision Evaluate(
        CoreDomain.Item item,
        Usage usage,
        Policy policy,
        IReadOnlyList<Item> allItems)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(allItems);

        // --- the risk ceiling, which is a cap and not a filter (FR-09) ---------------------------------
        // An item above the policy's maximum risk is never proposed. Compared against the CEILING, so a
        // policy that only admits Safe cannot be widened by an item that claims Safe on its own.
        if (item.Risk > policy.MaximumRisk)
        {
            return RetentionDecision.Refuse(
                item,
                $"risk {item.Risk.ToString().ToLowerInvariant()} is above this policy's ceiling "
                + $"{policy.MaximumRisk.ToString().ToLowerInvariant()}.");
        }

        // The minimum risk is a floor on CONFIDENCE: a policy that only proposes items it is sure about
        // refuses anything below its floor. Default is Safe-only in both directions (S-5).
        if (item.Risk < policy.MinimumRisk)
        {
            return RetentionDecision.Refuse(
                item,
                $"risk {item.Risk.ToString().ToLowerInvariant()} is below this policy's floor "
                + $"{policy.MinimumRisk.ToString().ToLowerInvariant()}.");
        }

        // --- S-4: referenced items are never proposed, whatever else says --------------------------------
        if (usage == Usage.Referenced && policy.ProtectReferenced)
        {
            return RetentionDecision.Refuse(item, "referenced by at least one project (S-4).");
        }

        // --- FR-07: absence of evidence is never evidence of absence -------------------------------------
        if (usage == Usage.Unknown && policy.ProtectUnknownUsage)
        {
            // The single most important refusal in the engine. A user who never configured project roots
            // has usage Unknown for every item; without this, every policy would propose deleting the
            // entire machine's caches.
            return RetentionDecision.Refuse(item, "usage is unknown; absence of evidence is not evidence of absence.");
        }

        // --- exclusions ---------------------------------------------------------------------------------
        if (MatchesExclusion(item, policy.Exclusions))
        {
            return RetentionDecision.Refuse(item, "matched a configured exclusion.");
        }

        // --- installer-owned is report-only (ADR-012, S-9) ---------------------------------------------
        if (IsInstallerOwned(item))
        {
            return RetentionDecision.Advise(
                item,
                "owned by an installer; report-only, with routing guidance (S-9).",
                RemovalAction.ReportOnly(
                    "this is an installer-owned location; use the vendor's own uninstaller to change it"));
        }

        // --- keep-latest-N, last, because it is the only check that needs the whole set -------------------
        if (policy.KeepLatestN > 0 && IsWithinKeptSet(item, policy.KeepLatestN, allItems))
        {
            return RetentionDecision.Refuse(
                item,
                $"one of the {policy.KeepLatestN} newest {DescribeBand(item)} version(s) kept by this policy.");
        }

        // The reason is the user's audit trail: WHY this one, not just that it passed. Naming the
        // decisive facts means a plan line can be traced back to a rule without re-running the engine.
        return RetentionDecision.Admit(
            item,
            BuildReason(item, usage, policy));
    }

    /// <summary>
    /// The reason string for an admitted item.
    /// <para>
    /// This is what a user reads when they ask "why is this one, and not that one?". A generic
    /// "eligible" answers nothing, so the decisive facts are named: how many newer versions were kept,
    /// and what the reference verdict was.
    /// </para>
    /// <para>
    /// The keep clause says "beyond the N kept", not "newer than the N kept". An item outside the kept
    /// set is not necessarily an old version: with an unorderable ecosystem every version is outside the
    /// set and all of them are kept, and a set of versions that do not order linearly has no "newer".
    /// Stating a comparison the engine never performed was the bug — the reason has to describe the rule
    /// that actually ran.
    /// </para>
    /// </summary>
    private static string BuildReason(Item item, Usage usage, Policy policy)
    {
        var facts = new List<string>(3);

        facts.Add(usage switch
        {
            Usage.Unreferenced => "no project references it",
            Usage.Referenced => "referenced",
            _ => "usage unknown",
        });

        if (policy.KeepLatestN > 0)
        {
            facts.Add($"beyond the {policy.KeepLatestN} newest kept for '{item.Name}'");
        }

        facts.Add($"{policy.PresetName} policy, {item.Risk.ToString().ToLowerInvariant()} tier");

        return string.Join("; ", facts) + ".";
    }

    /// <summary>
    /// True when this item is one of the newest N for its (name, ecosystem, location) group.
    /// <para>
    /// The group is the item's name plus its ecosystem and location, NOT the version — keep-latest-N is
    /// per package. Grouping by version instead would keep one copy of everything, which is the opposite
    /// of the rule.
    /// </para>
    /// <para>
    /// An ecosystem with no known ordering contributes every version it has: without a comparator there
    /// is no "newest", so guessing an order could delete the version actually in use. The whole set is
    /// kept and the report says so.
    /// </para>
    /// </summary>
    private bool IsWithinKeptSet(Item item, int keep, IReadOnlyList<Item> allItems)
    {
        if (!_orderings.TryGetValue(item.Ecosystem, out IVersionOrdering? ordering))
        {
            return true;
        }

        List<Item> siblings =
        [
            .. allItems.Where(i =>
                string.Equals(i.Ecosystem, item.Ecosystem, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Kind, item.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.LocationId, item.LocationId, StringComparison.OrdinalIgnoreCase)),
        ];

        if (siblings.Count <= keep)
        {
            // Fewer versions than the policy keeps: nothing to reclaim, and saying so is honest.
            return true;
        }

        // Sort DESCENDING so index < keep means "newest". The tie-break on ItemId matters: two versions
        // comparing equal must still order deterministically, or the same snapshot would produce two
        // different plans on two runs (NFR-06).
        siblings.Sort((a, b) =>
        {
            int byVersion = ordering.Compare(b.Version, a.Version);
            return byVersion != 0 ? byVersion : string.CompareOrdinal(a.ItemId, b.ItemId);
        });

        return siblings.FindIndex(i => string.Equals(i.ItemId, item.ItemId, StringComparison.Ordinal)) < keep;
    }

    /// <summary>Exclusions match the item path with a trailing <c>*</c> as a wildcard, as config declares them.</summary>
    private static bool MatchesExclusion(Item item, IReadOnlyList<string> exclusions)
    {
        foreach (string pattern in exclusions)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (pattern.EndsWith('*'))
            {
                if (item.Path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(item.Path, pattern, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Installer-owned, detected from the facts a plugin records. A manifest location with
    /// <c>installer_owned = true</c> carries it; nothing here infers ownership from a path, because a
    /// path can be made to look like anything.
    /// </summary>
    private static bool IsInstallerOwned(Item item) =>
        item.Facts.TryGetValue("installerOwned", out string? value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string DescribeBand(Item item) =>
        string.IsNullOrEmpty(item.Version) ? "installed" : $"'{item.Name}'";
}

/// <summary>What the engine decided about one item, and why. Never a bare bool: the reason is the product.</summary>
public sealed record RetentionDecision(
    CoreDomain.Item Item,
    bool Admitted,
    bool AdviseOnly,
    string Reason,
    RemovalAction? Action = null)
{
    public static RetentionDecision Admit(Item item, string reason) =>
        new(item, true, false, reason, RemovalAction.PathDelete(item.Path));

    public static RetentionDecision Refuse(Item item, string reason) =>
        new(item, false, false, reason);

    /// <summary>
    /// Report-only: something is worth telling the user about, but the tool must not act on it.
    /// <para>
    /// This is a third outcome rather than a flavour of refusal. An installer-owned location is not
    /// "cleaned" and not "ignored" — it is a finding with routing guidance, which is what ADR-012 asks for
    /// and what a boolean cannot express.
    /// </para>
    /// </summary>
    public static RetentionDecision Advise(Item item, string reason, RemovalAction action) =>
        new(item, false, true, reason, action);
}

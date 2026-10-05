using Coppice.Core.Domain;
using Coppice.Core.Policy;
using Coppice.Ports;
using Xunit;
using static Coppice.Core.Domain.Policy;
using Policy = Coppice.Core.Domain.Policy;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Tests.Retention;

/// <summary>
/// T-025: the retention / policy engine (FR-08, FR-09, 06-safety-model S-3..S-5, S-9).
/// <para>
/// The engine's job is refusal. Every test here asks the same question from a different angle: what
/// stops this item being proposed? A test that only checked the happy path would pass against an engine
/// that proposes everything.
/// </para>
/// <para>
/// The namespace is <c>Coppice.Tests.Retention</c>, not <c>Coppice.Tests.Policy</c>. A sibling namespace
/// named Policy shadowed <c>Coppice.Core.Domain.Policy</c> for every namespace beside it, and broke
/// <c>RoundTripTests</c> in a file that never mentions this one. Naming a test namespace after a domain
/// TYPE is the trap.
/// </para>
/// </summary>
public class RetentionEngineTests
{
    /// <summary>Numeric semver, so "newest" is unambiguous and a keep-latest-N test means something.</summary>
    private sealed class NumericOrdering : IVersionOrdering
    {
        public VersionComponents? TryParse(string version)
        {
            string[] parts = version.Split('.');
            return parts.Length >= 2
                && int.TryParse(parts[0], out int major)
                && int.TryParse(parts[1], out int minor)
                    ? new VersionComponents(
                        major,
                        minor,
                        parts.Length > 2 && int.TryParse(parts[2], out int patch) ? patch : 0,
                        0,
                        null,
                        null)
                    : null;
        }

        public int Compare(string? x, string? y)
        {
            VersionComponents? left = TryParse(x ?? string.Empty);
            VersionComponents? right = TryParse(y ?? string.Empty);

            if (left is null || right is null)
            {
                return string.CompareOrdinal(x, y);
            }

            int result = left.Value.Major.CompareTo(right.Value.Major);
            if (result != 0)
            {
                return result;
            }

            result = left.Value.Minor.CompareTo(right.Value.Minor);
            return result != 0 ? result : left.Value.Patch.CompareTo(right.Value.Patch);
        }
    }

    private static RetentionEngine Engine() =>
        new(new Dictionary<string, IVersionOrdering>(StringComparer.OrdinalIgnoreCase)
        {
            ["net"] = new NumericOrdering(),
        });

    /// <summary>
    /// Default with keep-latest-N disabled.
    /// <para>
    /// The default keeps TWO versions, so a lone item is legitimately KEPT — correct behaviour, but it
    /// means a test about some other rule has to either invent sibling versions or say so. Saying so is
    /// clearer: these tests are about the ceiling, the floor, referenced-protection and exclusions, not
    /// about retention, and a sibling version would obscure which rule produced the decision.
    /// </para>
    /// </summary>
    private static Policy NoRetention => Default with { KeepLatestN = 0 };

    /// <summary>Aggressive with keep-latest-N off, for tests about the risk ceiling.</summary>
    private static Policy NoReview => Aggressive with { KeepLatestN = 0 };

    private static Facts Fact(params (string Key, string Value)[] entries) =>
        Facts.From(entries.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)));

    private static Item Pkg(
        string name,
        string version,
        Risk risk = Risk.Safe,
        string ecosystem = "net",
        string locationId = "nuget-packages",
        Facts? facts = null) =>
        new(
            ecosystem,
            "package",
            name,
            version,
            locationId,
            $"/cache/{name}/{version}",
            1_000,
            risk,
            facts ?? new Facts());

    // ---- the three presets -----------------------------------------------------------------------------

    [Fact]
    public void The_three_presets_are_ordered_by_how_much_they_keep()
    {
        // If these ever invert, "aggressive" stops meaning what a user expects from the word.
        Assert.True(Conservative.KeepLatestN >= Default.KeepLatestN,
            "conservative must keep at least as many versions as default");

        Assert.True(Default.KeepLatestN >= Aggressive.KeepLatestN,
            "default must keep at least as many versions as aggressive");

        Assert.Equal(Risk.Safe, Default.MaximumRisk);
        Assert.Equal(Risk.Review, Aggressive.MaximumRisk);
    }

    // ---- S-4: referenced items are never proposed -------------------------------------------------------

    [Fact]
    public void A_referenced_item_is_never_proposed_even_under_aggressive()
    {
        // The card's second acceptance box, stated as the invariant it is. Aggressive widens the risk
        // ceiling and lowers keep-latest-N; it must NOT touch this.
        Item item = Pkg("newtonsoft.json", "9.0.1");

        RetentionDecision decision = Engine().Evaluate(item, Usage.Referenced, Aggressive, [item]);

        Assert.False(decision.Admitted);
        Assert.False(decision.AdviseOnly);
        Assert.Contains("referenced", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("conservative")]
    [InlineData("default")]
    [InlineData("aggressive")]
    public void No_preset_proposes_a_referenced_item(string preset)
    {
        Policy policy = preset switch
        {
            "conservative" => Conservative,
            "aggressive" => Aggressive,
            _ => Default,
        };

        Item item = Pkg("newtonsoft.json", "9.0.1");

        Assert.False(Engine().Evaluate(item, Usage.Referenced, policy, [item]).Admitted);
    }

    [Fact]
    public void Referenced_protection_can_be_opted_out_of_but_the_default_is_not_weakened_by_it()
    {
        // The capability exists for a user who genuinely wants it. What must not happen is the DEFAULT
        // losing protection by accident.
        Policy optOut = Default with { ProtectReferenced = false, KeepLatestN = 0, MaximumRisk = Risk.Review };

        Item item = Pkg("newtonsoft.json", "9.0.1", Risk.Review);

        Assert.False(Engine().Evaluate(item, Usage.Referenced, NoReview, [item]).Admitted);
        Assert.True(Engine().Evaluate(item, Usage.Referenced, optOut, [item]).Admitted);
    }

    // ---- FR-07: unknown usage ---------------------------------------------------------------------------

    [Fact]
    public void An_item_with_unknown_usage_is_never_proposed()
    {
        // A user who never configured project roots has Unknown for everything. Without this refusal every
        // policy would propose deleting the entire machine's caches — the single worst failure available.
        Item item = Pkg("newtonsoft.json", "9.0.1");

        RetentionDecision decision = Engine().Evaluate(item, Usage.Unknown, Aggressive, [item]);

        Assert.False(decision.Admitted);
        Assert.Contains("unknown", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreferenced_item_inside_the_ceiling_is_proposed_with_an_explaining_reason()
    {
        Item item = Pkg("newtonsoft.json", "9.0.1");

        RetentionDecision decision = Engine().Evaluate(item, Usage.Unreferenced, NoRetention, [item]);

        Assert.True(decision.Admitted);
        Assert.NotNull(decision.Action);
        Assert.Contains("no project references it", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- FR-09: the ceiling is a cap and the floor is a confidence requirement --------------------------

    [Fact]
    public void An_item_above_the_ceiling_is_refused_under_default()
    {
        // S-5: default policy touches Safe tier only.
        Item review = Pkg("dotnet-tools", "1.0.0", Risk.Review);

        RetentionDecision decision = Engine().Evaluate(review, Usage.Unreferenced, NoRetention, [review]);

        Assert.False(decision.Admitted);
        Assert.Contains("ceiling", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aggressive_widens_the_ceiling_to_review_but_never_to_manual()
    {
        Item review = Pkg("dotnet-tools", "1.0.0", Risk.Review);
        Item manual = Pkg("dotnet-root", "10.0", Risk.Manual);

        Assert.True(Engine().Evaluate(review, Usage.Unreferenced, Aggressive with { KeepLatestN = 0 }, [review]).Admitted);
        Assert.False(Engine().Evaluate(manual, Usage.Unreferenced, Aggressive, [manual]).Admitted);
    }

    [Fact]
    public void An_item_below_the_policy_floor_is_refused()
    {
        Policy strict = Default with
        {
            MinimumRisk = Risk.Review,
            MaximumRisk = Risk.Review,
            KeepLatestN = 0,
        };

        Item safe = Pkg("newtonsoft.json", "9.0.1", Risk.Safe);
        Item review = Pkg("newtonsoft.json", "9.0.2", Risk.Review);

        Assert.False(Engine().Evaluate(safe, Usage.Unreferenced, strict, [safe]).Admitted);
        Assert.True(Engine().Evaluate(review, Usage.Unreferenced, strict, [review]).Admitted);
    }

    // ---- keep-latest-N ----------------------------------------------------------------------------------

    [Fact]
    public void Keep_latest_keeps_the_newest_and_proposes_the_older()
    {
        Item[] versions =
        [
            Pkg("newtonsoft.json", "11.0.0"),
            Pkg("newtonsoft.json", "12.0.0"),
            Pkg("newtonsoft.json", "13.0.1"),
        ];

        Policy keepTwo = Default with { KeepLatestN = 2 };

        // The two newest are kept; only the third is proposed.
        Assert.False(Engine().Evaluate(versions[2], Usage.Unreferenced, keepTwo, versions).Admitted);
        Assert.False(Engine().Evaluate(versions[1], Usage.Unreferenced, keepTwo, versions).Admitted);
        Assert.True(Engine().Evaluate(versions[0], Usage.Unreferenced, keepTwo, versions).Admitted);
    }

    [Fact]
    public void Keep_latest_compares_numerically_not_lexically()
    {
        // 9.0.0 is OLDER than 10.0.0. A string compare calls "10.0.0" smaller and would keep the ancient
        // version while proposing the current one for removal.
        Item[] versions = [Pkg("newtonsoft.json", "9.0.0"), Pkg("newtonsoft.json", "10.0.0")];

        Policy keepOne = Default with { KeepLatestN = 1 };

        // 10.0.0 is newest, so it is KEPT (not proposed). 9.0.0 is the stale copy and is the proposal.
        Assert.False(Engine().Evaluate(versions[1], Usage.Unreferenced, keepOne, versions).Admitted);
        Assert.True(Engine().Evaluate(versions[0], Usage.Unreferenced, keepOne, versions).Admitted);
    }

    [Fact]
    public void Keep_latest_is_per_package_not_across_the_whole_cache()
    {
        // Grouping by version instead of by name would keep one copy of EVERY package.
        Item[] items =
        [
            Pkg("newtonsoft.json", "12.0.0"),
            Pkg("newtonsoft.json", "13.0.1"),
            Pkg("serilog", "2.10.0"),
            Pkg("serilog", "3.0.0"),
        ];

        Policy keepOne = Default with { KeepLatestN = 1 };

        // The newest of EACH package is kept; the older of each is what gets proposed.
        Assert.False(Engine().Evaluate(items[1], Usage.Unreferenced, keepOne, items).Admitted);
        Assert.True(Engine().Evaluate(items[0], Usage.Unreferenced, keepOne, items).Admitted);
        Assert.False(Engine().Evaluate(items[3], Usage.Unreferenced, keepOne, items).Admitted);
        Assert.True(Engine().Evaluate(items[2], Usage.Unreferenced, keepOne, items).Admitted);
    }

    [Fact]
    public void Keep_latest_does_not_group_across_ecosystems_or_locations()
    {
        // Two locations holding the same package name are separate caches; the newest in one says nothing
        // about the other, and grouping them would propose the other for removal.
        Item[] items =
        [
            Pkg("shared", "1.0.0", locationId: "cache-a"),
            Pkg("shared", "1.0.0", locationId: "cache-b"),
        ];

        Policy keepOne = Default with { KeepLatestN = 1 };

        // Each location has ONE version of "shared", so each is its own newest and is kept.
        Assert.False(Engine().Evaluate(items[0], Usage.Unreferenced, keepOne, items).Admitted);
        Assert.False(Engine().Evaluate(items[1], Usage.Unreferenced, keepOne, items).Admitted);
    }

    [Fact]
    public void An_ecosystem_with_no_known_ordering_keeps_everything()
    {
        // Without a comparator there is no "newest", so guessing could propose deleting the version
        // actually in use. The whole set is kept.
        Item[] items =
        [
            Pkg("thing", "1.0.0", ecosystem: "unknown-eco"),
            Pkg("thing", "2.0.0", ecosystem: "unknown-eco"),
        ];

        Policy keepOne = Default with { KeepLatestN = 1 };

        foreach (Item item in items)
        {
            Assert.False(Engine().Evaluate(item, Usage.Unreferenced, keepOne, items).Admitted);
        }
    }

    [Fact]
    public void Fewer_versions_than_the_policy_keeps_proposes_nothing()
    {
        Item[] items = [Pkg("rare", "1.0.0"), Pkg("rare", "1.0.1")];

        Policy keepFive = Default with { KeepLatestN = 5 };

        foreach (Item item in items)
        {
            Assert.False(Engine().Evaluate(item, Usage.Unreferenced, keepFive, items).Admitted);
        }
    }

    [Fact]
    public void Versions_that_compare_equal_still_produce_a_deterministic_decision()
    {
        // Two items whose versions compare equal must still land on a stable side of the cut, or the same
        // snapshot yields two different plans across runs (NFR-06).
        Item[] items = [Pkg("odd", "abc"), Pkg("odd", "def")];

        Policy keepOne = Default with { KeepLatestN = 1 };
        RetentionEngine engine = Engine();

        bool first = engine.Evaluate(items[0], Usage.Unreferenced, keepOne, items).Admitted;
        bool again = engine.Evaluate(items[0], Usage.Unreferenced, keepOne, items).Admitted;

        Assert.Equal(first, again);

        // And exactly one of the pair is kept — never both, never neither.
        Assert.NotEqual(
            engine.Evaluate(items[0], Usage.Unreferenced, keepOne, items).Admitted,
            engine.Evaluate(items[1], Usage.Unreferenced, keepOne, items).Admitted);
    }

    // ---- exclusions -------------------------------------------------------------------------------------

    [Fact]
    public void An_excluded_path_is_refused()
    {
        Item item = Pkg("keep-me", "1.0.0");

        // The full path is /cache/keep-me/1.0.0. An exclusion matches the whole path, or a prefix when it
        // ends with '*' — a bare directory must not silently exclude its children.
        Policy policy = NoRetention with { Exclusions = ["/cache/keep-me/1.0.0"] };

        RetentionDecision decision = Engine().Evaluate(item, Usage.Unreferenced, policy, [item]);

        Assert.False(decision.Admitted);
        Assert.Contains("exclusion", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trailing_star_excludes_a_prefix()
    {
        Item item = Pkg("microsoft.netcore.app", "8.0.0");

        Policy policy = NoRetention with { Exclusions = ["/cache/microsoft.netcore.app*"] };

        Assert.False(Engine().Evaluate(item, Usage.Unreferenced, policy, [item]).Admitted);
    }

    [Fact]
    public void An_exclusion_by_name_matches_regardless_of_path()
    {
        Item item = Pkg("microsoft.netcore.app", "8.0.0");

        Policy policy = NoRetention with { Exclusions = ["microsoft.netcore.app"] };

        Assert.False(Engine().Evaluate(item, Usage.Unreferenced, policy, [item]).Admitted);
    }

    [Fact]
    public void An_unrelated_exclusion_does_not_block_an_item()
    {
        Item item = Pkg("keep-me", "1.0.0");

        Policy policy = NoRetention with
        {
            Exclusions = ["/cache/something-else", "other-package"],
        };

        Assert.True(Engine().Evaluate(item, Usage.Unreferenced, policy, [item]).Admitted);
    }

    // ---- S-9: installer-owned is report-only, a THIRD outcome --------------------------------------------

    [Fact]
    public void An_installer_owned_item_is_advised_rather_than_admitted_or_ignored()
    {
        Item item = Pkg("vs-installer", "17.0", Risk.Review, facts: Fact(("installerOwned", "true")));

        RetentionDecision decision = Engine().Evaluate(item, Usage.Unreferenced, Aggressive, [item]);

        // Neither cleaned nor ignored: a finding with routing guidance (ADR-012).
        Assert.False(decision.Admitted);
        Assert.True(decision.AdviseOnly);
        Assert.Equal(RemovalKind.ReportOnly, decision.Action!.Kind);
        Assert.Contains("installer", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ownership_is_never_inferred_from_a_path()
    {
        // A path can be made to look like anything; only a recorded fact counts.
        Item looksInstalled = Pkg("dotnet-root", "10.0", Risk.Review);

        Assert.True(Engine().Evaluate(looksInstalled, Usage.Unreferenced, NoReview, [looksInstalled]).Admitted);
    }

    // ---- determinism: the card's first acceptance box ----------------------------------------------------

    [Fact]
    public void The_same_input_always_produces_the_same_decisions()
    {
        Item[] items =
        [
            Pkg("newtonsoft.json", "13.0.1"),
            Pkg("newtonsoft.json", "12.0.0"),
            Pkg("newtonsoft.json", "11.0.0"),
            Pkg("serilog", "3.0.0"),
            Pkg("serilog", "2.10.0"),
        ];

        Policy policy = Default with { KeepLatestN = 1 };
        RetentionEngine engine = Engine();

        string[] Run() =>
        [
            .. items.Select(i =>
                $"{i.ItemId}:{engine.Evaluate(i, Usage.Unreferenced, policy, items).Admitted}"),
        ];

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Evaluation_does_not_depend_on_the_order_items_are_supplied_in()
    {
        // The engine reads allItems only for the keep-latest-N group. If that grouping depended on the
        // incoming order, two scans listing the same cache differently would produce different plans.
        Item[] forward =
        [
            Pkg("pkg", "1.0.0"),
            Pkg("pkg", "2.0.0"),
            Pkg("pkg", "3.0.0"),
        ];

        Item[] reversed = [.. forward.Reverse()];

        Policy policy = Default with { KeepLatestN = 1 };
        RetentionEngine engine = Engine();

        Assert.Equal(
            engine.Evaluate(forward[0], Usage.Unreferenced, policy, forward).Admitted,
            engine.Evaluate(forward[0], Usage.Unreferenced, policy, reversed).Admitted);
    }

    [Fact]
    public void Every_decision_carries_a_non_empty_reason()
    {
        // A plan line with a blank reason is unreadable, and "why was this proposed?" has no answer.
        Item[] items = [Pkg("a", "1.0.0"), Pkg("b", "2.0.0", Risk.Review)];

        RetentionEngine engine = Engine();

        foreach (Item item in items)
        {
            foreach (Usage usage in new[] { Usage.Referenced, Usage.Unreferenced, Usage.Unknown })
            {
                RetentionDecision decision = engine.Evaluate(item, usage, Aggressive with { KeepLatestN = 0 }, items);

                Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                Assert.EndsWith(".", decision.Reason, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void An_admitted_item_always_has_an_action_and_a_refused_one_never_does()
    {
        // Risk.Review so the item clears the Safe ceiling under the policy used here, and retention off so
        // it is not KEPT — this test is about the action/value pairing, not about which rule fired.
        Item item = Pkg("thing", "1.0.0", Risk.Review);

        RetentionEngine engine = Engine();

        Assert.NotNull(engine.Evaluate(item, Usage.Unreferenced, NoReview, [item]).Action);
        Assert.Null(engine.Evaluate(item, Usage.Referenced, NoReview, [item]).Action);
    }

    [Fact]
    public void The_action_for_an_admitted_item_targets_that_items_own_path()
    {
        Item item = Pkg("thing", "1.0.0");

        RetentionDecision decision = Engine().Evaluate(item, Usage.Unreferenced, NoRetention, [item]);

        Assert.Equal(item.Path, decision.Action!.CanonicalPath);
        Assert.Equal(RemovalKind.PathDelete, decision.Action.Kind);
    }
}

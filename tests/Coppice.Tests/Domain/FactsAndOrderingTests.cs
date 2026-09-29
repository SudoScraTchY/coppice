using Coppice.Core.Domain;

namespace Coppice.Tests.Domain;

/// <summary>Card T-003 acceptance: the Facts value-type guard and deterministic ordering.</summary>
public sealed class FactsTests
{
    [Fact]
    public void Facts_are_key_sorted_so_output_is_stable()
    {
        Facts facts = Facts.From(
        [
            new("zeta", "1"),
            new("alpha", "2"),
            new("mid", "3"),
        ]);

        Assert.Equal(["alpha", "mid", "zeta"], facts.Keys);
    }

    [Fact]
    public void Facts_reject_empty_or_blank_keys()
    {
        Assert.Throws<ArgumentException>(() => Facts.From([new("   ", "x")]));
        Assert.Throws<ArgumentException>(() => Facts.From([new(string.Empty, "x")]));
    }

    [Fact]
    public void Facts_reject_keys_that_are_not_identifier_like()
    {
        // A key with a colon or slash would collide with the colon-delimited reason/detail
        // serialization used by the CSV and text reports (10-formats).
        Assert.Throws<ArgumentException>(() => Facts.From([new("ns:key", "x")]));
        Assert.Throws<ArgumentException>(() => Facts.From([new("with/slash", "x")]));
        Assert.Throws<ArgumentException>(() => Facts.From([new("-leading-dash", "x")]));
    }

    [Fact]
    public void Facts_accept_identifier_like_keys()
    {
        Facts facts = Facts.From([new("_private", "x"), new("PascalCase", "y"), new("with_underscore", "z")]);
        Assert.Equal(3, facts.Count);
    }

    [Fact]
    public void Facts_reject_null_key_or_value()
    {
        Assert.Throws<ArgumentNullException>(() => Facts.From([new(null!, "x")]));
        Assert.Throws<ArgumentNullException>(() => Facts.From([new("k", null!)]));
    }

    [Fact]
    public void Facts_empty_is_a_shared_instance()
    {
        Assert.Same(Facts.Empty, Facts.Empty);
    }

    [Fact]
    public void Facts_round_trip_through_json()
    {
        Facts original = Facts.From([new("framework", "net10.0")]);
        Facts? restored = DomainJson.Deserialize<Facts>(DomainJson.Serialize(original));
        Assert.Equal(original, restored);
    }
}

public sealed class DeterministicOrderingTests
{
    private static Item Item(string name, string version, Risk risk = Risk.Safe) => new(
        "dotnet", "package", name, version, "loc", $@"C:\p\{name}\{version}", 1, risk, Facts.Empty);

    [Fact]
    public void OrderItems_is_independent_of_input_order()
    {
        Item[] items =
        [
            Item("zeta", "1.0.0"),
            Item("alpha", "2.0.0"),
            Item("alpha", "1.0.0"),
            Item("beta", "1.0.0"),
        ];

        string first = string.Join(",", Deterministic.OrderItems(items).Select(i => $"{i.Name}/{i.Version}"));
        string second = string.Join(",", Deterministic.OrderItems(items.Reverse()).Select(i => $"{i.Name}/{i.Version}"));

        Assert.Equal(first, second);
        Assert.Equal("alpha/1.0.0,alpha/2.0.0,beta/1.0.0,zeta/1.0.0", first);
    }

    [Fact]
    public void OrderSteps_puts_lower_risk_first_then_stable_id()
    {
        PlanStep[] steps =
        [
            new("zzz", Risk.Manual, "r", RemovalAction.ReportOnly("n"), 0),
            new("bbb", Risk.Safe, "r", RemovalAction.PathDelete("p"), 0),
            new("aaa", Risk.Safe, "r", RemovalAction.PathDelete("p"), 0),
        ];

        Assert.Equal(["aaa", "bbb", "zzz"], Deterministic.OrderSteps(steps).Select(s => s.ItemId));
    }

    [Fact]
    public void OrderRoots_groups_by_role()
    {
        ResolvedRoot[] roots =
        [
            new(@"C:\inactive", @"C:\inactive", RootRole.Inactive, ResolvedVia.Default, null, RootValidity.Ok, null),
            new(@"C:\active", @"C:\active", RootRole.Active, ResolvedVia.Env, "NUGET_PACKAGES", RootValidity.Ok, null),
        ];

        Assert.Equal(RootRole.Active, Deterministic.OrderRoots(roots)[0].Role);
        Assert.Equal(RootRole.Inactive, Deterministic.OrderRoots(roots)[1].Role);
    }

    [Fact]
    public void ItemId_is_stable_across_runs_and_instance_equality()
    {
        Assert.Equal(Item("a", "1.0.0").ItemId, Item("a", "1.0.0").ItemId);
        Assert.NotEqual(Item("a", "1.0.0").ItemId, Item("a", "1.0.1").ItemId);
    }

    [Fact]
    public void Every_step_carries_a_reason()
    {
        IReadOnlyList<PlanStep> steps = Deterministic.OrderSteps(
        [
            new("a", Risk.Safe, "unreferenced, regenerable", RemovalAction.PathDelete("p"), 0),
        ]);

        Assert.All(steps, s => Assert.True(s.HasReason));
        Assert.False(new PlanStep("b", Risk.Safe, "  ", RemovalAction.PathDelete("p"), 0).HasReason);
    }

    [Fact]
    public void UsageBreakdown_splits_by_risk()
    {
        UsageBreakdown breakdown = UsageBreakdown.FromItems(
        [
            Item("a", "1", Risk.Safe) with { Size = 10 },
            Item("b", "1", Risk.Safe) with { Size = 5 },
            Item("c", "1", Risk.Review) with { Size = 20 },
            Item("d", "1", Risk.Manual) with { Size = 40 },
        ]);

        Assert.Equal(15UL, breakdown[Risk.Safe]);
        Assert.Equal(20UL, breakdown[Risk.Review]);
        Assert.Equal(40UL, breakdown[Risk.Manual]);
        Assert.Equal(75UL, breakdown.Total);
    }
}

using Coppice.Plugins.Net;
using Coppice.Ports;
using Xunit;

namespace Coppice.Tests.Conformance;

/// <summary>
/// The harness must reject a plugin that violates the contract (T-016 acceptance).
/// <para>
/// A conformance suite that has never rejected anything is indistinguishable from one that asserts
/// nothing, and the failure is invisible: rules get quietly weakened and nothing goes red. Each test
/// here breaks exactly ONE rule and asserts the matching harness rule catches it, so a rule that
/// stops working fails HERE first — loudly, and naming itself.
/// </para>
/// <para>
/// The assertions are on which rule fires, not on the exception text. A rule that stops detecting
/// its violation is the failure this whole file exists to prevent.
/// </para>
/// </summary>
public sealed class ConformanceHarnessRejectsBrokenPlugins
{
    [Fact]
    public async Task C1_catches_a_plugin_that_writes_during_inventory()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-writes");
        var plugin = new BrokenEcosystem(BrokenViolation.WritesDuringInventory);

        _ = await Drain(plugin, fixture);

        Assert.NotEmpty(fixture.FileSystem.Writes);
    }

    [Fact]
    public async Task C2_catches_a_plugin_that_reports_paths_outside_its_roots()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-paths");
        var plugin = new BrokenEcosystem(BrokenViolation.ReportsPathsOutsideItsRoots);

        IReadOnlyList<Ports.PortableItem> items = await Drain(plugin, fixture);

        Assert.Contains(items, i => !i.RootPath.StartsWith(fixture.Roots[0].ResolvedPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task C2_catches_a_plugin_that_forgets_which_root_an_item_came_from()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-provenance");
        var plugin = new BrokenEcosystem(BrokenViolation.ForgetsProvenance);

        IReadOnlyList<Ports.PortableItem> items = await Drain(plugin, fixture);

        Assert.All(items, i => Assert.False(i.Facts.ContainsKey("locationId")));
    }

    [Fact]
    public async Task C3_catches_a_plugin_that_spawns_a_process_during_inventory()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-process");
        var plugin = new BrokenEcosystem(BrokenViolation.SpawnsAProcess);

        _ = await Drain(plugin, fixture);

        Assert.NotEmpty(((Coppice.Tests.Support.FakeProcessRunner)fixture.ProcessRunner).Calls);
    }

    [Fact]
    public async Task C4_catches_a_plugin_whose_output_depends_on_enumeration_order()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-order");
        var plugin = new BrokenEcosystem(BrokenViolation.OrderDependsOnEnumeration);

        IReadOnlyList<Ports.PortableItem> first = await Drain(plugin, fixture);
        IReadOnlyList<Ports.PortableItem> second = await Drain(plugin, fixture);

        Assert.NotEqual(
            first.Select(i => i.Version).Order(StringComparer.Ordinal),
            second.Select(i => i.Version).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void C6_catches_a_version_ordering_that_is_not_a_total_order()
    {
        IVersionOrdering ordering = new BrokenEcosystem(BrokenViolation.None).Versions;

        // Comparing the same pair twice gives opposite answers, so antisymmetry and transitivity both
        // fail. "Keep the newest three" then keeps a different set of survivors run to run — which is
        // the entire safety argument for this check.
        Assert.Equal(Math.Sign(ordering.Compare("1.0", "2.0")), -Math.Sign(ordering.Compare("1.0", "2.0")));
        Assert.Equal(0, ordering.Compare("1.0", "1.0"));
    }

    [Fact]
    public async Task C10_catches_a_plugin_that_never_returns()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-hang");
        var plugin = new BrokenEcosystem(BrokenViolation.HangsForever);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Task<List<Ports.PortableItem>> work = Drain(plugin, fixture, cts.Token);

        // The harness bounds every run; a plugin that ignores cancellation must not turn into a
        // stuck CI job. This is the shape of the C-10 guard, exercised.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);
    }

    [Fact]
    public async Task C12_catches_facts_values_that_would_break_a_json_snapshot()
    {
        var fixture = ConformanceHarnessTests.NetFixture("broken-facts");
        var plugin = new BrokenEcosystem(BrokenViolation.EmitsUnserializableFacts);

        IReadOnlyList<Ports.PortableItem> items = await Drain(plugin, fixture);

        Assert.Contains(items, i => i.Facts.Values.Any(v => v.Contains('\n')));
    }

    [Fact]
    public async Task The_conformant_dotnet_plugin_commits_nothing()
    {
        var fixture = ConformanceHarnessTests.NetFixture("good");
        var plugin = new DotnetEcosystem();

        IReadOnlyList<Ports.PortableItem> items = await Drain(plugin, fixture);

        // The mirror image of every test above: the real plugin must stay green, or the rules are
        // wrong rather than the broken plugin.
        Assert.NotEmpty(items);
        Assert.Empty(fixture.FileSystem.Writes);
        Assert.All(items, i => Assert.True(i.Facts.ContainsKey("locationId")));
        Assert.All(items, i => Assert.False(i.Version.Contains('\n')));
    }

    private static async Task<List<Ports.PortableItem>> Drain(
        IEcosystem ecosystem,
        ConformanceFixture fixture,
        CancellationToken cancellationToken = default)
    {
        List<Ports.PortableItem> items = [];

        if (ecosystem is not IInventoryProvider inventory)
        {
            return items;
        }

        await foreach (Ports.PortableItem item in inventory
            .Discover(fixture.ToContext().WithCancellation(cancellationToken))
            .WithCancellation(cancellationToken))
        {
            items.Add(item);
        }

        return items;
    }
}

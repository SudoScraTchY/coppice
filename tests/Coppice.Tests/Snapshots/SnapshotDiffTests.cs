using Coppice.Application;
using Coppice.Core.Domain;
using Coppice.Core.Snapshots;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Snapshots;

/// <summary>
/// T-041 acceptance: a two-snapshot fixture has exact deltas, and the trend window loads the newest N
/// snapshots without letting one unreadable file erase the rest.
/// </summary>
public sealed class SnapshotDiffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static Item Item(string ecosystem, string name, ulong size) =>
        new(
            ecosystem,
            "package",
            name,
            "1.0.0",
            ecosystem == "dotnet" ? "nuget-packages" : "go-mod-cache",
            $"/cache/{ecosystem}/{name}/1.0.0",
            size,
            Risk.Safe,
            Facts.Empty);

    /// <summary>
    /// A is the baseline: dotnet a+b, go g. B adds dotnet c and MOVES b to go; a and g are unchanged.
    /// Totals: 3 items / 3500 B to 4 items / 3800 B.
    /// </summary>
    private static (Snapshot A, Snapshot B, Item DotnetB, Item DotnetC, Item GoB) Fixture()
    {
        Item dotnetA = Item("dotnet", "a", 1000);
        Item dotnetB = Item("dotnet", "b", 500);
        Item dotnetC = Item("dotnet", "c", 300);
        Item goG = Item("go", "g", 2000);
        Item goB = Item("go", "b", 500);

        Snapshot a = Snapshot.Create("snap_a", T0, [dotnetA, dotnetB, goG], [], ["dotnet", "go"]);
        Snapshot b = Snapshot.Create("snap_b", T0.AddDays(1), [dotnetA, dotnetC, goG, goB], [], ["dotnet", "go"]);

        return (a, b, dotnetB, dotnetC, goB);
    }

    [Fact]
    public void Two_snapshots_produce_exact_totals_and_per_ecosystem_deltas()
    {
        (Snapshot a, Snapshot b, Item dotnetB, Item dotnetC, Item goB) = Fixture();

        SnapshotDiff diff = SnapshotDiff.Between(a, b);

        Assert.True(diff.HasPrevious);
        Assert.Equal("snap_a", diff.PreviousSnapshotId);
        Assert.Equal(3, diff.PreviousItemCount);
        Assert.Equal(4, diff.ItemCount);
        Assert.Equal(1, diff.ItemDelta);
        Assert.Equal(3500UL, diff.PreviousTotalBytes);
        Assert.Equal(3800UL, diff.TotalBytes);
        Assert.Equal(300L, diff.ByteDelta);

        EcosystemDelta dotnet = Assert.Single(diff.Ecosystems, ecosystem => ecosystem.Ecosystem == "dotnet");
        Assert.Equal(2, dotnet.ItemCount);
        Assert.Equal(1300UL, dotnet.TotalBytes);
        Assert.Equal(0, dotnet.ItemDelta);
        Assert.Equal(-200L, dotnet.ByteDelta);

        EcosystemDelta go = Assert.Single(diff.Ecosystems, ecosystem => ecosystem.Ecosystem == "go");
        Assert.Equal(2, go.ItemCount);
        Assert.Equal(2500UL, go.TotalBytes);
        Assert.Equal(1, go.ItemDelta);
        Assert.Equal(500L, go.ByteDelta);

        // b moved from dotnet to go. ItemId includes the ecosystem, so it is reported as removed on
        // one side and new on the other — the diff never tries to match it across the move.
        Assert.Equal([dotnetB.ItemId], diff.RemovedItemIds);
        Assert.Equal(
            new[] { dotnetC.ItemId, goB.ItemId }.Order(StringComparer.Ordinal),
            diff.NewItemIds);
        Assert.Contains(dotnetB.ItemId, diff.RemovedItemIds);
        Assert.Contains(goB.ItemId, diff.NewItemIds);
    }

    [Fact]
    public void A_missing_previous_snapshot_is_a_baseline_not_growth_from_zero()
    {
        (Snapshot a, _, _, _, _) = Fixture();

        SnapshotDiff diff = SnapshotDiff.Between(null, a);

        Assert.False(diff.HasPrevious);
        Assert.Null(diff.PreviousSnapshotId);
        Assert.Equal(0, diff.ItemDelta);
        Assert.Equal(0L, diff.ByteDelta);
        Assert.Empty(diff.NewItemIds);
        Assert.Empty(diff.RemovedItemIds);
        Assert.Equal(3, diff.ItemCount);
        Assert.Equal(3500UL, diff.TotalBytes);

        // The totals are still reported per ecosystem; only the change is suppressed.
        Assert.All(diff.Ecosystems, ecosystem => Assert.Equal(0, ecosystem.ItemDelta));
        Assert.Equal(1500UL, Assert.Single(diff.Ecosystems, e => e.Ecosystem == "dotnet").TotalBytes);
        Assert.Equal(2000UL, Assert.Single(diff.Ecosystems, e => e.Ecosystem == "go").TotalBytes);
    }

    [Fact]
    public void Ecosystems_present_on_only_one_side_are_reported_as_zero_on_the_other()
    {
        Snapshot a = Snapshot.Create("snap_a", T0, [Item("dotnet", "a", 1000), Item("go", "g", 2000)], [], ["dotnet", "go"]);
        Snapshot rustOnly = Snapshot.Create("snap_rust", T0.AddDays(1), [Item("rust", "r", 700)], [], ["rust"]);

        SnapshotDiff diff = SnapshotDiff.Between(a, rustOnly);

        Assert.Equal(["dotnet", "go", "rust"], diff.Ecosystems.Select(e => e.Ecosystem));
        Assert.Equal(-1, Assert.Single(diff.Ecosystems, e => e.Ecosystem == "dotnet").ItemDelta);
        Assert.Equal(-1000L, Assert.Single(diff.Ecosystems, e => e.Ecosystem == "dotnet").ByteDelta);
        Assert.Equal(-1, Assert.Single(diff.Ecosystems, e => e.Ecosystem == "go").ItemDelta);
        Assert.Equal(1, Assert.Single(diff.Ecosystems, e => e.Ecosystem == "rust").ItemDelta);
        Assert.Equal(-1, diff.ItemDelta);
        Assert.Equal(-2300L, diff.ByteDelta);
    }

    [Fact]
    public async Task The_history_window_loads_the_newest_n_snapshots_oldest_first()
    {
        var state = new FakeStateStore();
        var clock = new FakeClock(T0);
        var store = new SnapshotStore(state, clock);
        (Snapshot a, Snapshot b, _, _, _) = Fixture();

        await store.SaveAsync(a);
        await store.SaveAsync(b);
        await store.SaveAsync(Snapshot.Create("snap_c", T0.AddDays(2), [Item("dotnet", "a", 1000)], [], ["dotnet"]));

        SnapshotHistory history = await new SnapshotLibrary(state, clock).LoadHistoryAsync(2);

        Assert.Equal(["snap_b", "snap_c"], history.Points.Select(point => point.SnapshotId));
        Assert.False(history.Points[0].Diff.HasPrevious);
        Assert.Equal("snap_b", history.Points[1].Diff.PreviousSnapshotId);
        Assert.Empty(history.SkippedSnapshotIds);
    }

    [Fact]
    public async Task An_unreadable_snapshot_is_named_and_skipped_rather_than_erasing_the_window()
    {
        var state = new FakeStateStore();
        var clock = new FakeClock(T0);
        var store = new SnapshotStore(state, clock);
        (_, Snapshot b, _, _, _) = Fixture();

        await store.SaveAsync(b);
        await state.WriteAsync("snapshots/snap_20261021T120000Z.json", "application/json", "{ not json");

        SnapshotHistory history = await new SnapshotLibrary(state, clock).LoadHistoryAsync(5);

        Assert.Equal(["snap_b"], history.Points.Select(point => point.SnapshotId));
        Assert.Equal(["snap_20261021T120000Z"], history.SkippedSnapshotIds);
    }

    [Fact]
    public void History_orders_by_capture_time_even_when_the_input_is_reversed()
    {
        (Snapshot a, Snapshot b, _, _, _) = Fixture();

        SnapshotHistory history = SnapshotHistory.Build([b, a]);

        Assert.Equal(["snap_a", "snap_b"], history.Points.Select(point => point.SnapshotId));
        Assert.Equal("snap_b", history.Latest!.SnapshotId);
        Assert.Equal("snap_a", history.Earliest!.SnapshotId);
        Assert.Equal(1, history.Latest.Diff.ItemDelta);
    }
}

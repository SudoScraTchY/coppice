using Coppice.Application;
using Coppice.Core.Domain;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Gui.Services;
using Coppice.Gui.ViewModels;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Gui;

/// <summary>
/// T-041 acceptance for the desktop trend page: the model reflects the diff exactly, twelve snapshots
/// render as one bounded chart, and the page is fed through the same application query as the CLI.
/// The Avalonia views are compiled by the build; these tests stay headless.
/// </summary>
public sealed class HistoryTrendTests
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

    private static (Snapshot A, Snapshot B) Fixture()
    {
        Snapshot a = Snapshot.Create(
            "snap_a",
            T0,
            [Item("dotnet", "a", 1000), Item("dotnet", "b", 500), Item("go", "g", 2000)],
            [],
            ["dotnet", "go"]);

        Snapshot b = Snapshot.Create(
            "snap_b",
            T0.AddDays(1),
            [Item("dotnet", "a", 1000), Item("dotnet", "c", 300), Item("go", "g", 2000), Item("go", "b", 500)],
            [],
            ["dotnet", "go"]);

        return (a, b);
    }

    [Fact]
    public void The_trend_reflects_the_snapshot_deltas()
    {
        (Snapshot a, Snapshot b) = Fixture();

        HistoryTrend trend = HistoryTrend.From(SnapshotHistory.Build([a, b]));

        Assert.Equal(2, trend.Points.Count);
        Assert.Equal(2, trend.Recent.Count);

        // Newest first for the list; oldest first for the chart.
        Assert.Equal("snap_b", trend.Recent[0].SnapshotId);
        Assert.Equal("snap_a", trend.Points[0].SnapshotId);
        Assert.True(trend.Points[0].IsBaseline);

        HistoryTrendPoint latest = trend.Points[1];
        Assert.Equal(1, latest.ItemDelta);
        Assert.Equal(300L, latest.ByteDelta);
        Assert.Equal("+1 item(s), +300 B", latest.DeltaLabel);
        Assert.True(latest.IsGrowth);
        Assert.False(latest.IsDecline);
        Assert.Equal("4 item(s)", latest.ItemLabel);
        Assert.Equal("3.7 KB", latest.TotalLabel);

        Assert.Equal("-200 B", Assert.Single(latest.Ecosystems, e => e.Ecosystem == "dotnet").Delta);
        Assert.Equal("+1 item(s), +500 B", Assert.Single(latest.Ecosystems, e => e.Ecosystem == "go").Delta);
        Assert.Equal("dotnet · -200 B", Assert.Single(latest.Ecosystems, e => e.Ecosystem == "dotnet").Label);

        Assert.Contains("Since the previous scan", trend.LatestSummary, StringComparison.Ordinal);
        Assert.Equal(120, latest.ByteBarHeight);
        Assert.True(latest.ItemBarHeight > 0);
    }

    [Fact]
    public void Twelve_snapshots_produce_one_bounded_chart_without_gaps()
    {
        Snapshot[] snapshots = [.. Enumerable.Range(0, 12).Select(index =>
            Snapshot.Create(
                $"snap_{index:D2}",
                T0.AddDays(index),
                [.. Enumerable.Range(0, index + 1).Select(item => Item("dotnet", $"pkg{item}", (ulong)(1000 * (item + 1))))],
                [],
                ["dotnet"]))];

        HistoryTrend trend = HistoryTrend.From(SnapshotHistory.Build(snapshots));

        Assert.Equal(12, trend.Points.Count);
        Assert.Equal(12, trend.Recent.Count);
        Assert.Equal("snap_11", trend.Recent[0].SnapshotId);
        Assert.Equal("snap_00", trend.Points[0].SnapshotId);

        Assert.All(trend.Points, point =>
        {
            Assert.InRange(point.ByteBarHeight, 0, HistoryTrend.MaxBarHeight);
            Assert.InRange(point.ItemBarHeight, 0, HistoryTrend.MaxBarHeight);
            Assert.False(string.IsNullOrWhiteSpace(point.CapturedAt));
            Assert.False(string.IsNullOrWhiteSpace(point.ShortDate));
            Assert.False(string.IsNullOrWhiteSpace(point.DeltaLabel));
            Assert.Single(point.Ecosystems);
        });

        // Scaling is per metric and across the whole window, so the newest point is the tallest and
        // the chart cannot outgrow its 120px budget.
        Assert.Equal(HistoryTrend.MaxBarHeight, trend.Points[^1].ByteBarHeight);
        Assert.Equal(HistoryTrend.MaxBarHeight, trend.Points[^1].ItemBarHeight);

        // Growth is monotonic here, so no bar shrinks as the window advances.
        for (int i = 1; i < trend.Points.Count; i++)
        {
            Assert.True(trend.Points[i].ByteBarHeight >= trend.Points[i - 1].ByteBarHeight);
            Assert.True(trend.Points[i].ItemBarHeight >= trend.Points[i - 1].ItemBarHeight);
        }
    }

    [Fact]
    public void An_empty_history_has_a_first_run_message_and_no_bars()
    {
        HistoryTrend trend = HistoryTrend.From(SnapshotHistory.Build([]));

        Assert.True(trend.IsEmpty);
        Assert.Equal(0, trend.SnapshotCount);
        Assert.Empty(trend.Points);
        Assert.Empty(trend.Recent);
        Assert.Contains("No snapshots yet", trend.Summary, StringComparison.Ordinal);
        Assert.Contains("baseline", trend.LatestSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_view_model_loads_the_same_history_the_cli_reports()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var runner = new FakeProcessRunner();
        var state = new FakeStateStore();
        var log = new FakeAppendLog();
        var clock = new FakeClock(T0);

        (Snapshot a, Snapshot b) = Fixture();
        var store = new SnapshotStore(state, clock);
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        EcosystemRegistry registry = EcosystemRegistry.CreateDefault(fs);
        QuarantineServices quarantine = ApplyWiring.CreateQuarantine(fs, env, state, log, clock);

        using GuiServices services = new(
            registry,
            new DoctorService(fs, runner, env, OperatingSystemKind.Linux, registry),
            new ScanService(fs, runner, env, state, clock, registry),
            new SnapshotLibrary(state, clock),
            new PlanApplyService(fs, runner, env, state, log, clock, registry),
            new QuarantineLibrary(fs, quarantine.Index, quarantine.Restore));

        var viewModel = new MainWindowViewModel(services, GuiPreferences.Load(new FakeShellPreferencesStore()));

        await viewModel.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasHistory);
        Assert.Equal(2, viewModel.HistoryCount);
        Assert.Equal("2 snapshot(s)", viewModel.HistoryDetail);
        Assert.Equal(2, viewModel.HistoryRows.Count);
        Assert.Equal("snap_b", viewModel.HistoryRows[0].SnapshotId);
        Assert.Equal(1, viewModel.HistoryPoints[1].ItemDelta);
        Assert.Equal(300L, viewModel.HistoryPoints[1].ByteDelta);
        Assert.Contains("Since the previous scan", viewModel.HistoryLatest, StringComparison.Ordinal);
    }
}

using System.Text.Json;
using Coppice.Application;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Snapshots;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Cli;

/// <summary>
/// T-041 acceptance for the <c>history</c> verb: exact deltas in the text summary, a stable JSON shape,
/// and a deterministic document across runs.
/// </summary>
public sealed class HistoryCommandTests
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

    private static async Task<FakeStateStore> SeededStateAsync()
    {
        var state = new FakeStateStore();
        var store = new SnapshotStore(state, new FakeClock(T0));

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

        await store.SaveAsync(a);
        await store.SaveAsync(b);
        return state;
    }

    [Fact]
    public async Task The_text_summary_reports_totals_and_the_delta_from_the_previous_scan()
    {
        FakeStateStore state = await SeededStateAsync();

        var captured = new StringWriter();
        int exit = new HistoryCommand(state, output: captured).Run([]);
        string output = captured.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("2 snapshot(s), oldest first", output, StringComparison.Ordinal);
        Assert.Contains("baseline", output, StringComparison.Ordinal);
        Assert.Contains("+1 item(s), +300 B", output, StringComparison.Ordinal);
        Assert.Contains("dotnet", output, StringComparison.Ordinal);
        Assert.Contains("\n    dotnet", output, StringComparison.Ordinal);
        Assert.Contains("-200 B", output, StringComparison.Ordinal);
        Assert.Contains("go", output, StringComparison.Ordinal);
        Assert.Contains("+500 B", output, StringComparison.Ordinal);
        Assert.Contains("Since snap_a: +1 item(s), +300 B", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_json_shape_is_versioned_and_carries_exact_numbers()
    {
        FakeStateStore state = await SeededStateAsync();

        var captured = new StringWriter();
        int exit = new HistoryCommand(state, output: captured).Run(["--json"]);
        string output = captured.ToString();

        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Empty(root.GetProperty("skippedSnapshotIds").EnumerateArray());

        JsonElement[] snapshots = [.. root.GetProperty("snapshots").EnumerateArray()];
        Assert.Equal(2, snapshots.Length);

        Assert.Equal("snap_a", snapshots[0].GetProperty("snapshotId").GetString());
        Assert.Equal(JsonValueKind.Null, snapshots[0].GetProperty("previousSnapshotId").ValueKind);
        Assert.Equal(0, snapshots[0].GetProperty("itemDelta").GetInt32());
        Assert.Equal(0, snapshots[0].GetProperty("byteDelta").GetInt64());

        JsonElement latest = snapshots[1];
        Assert.Equal("snap_b", latest.GetProperty("snapshotId").GetString());
        Assert.Equal("snap_a", latest.GetProperty("previousSnapshotId").GetString());
        Assert.Equal(4, latest.GetProperty("itemCount").GetInt32());
        Assert.Equal(3800UL, latest.GetProperty("totalBytes").GetUInt64());
        Assert.Equal(1, latest.GetProperty("itemDelta").GetInt32());
        Assert.Equal(300L, latest.GetProperty("byteDelta").GetInt64());

        JsonElement[] ecosystems = [.. latest.GetProperty("ecosystems").EnumerateArray()];
        Assert.Equal(["dotnet", "go"], ecosystems.Select(e => e.GetProperty("ecosystem").GetString()));
        Assert.Equal(-200L, ecosystems[0].GetProperty("byteDelta").GetInt64());
        Assert.Equal(500L, ecosystems[1].GetProperty("byteDelta").GetInt64());

        string[] removed = [.. latest.GetProperty("removedItemIds").EnumerateArray().Select(id => id.GetString()!)];
        string[] added = [.. latest.GetProperty("newItemIds").EnumerateArray().Select(id => id.GetString()!)];
        Assert.Single(removed);
        Assert.Equal(2, added.Length);
        Assert.Equal(added.Order(StringComparer.Ordinal), added);
    }

    [Fact]
    public async Task The_json_output_is_byte_identical_across_runs()
    {
        FakeStateStore state = await SeededStateAsync();

        var firstWriter = new StringWriter();
        new HistoryCommand(state, output: firstWriter).Run(["--json"]);
        var secondWriter = new StringWriter();
        new HistoryCommand(state, output: secondWriter).Run(["--json"]);

        Assert.Equal(firstWriter.ToString(), secondWriter.ToString());
    }

    [Fact]
    public async Task The_limit_bounds_the_window_and_is_inclusive()
    {
        FakeStateStore state = await SeededStateAsync();

        var captured = new StringWriter();
        int exit = new HistoryCommand(state, output: captured).Run(["--json", "--limit", "1"]);
        string output = captured.ToString();

        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement snapshot = Assert.Single(document.RootElement.GetProperty("snapshots").EnumerateArray());
        Assert.Equal("snap_b", snapshot.GetProperty("snapshotId").GetString());

        // The window is one scan, so it has no earlier scan inside it to compare with. Reporting a
        // fabricated +4 items here would be worse than saying "baseline".
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("previousSnapshotId").ValueKind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void A_non_positive_or_unparseable_limit_is_a_usage_error(string limit)
    {
        FakeStateStore state = new();

        Assert.Throws<ArgumentException>(() => new HistoryCommand(state).Run(["--limit", limit]));
    }

    [Fact]
    public void Unknown_flags_are_rejected_rather_than_ignored()
    {
        Assert.Throws<ArgumentException>(() => new HistoryCommand(new FakeStateStore()).Run(["--since", "yesterday"]));
    }

    [Fact]
    public void An_empty_store_says_so_without_failing()
    {
        var captured = new StringWriter();
        int exit = new HistoryCommand(new FakeStateStore(), output: captured).Run([]);
        string output = captured.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("No snapshots yet", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_application_query_and_the_report_agree_on_the_latest_delta()
    {
        FakeStateStore state = await SeededStateAsync();

        SnapshotHistory history = await new SnapshotLibrary(state, new FakeClock(T0)).LoadHistoryAsync(12);

        Assert.Equal(1, history.Latest!.Diff.ItemDelta);
        Assert.Contains("+1 item(s), +300 B", HistoryReport.ToText(history), StringComparison.Ordinal);
    }
}

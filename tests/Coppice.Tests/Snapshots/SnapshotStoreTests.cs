using Coppice.Core.Domain;
using Coppice.Core.Snapshots;
using Coppice.Tests.Support;

namespace Coppice.Tests.Snapshots;

/// <summary>
/// Card T-009 acceptance: round-trip, schema-version rejection, and pruning.
/// Uses the in-memory state fake so nothing touches a real state directory.
/// </summary>
public sealed class SnapshotStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 12, 10, 15, 0, TimeSpan.Zero);

    private static FakeStateStore State() => new();

    private static SnapshotStore Store(FakeStateStore state, DateTimeOffset? now = null) =>
        new(state, new FakeClock(now ?? T0));

    private static Item[] SampleItems() =>
    [
        new("dotnet", "package", "Newtonsoft.Json", "13.0.3", "nuget-packages", @"/cache/newtonsoft.json/13.0.3", 4_718_592, Risk.Safe, Facts.From([new("framework", "net10.0")])),
        new("dotnet", "package", "Serilog", "4.0.0", "nuget-packages", @"/cache/serilog/4.0.0", 1_048_576, Risk.Review, Facts.Empty),
    ];

    private static ScanIssue[] SampleIssues() => [];

    [Fact]
    public async Task A_snapshot_round_trips_losslessly()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        Snapshot saved = await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"]);
        SnapshotLoadResult loaded = await store.LoadAsync(saved.SnapshotId);

        Assert.True(loaded.Ok, loaded.Message);
        Assert.Equal(saved.SnapshotId, loaded.Snapshot!.SnapshotId);
        Assert.Equal(saved.CreatedAtUtc, loaded.Snapshot.CreatedAtUtc);
        Assert.Equal(saved.Items, loaded.Snapshot.Items);
        Assert.Equal(saved.Issues, loaded.Snapshot.Issues);
        Assert.Equal(saved.Ecosystems, loaded.Snapshot.Ecosystems);
        Assert.Equal(saved.Usage, loaded.Snapshot.Usage);
        Assert.Equal(saved.Checksum, loaded.Snapshot.Checksum);
    }

    [Fact]
    public async Task Items_are_stored_in_deterministic_order()
    {
        // NFR-06 applies to snapshots too: two runs over the same items must produce the same bytes.
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        Item[] reversed = [.. SampleItems().Reverse()];
        Snapshot a = await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], "snap_a");
        Snapshot b = await store.CaptureAsync(reversed, SampleIssues(), ["dotnet"], "snap_b");

        Assert.Equal(
            [.. a.Items.Select(i => i.Name)],
            [.. b.Items.Select(i => i.Name)]);
    }

    [Fact]
    public async Task Loading_an_unknown_snapshot_reports_not_found()
    {
        SnapshotLoadResult result = await Store(State()).LoadAsync("snap_does_not_exist");

        Assert.False(result.Ok);
        Assert.Equal(SnapshotLoadFailure.NotFound, result.Failure);
    }

    [Fact]
    public async Task A_future_schema_version_is_refused_not_guessed()
    {
        // The critical case: a snapshot written by a NEWER build. Guessing at a shape we cannot
        // parse is how a plan ends up justified by numbers that were never measured.
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        Snapshot future = Snapshot.Create("snap_future", T0, SampleItems(), SampleIssues(), ["dotnet"]) with
        {
            SchemaVersion = SnapshotStore.CurrentSchemaVersion + 1,
        };

        await store.SaveAsync(future);
        SnapshotLoadResult result = await store.LoadAsync("snap_future");

        Assert.False(result.Ok);
        Assert.Equal(SnapshotLoadFailure.UnsupportedSchemaVersion, result.Failure);
        Assert.Contains("not loaded rather than guessed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_schema_version_is_also_refused()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        Snapshot old = Snapshot.Create("snap_old", T0, SampleItems(), SampleIssues(), ["dotnet"]) with
        {
            SchemaVersion = 0,
        };

        await store.SaveAsync(old);
        Assert.Equal(SnapshotLoadFailure.UnsupportedSchemaVersion, (await store.LoadAsync("snap_old")).Failure);
    }

    [Fact]
    public async Task A_tampered_snapshot_fails_its_checksum()
    {
        // FR-11: a plan built from a tampered snapshot is a plan justified by numbers nobody measured.
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        Snapshot saved = await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"]);

        string json = DomainJson.Serialize(saved);
        string tampered = json.Replace("\"size\":4718592", "\"size\":9999999");
        await state.WriteAsync("snapshots/" + saved.SnapshotId + ".json", "application/json", tampered);

        SnapshotLoadResult result = await store.LoadAsync(saved.SnapshotId);

        Assert.False(result.Ok);
        Assert.Equal(SnapshotLoadFailure.ChecksumMismatch, result.Failure);
    }

    [Fact]
    public async Task A_malformed_snapshot_is_reported_not_thrown()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);
        await state.WriteAsync("snapshots/snap_broken.json", "application/json", "{ this is not json");

        SnapshotLoadResult result = await store.LoadAsync("snap_broken");

        Assert.False(result.Ok);
        Assert.Equal(SnapshotLoadFailure.Malformed, result.Failure);
    }

    [Fact]
    public async Task Pruning_keeps_the_newest_n_and_returns_the_pruned_ids()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        for (int i = 0; i < 5; i++)
        {
            await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], Snapshot.FormatId(T0.AddDays(i)));
        }

        IReadOnlyList<string> pruned = await store.PruneAsync(keep: 2);

        // Ids are listed newest-first, so the two newest survive.
        Assert.Equal(
            [Snapshot.FormatId(T0.AddDays(4)), Snapshot.FormatId(T0.AddDays(3))],
            store.ListIds());

        // The pruned ids are RETURNED, not just deleted: a caller can tell the user exactly which
        // history is gone. Silently discarding it is how a user loses the baseline they needed.
        // keep=2 keeps the two newest, so the three OLDEST are the ones pruned.
        Assert.Equal(
            [Snapshot.FormatId(T0.AddDays(2)), Snapshot.FormatId(T0.AddDays(1)), Snapshot.FormatId(T0)],
            pruned);
    }

    [Fact]
    public async Task Pruning_fewer_than_keep_n_removes_nothing()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);
        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], "snap_a");
        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], "snap_b");

        IReadOnlyList<string> pruned = await store.PruneAsync(keep: 5);

        Assert.Empty(pruned);
        Assert.Equal(2, store.ListIds().Count);
    }

    [Fact]
    public async Task Prune_rejects_a_keep_of_zero()
    {
        // keep=0 would delete the entire history. That is not a tuning mistake, it is data loss.
        SnapshotStore store = Store(State());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PruneAsync(keep: 0));
    }

    [Fact]
    public async Task Ids_are_listed_newest_first()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);

        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], "snap_20240101T000000Z");
        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], "snap_20250101T000000Z");
        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], "snap_20230101T000000Z");

        Assert.Equal(
            ["snap_20250101T000000Z", "snap_20240101T000000Z", "snap_20230101T000000Z"],
            store.ListIds());
    }

    [Fact]
    public async Task LoadLatest_returns_the_newest()
    {
        FakeStateStore state = State();
        SnapshotStore store = Store(state);
        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], Snapshot.FormatId(T0));
        await store.CaptureAsync(SampleItems(), SampleIssues(), ["dotnet"], Snapshot.FormatId(T0.AddDays(1)));

        SnapshotLoadResult latest = await store.LoadLatestAsync();

        Assert.True(latest.Ok);
        Assert.Equal(Snapshot.FormatId(T0.AddDays(1)), latest.Snapshot!.SnapshotId);
    }

    [Fact]
    public async Task LoadLatest_on_an_empty_store_reports_not_found()
    {
        SnapshotLoadResult latest = await Store(State()).LoadLatestAsync();

        Assert.False(latest.Ok);
        Assert.Equal(SnapshotLoadFailure.NotFound, latest.Failure);
    }

    [Fact]
    public void Snapshot_ids_sort_as_timestamps_and_are_filename_safe()
    {
        string early = Snapshot.FormatId(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        string late = Snapshot.FormatId(new DateTimeOffset(2025, 6, 1, 12, 30, 0, TimeSpan.Zero));

        Assert.Equal("snap_20250101T000000Z", early);
        Assert.True(string.CompareOrdinal(early, late) < 0);
        Assert.DoesNotContain(':', early);
        Assert.DoesNotContain('/', early);
    }

    [Fact]
    public void A_snapshot_reports_its_own_tampering()
    {
        Snapshot snapshot = Snapshot.Create("snap_x", T0, SampleItems(), SampleIssues(), ["dotnet"]);

        Assert.True(snapshot.IsIntact());

        // Content changes break the checksum...
        Assert.False((snapshot with { Usage = new UsageBreakdown(1, 0, 0) }).IsIntact());

        // ...but the id and timestamp are metadata about the capture, not the state it recorded,
        // so re-snapshotting identical content under a new id stays verifiable.
        Assert.True((snapshot with { SnapshotId = "snap_y" }).IsIntact());
    }

    [Fact]
    public void Two_snapshots_of_identical_content_have_identical_checksums()
    {
        Snapshot a = Snapshot.Create("snap_a", T0, SampleItems(), SampleIssues(), ["dotnet"]);
        Snapshot b = Snapshot.Create("snap_b", T0, SampleItems(), SampleIssues(), ["dotnet"]);

        // Checksum covers content, not the id, so a re-scan of an unchanged machine is recognizable
        // as unchanged — which is what a trend report needs.
        Assert.Equal(a.Checksum, b.Checksum);
    }
}

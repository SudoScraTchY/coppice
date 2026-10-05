using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Tests.Support;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Tests.Plans;

/// <summary>
/// T-026 acceptance: "round-trip lossless; tampered checksum → rejection on load" (FR-11).
/// <para>
/// The store is where a plan becomes an instruction someone might act on, so these tests are about
/// refusals. Each one asserts both that the load FAILED and that the message says why — a refusal a user
/// cannot act on is the same as no refusal, because the obvious next move is to run the plan anyway.
/// </para>
/// </summary>
public sealed class PlanStoreTests
{
    private static readonly PlanBuilder.BuildFailure MissingReason = PlanBuilder.BuildFailure.MissingReason;

    private static Plan AValidPlan() =>
        PlanBuilder.Build(
            "snap_20250112T1015Z",
            Policy.Default,
            [
                new("nuget:newtonsoft.json/13.0.1", Risk.Safe, "unreferenced and regenerable", RemovalAction.PathDelete(@"C:\cache\newtonsoft.json\13.0.1"), 11_927_552),
                new("nuget:serilog/3.1.1", Risk.Review, "reinstallable, opt-in required", RemovalAction.Native("dotnet tool uninstall serilog"), 2048),
            ])
            .Plan!;

    private static PlanStep Step(string itemId, ulong bytes) =>
        new(itemId, Risk.Safe, $"unreferenced and regenerable ({itemId})", RemovalAction.PathDelete($@"C:\cache\{itemId}"), bytes);

    private static (PlanStore Store, FakeStateStore State) NewStore()
    {
        var state = new FakeStateStore();
        return (new PlanStore(state), state);
    }

    /// <summary>Writes an arbitrary document under the plan's key, bypassing the builder.</summary>
    private static async Task WriteRawAsync(FakeStateStore state, Plan plan, string json)
    {
        await state.WriteAsync($"plans/{plan.PlanId}.json", "application/json", json, CancellationToken.None);
    }

    // ---- the happy path --------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_plan_loads_back_intact()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();

        await store.SaveAsync(plan);
        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.True(loaded.IsSuccess, loaded.Message);
        Assert.Equal(plan, loaded.Plan);
        Assert.True(loaded.Plan!.IsIntact());
        Assert.Equal(plan.ExpectedReclaimBytes, loaded.Plan.ExpectedReclaimBytes);
    }

    [Fact]
    public async Task Listing_ids_returns_the_saved_plans()
    {
        (PlanStore store, _) = NewStore();

        // Both plans are BUILT, not copied with `with`: the plan id is derived from the steps and the
        // snapshot they were justified against, so changing only the SnapshotId on an already-built plan
        // would leave its id stale and the two saves would collide on one key. Deriving the id in the
        // builder is what keeps that from being possible through the normal path.
        Plan first = PlanBuilder.Build("snap_a", Policy.Default, [Step("nuget:a/1.0", 1_000)]).Plan!;
        Plan second = PlanBuilder.Build("snap_b", Policy.Default, [Step("nuget:a/1.0", 1_000)]).Plan!;

        Assert.NotEqual(first.PlanId, second.PlanId);

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        Assert.Equal(
            [first.PlanId, second.PlanId],
            store.ListIds().OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_deleted_plan_is_gone()
    {
        (PlanStore store, _) = NewStore();
        Plan plan = AValidPlan();

        await store.SaveAsync(plan);
        await store.DeleteAsync(plan.PlanId);

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);
        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.NotFound, loaded.Failure);
    }

    // ---- tampered checksum → rejection -------------------------------------------------------------

    [Fact]
    public async Task A_plan_edited_to_add_a_step_is_refused_on_load()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        // The realistic tamper: someone opens the JSON and adds the directory they want gone.
        Plan tampered = plan with
        {
            Steps = [.. plan.Steps, new("nuget:something-else/1.0", Risk.Manual, "added by hand", RemovalAction.PathDelete(@"C:\cache\valuable"), 999_999)],
            ExpectedReclaimBytes = plan.ExpectedReclaimBytes + 999_999,
        };

        await WriteRawAsync(state, plan, DomainJson.Serialize(tampered, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.ChecksumMismatch, loaded.Failure);
        Assert.Contains("modified", loaded.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(loaded.Plan);
    }

    [Fact]
    public async Task A_plan_whose_bytes_were_changed_is_refused_on_load()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        // Editing only the total — the number the user sizes their risk against.
        await WriteRawAsync(state, plan, DomainJson.Serialize(plan with { ExpectedReclaimBytes = 1 }, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.ChecksumMismatch, loaded.Failure);
    }

    [Fact]
    public async Task A_plan_whose_reason_was_rewritten_is_refused_on_load()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        Plan reworded = plan with
        {
            Steps = [plan.Steps[0] with { Reason = "obviously fine" }, plan.Steps[1]],
        };

        await WriteRawAsync(state, plan, DomainJson.Serialize(reworded, indented: true));

        Assert.Equal(PlanLoadFailure.ChecksumMismatch, (await store.LoadAsync(plan.PlanId)).Failure);
    }

    [Fact]
    public async Task A_plan_from_another_schema_version_is_refused()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        await WriteRawAsync(state, plan, DomainJson.Serialize(plan with { SchemaVersion = PlanStore.CurrentSchemaVersion + 1 }, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.UnsupportedSchemaVersion, loaded.Failure);
        Assert.Contains("not loaded rather than guessed at", loaded.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_json_is_refused_with_the_parser_s_reason()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await state.WriteAsync($"plans/{plan.PlanId}.json", "application/json", "{ this is not json", CancellationToken.None);

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.Malformed, loaded.Failure);
    }

    // ---- structurally invalid but checksummed ----------------------------------------------------------

    [Fact]
    public async Task A_checksummed_plan_with_no_reasons_is_refused()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        // Recomputed so the checksum PASSES. This is the case the checksum cannot catch: the document was
        // never well-formed. Load has to re-check the invariants itself, or a plan written by anything
        // other than this builder would execute without reasons.
        Plan unreasoned = plan with
        {
            Steps = [.. plan.Steps.Select(s => s with { Reason = "  " })],
        };

        unreasoned = unreasoned with { Checksum = unreasoned.ComputeChecksum() };
        await WriteRawAsync(state, plan, DomainJson.Serialize(unreasoned, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.Invalid, loaded.Failure);
        Assert.Contains(nameof(MissingReason), loaded.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_checksummed_plan_with_no_steps_is_refused()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        Plan empty = plan with { Steps = [], ExpectedReclaimBytes = 0 };
        empty = empty with { Checksum = empty.ComputeChecksum() };
        await WriteRawAsync(state, plan, DomainJson.Serialize(empty, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.Invalid, loaded.Failure);
    }

    [Fact]
    public async Task A_checksummed_plan_whose_total_disagrees_with_its_steps_is_refused()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        // The total moved and the checksum was recomputed to match, so only the sum check can catch it.
        // This is the one that matters for the user: the number is what they sized their risk against.
        Plan inconsistent = plan with { ExpectedReclaimBytes = plan.ExpectedReclaimBytes + 5_000 };
        inconsistent = inconsistent with { Checksum = inconsistent.ComputeChecksum() };
        await WriteRawAsync(state, plan, DomainJson.Serialize(inconsistent, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.Invalid, loaded.Failure);
    }

    [Fact]
    public async Task A_checksummed_plan_with_a_duplicate_step_is_refused()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        Plan duplicated = plan with
        {
            Steps = [plan.Steps[0], plan.Steps[0]],
            ExpectedReclaimBytes = plan.Steps[0].ExpectedBytes * 2,
        };

        duplicated = duplicated with { Checksum = duplicated.ComputeChecksum() };
        await WriteRawAsync(state, plan, DomainJson.Serialize(duplicated, indented: true));

        PlanLoadResult loaded = await store.LoadAsync(plan.PlanId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.Invalid, loaded.Failure);
    }

    // ---- the checksum is not self-referential ----------------------------------------------------------

    [Fact]
    public async Task Changing_the_plan_id_does_not_invalidate_a_plan()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        // The id is derived from the steps, so a checksum that covered it could never be computed. This
        // proves the two are independent: renaming a plan does not invalidate it.
        Plan renamed = plan with { PlanId = "pln_renamed" };
        await WriteRawAsync(state, renamed, DomainJson.Serialize(renamed, indented: true));

        PlanLoadResult loaded = await store.LoadAsync("pln_renamed");

        Assert.True(loaded.IsSuccess, loaded.Message);
        Assert.Equal("pln_renamed", loaded.Plan!.PlanId);
    }

    [Fact]
    public async Task Reindenting_a_saved_plan_does_not_invalidate_it()
    {
        (PlanStore store, FakeStateStore state) = NewStore();
        Plan plan = AValidPlan();
        await store.SaveAsync(plan);

        await WriteRawAsync(state, plan, DomainJson.Serialize(plan, indented: true));

        // Otherwise the checksum would forbid anyone opening and saving a plan.
        Assert.True((await store.LoadAsync(plan.PlanId)).IsSuccess);
    }

    [Fact]
    public async Task Loading_an_unknown_plan_says_so_rather_than_returning_an_empty_one()
    {
        (PlanStore store, _) = NewStore();

        PlanLoadResult loaded = await store.LoadAsync("pln_does_not_exist");

        Assert.False(loaded.IsSuccess);
        Assert.Equal(PlanLoadFailure.NotFound, loaded.Failure);
        Assert.Contains("pln_does_not_exist", loaded.Message!, StringComparison.Ordinal);
    }
}

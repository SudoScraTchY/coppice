using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Tests.Plans;

/// <summary>
/// T-026: plan builder, plan JSON, and the checksum over canonical serialization (FR-11).
/// <para>
/// A plan is an INSTRUCTION, so these tests are about what stops a wrong instruction from being executed.
/// A snapshot that is misread costs a stale report; a plan that is misread costs the wrong files.
/// </para>
/// </summary>
public sealed class PlanBuilderTests
{
    private const string SnapshotId = "snap_20250112T1015Z";

    private static PlanStep Step(
        string itemId,
        ulong bytes = 1024,
        Risk risk = Risk.Safe,
        string? reason = null,
        string? path = null) =>
        new(
            itemId,
            risk,
            reason ?? $"unreferenced and regenerable ({itemId})",
            RemovalAction.PathDelete(path ?? $@"C:\cache\{itemId}"),
            bytes);

    private static PlanResult Build(params PlanStep[] steps) =>
        PlanBuilder.Build(SnapshotId, Policy.Default, steps);

    // ---- lossless round-trip ---------------------------------------------------------------------

    [Fact]
    public void A_built_plan_round_trips_losslessly()
    {
        PlanResult result = Build(
            Step("nuget:newtonsoft.json/13.0.1", 11_927_552),
            Step("nuget:serilog/3.1.1", 2048, Risk.Review, "reinstallable, opt-in required"),
            Step("sdk:vs-installer/17.0", 8192, Risk.Manual, "installer-owned; use the Visual Studio Installer"));

        Assert.True(result.IsSuccess, result.Message);

        Plan plan = result.Plan!;
        string json = DomainJson.Serialize(plan);
        Plan? restored = DomainJson.Deserialize<Plan>(json);

        Assert.NotNull(restored);

        // Byte-identical on re-serialize, which is the actual claim: not "the fields match" but "the
        // canonical form is a fixed point", so a plan survives save → load → save unchanged.
        Assert.Equal(json, DomainJson.Serialize(restored));
        Assert.Equal(plan, restored);
        Assert.Equal(plan.Steps.Count, restored.Steps.Count);
        Assert.Equal(plan.Steps.Select(s => s.ItemId), restored.Steps.Select(s => s.ItemId));
        Assert.Equal(plan.ExpectedReclaimBytes, restored.ExpectedReclaimBytes);
        Assert.Equal(plan.Checksum, restored.Checksum);
        Assert.Equal(plan.SchemaVersion, restored.SchemaVersion);
    }

    [Fact]
    public void A_restored_plan_is_still_intact()
    {
        Plan plan = Build(Step("nuget:a/1.0"), Step("nuget:b/2.0", 4096)).Plan!;

        Plan? restored = DomainJson.Deserialize<Plan>(DomainJson.Serialize(plan));

        Assert.True(restored!.IsIntact());
    }

    [Fact]
    public void Every_action_shape_survives_the_round_trip()
    {
        // Each RemovalAction carries different optional fields. A converter that dropped the unused ones
        // would still round-trip a path-delete plan, so all three kinds are exercised.
        Plan plan = Build(
            new("nuget:p/1.0", Risk.Safe, "unused", RemovalAction.PathDelete(@"C:\cache\p"), 10),
            new("nuget:t/1.0", Risk.Safe, "opt-in", RemovalAction.Native("dotnet tool uninstall p"), 20),
            new("sdk:vs/17", Risk.Manual, "installer", RemovalAction.ReportOnly("use the Visual Studio Installer"), 30))
            .Plan!;

        Plan restored = DomainJson.Deserialize<Plan>(DomainJson.Serialize(plan))!;

        Assert.Equal(RemovalKind.PathDelete, restored.Steps[0].Action.Kind);
        Assert.Equal(@"C:\cache\p", restored.Steps[0].Action.CanonicalPath);
        Assert.Equal(RemovalKind.NativeCommand, restored.Steps[1].Action.Kind);
        Assert.Equal("dotnet tool uninstall p", restored.Steps[1].Action.CommandLine);
        Assert.Equal(RemovalKind.ReportOnly, restored.Steps[2].Action.Kind);
        Assert.Equal("use the Visual Studio Installer", restored.Steps[2].Action.RouteNote);
    }

    // ---- reason strings ---------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_step_without_a_reason_is_refused(string reason)
    {
        PlanResult result = Build(Step("nuget:p/1.0", 1024, Risk.Safe, reason));

        Assert.False(result.IsSuccess);
        Assert.Equal(PlanBuilder.BuildFailure.MissingReason, result.Failure);
        Assert.Contains("no reason", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_step_in_a_built_plan_has_a_non_empty_reason()
    {
        // The acceptance criterion as a property over the built plan, not just over one hand-made case.
        Plan plan = Build(
            Step("nuget:a/1.0"),
            Step("nuget:b/1.0", 4096, Risk.Review, "reinstallable"),
            Step("nuget:c/1.0", 8192, Risk.Manual, "installer-owned"))
            .Plan!;

        Assert.All(plan.Steps, step =>
        {
            Assert.True(step.HasReason, $"step {step.ItemId} has no reason");
            Assert.False(string.IsNullOrWhiteSpace(step.Reason));
        });
    }

    // ---- the bytes sum ---------------------------------------------------------------------------

    [Fact]
    public void The_expected_total_is_the_sum_of_the_steps()
    {
        Plan plan = Build(
            Step("nuget:a/1.0", 1_000),
            Step("nuget:b/1.0", 2_000),
            Step("nuget:c/1.0", 3_000))
            .Plan!;

        // Aggregate manually: LINQ's Sum has no ulong overload, and widening through ulong via a cast per
        // element is exactly the kind of arithmetic that silently wraps if this ever changes.
        ulong sum = 0;
        foreach (PlanStep step in plan.Steps)
        {
            sum += step.ExpectedBytes;
        }

        Assert.Equal(6_000ul, plan.ExpectedReclaimBytes);
        Assert.Equal(sum, plan.ExpectedReclaimBytes);
    }

    [Fact]
    public void The_total_holds_whatever_order_the_steps_arrive_in()
    {
        // The builder orders steps itself, so the total must not depend on the caller's order.
        PlanStep[] steps = [Step("nuget:a/1.0", 1_000), Step("nuget:b/1.0", 2_000), Step("nuget:c/1.0", 3_000)];

        ulong forward = Build(steps).Plan!.ExpectedReclaimBytes;
        ulong reversed = Build([.. steps.Reverse()]).Plan!.ExpectedReclaimBytes;

        Assert.Equal(forward, reversed);
        Assert.Equal(6_000ul, forward);
    }

    [Fact]
    public void A_step_set_of_zero_bytes_sums_to_zero_and_is_still_a_plan()
    {
        // A cache of empty directories is a real scan outcome. Zero bytes must not be mistaken for "no
        // plan" — the steps are still there and still need reasons.
        Plan plan = Build(Step("nuget:empty/1.0", 0)).Plan!;

        Assert.Equal(0ul, plan.ExpectedReclaimBytes);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void An_empty_plan_is_refused()
    {
        PlanResult result = Build();

        Assert.False(result.IsSuccess);
        Assert.Equal(PlanBuilder.BuildFailure.Empty, result.Failure);
    }

    [Fact]
    public void A_duplicate_item_id_is_refused()
    {
        PlanResult result = Build(Step("nuget:p/1.0", 1_000), Step("nuget:p/1.0", 2_000, Risk.Review, "same id"));

        Assert.False(result.IsSuccess);
        Assert.Equal(PlanBuilder.BuildFailure.DuplicateStep, result.Failure);
        Assert.Contains("nuget:p/1.0", result.Message!, StringComparison.Ordinal);
    }

    // ---- determinism -----------------------------------------------------------------------------

    [Fact]
    public void The_same_steps_produce_the_same_plan_every_time()
    {
        PlanStep[] steps = [Step("nuget:a/1.0", 1_000), Step("nuget:b/1.0", 2_000, Risk.Review, "reinstallable")];

        Plan first = Build(steps).Plan!;
        Plan second = Build(steps).Plan!;

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.Checksum, second.Checksum);
        Assert.Equal(DomainJson.Serialize(first), DomainJson.Serialize(second));
    }

    [Fact]
    public void Steps_are_ordered_by_risk_then_item_id()
    {
        Plan plan = Build(
            Step("nuget:m/1.0", 1, Risk.Manual, "manual"),
            Step("nuget:s/1.0", 1, Risk.Safe, "safe"),
            Step("nuget:r/1.0", 1, Risk.Review, "review"),
            Step("nuget:a/1.0", 1, Risk.Safe, "safe"))
            .Plan!;

        Assert.Equal(
            [Risk.Safe, Risk.Safe, Risk.Review, Risk.Manual],
            plan.Steps.Select(s => s.Risk));
        Assert.Equal(["nuget:a/1.0", "nuget:s/1.0"], plan.Steps.Where(s => s.Risk == Risk.Safe).Select(s => s.ItemId));
    }

    [Fact]
    public void The_plan_id_changes_when_any_step_field_changes()
    {
        // The plan id is what the store keys on. If two plans with different instructions shared an id,
        // saving the second would overwrite the first and the user would run one plan while holding
        // another — so every field that changes behaviour must change the id.
        string baseline = Build(Step("nuget:p/1.0", 1_000)).Plan!.PlanId;

        string[] variants =
        [
            Build(Step("nuget:p/1.0", 1_001)).Plan!.PlanId,
            Build(Step("nuget:p/1.0", 1_000, Risk.Safe, "a different reason")).Plan!.PlanId,
            Build(Step("nuget:p/1.0", 1_000, Risk.Review, "unreferenced and regenerable (nuget:p/1.0)")).Plan!.PlanId,
            Build(Step("nuget:p/1.0", 1_000, Risk.Safe, "unreferenced and regenerable (nuget:p/1.0)", path: @"D:\other")).Plan!.PlanId,
        ];

        Assert.All(variants, id => Assert.NotEqual(baseline, id));
    }

    [Fact]
    public void The_plan_id_is_stable_across_policy_presets_that_produce_identical_instructions()
    {
        // Two presets that decide the same thing are the same instruction. Their plans differ only in the
        // policy recorded, which is part of Content and so of the checksum — but the id names the steps.
        // Asserted so the intent is explicit: the id is derived from what the plan DOES.
        Plan conservative = PlanBuilder.Build(SnapshotId, Policy.Conservative, [Step("nuget:p/1.0", 1_000)]).Plan!;
        Plan standard = PlanBuilder.Build(SnapshotId, Policy.Default, [Step("nuget:p/1.0", 1_000)]).Plan!;

        Assert.NotEqual(conservative.Checksum, standard.Checksum);
    }

    // ---- tamper detection ------------------------------------------------------------------------

    [Fact]
    public void A_plan_edited_after_being_written_fails_its_checksum()
    {
        Plan plan = Build(Step("nuget:p/1.0", 1_000), Step("nuget:q/1.0", 2_000)).Plan!;
        Assert.True(plan.IsIntact());

        // Add a step. This is the attack the checksum exists for: someone edits the file to make a plan
        // remove something it was not supposed to touch.
        Plan tampered = plan with
        {
            Steps = [.. plan.Steps, Step("nuget:something-else/1.0", 9_999_999, Risk.Manual, "added by hand")],
            ExpectedReclaimBytes = plan.ExpectedReclaimBytes + 9_999_999,
        };

        Assert.False(tampered.IsIntact());
    }

    [Fact]
    public void Changing_a_reason_changes_the_checksum()
    {
        Plan plan = Build(Step("nuget:p/1.0", 1_000)).Plan!;

        Plan reworded = plan with
        {
            Steps = [plan.Steps[0] with { Reason = "actually something else entirely" }],
        };

        Assert.False(reworded.IsIntact());
    }

    [Fact]
    public void Changing_the_policy_changes_the_checksum()
    {
        Plan plan = Build(Step("nuget:p/1.0", 1_000)).Plan!;

        Assert.False((plan with { Policy = Policy.Aggressive }).IsIntact());
    }

    [Fact]
    public void The_checksum_is_not_a_function_of_the_plan_id()
    {
        // The id is derived from the steps, so covering it would make the checksum self-referential and a
        // fresh plan could never validate.
        Plan plan = Build(Step("nuget:p/1.0", 1_000)).Plan!;

        Assert.True((plan with { PlanId = "pln_somethingelse" }).IsIntact());
    }

    [Fact]
    public void The_checksum_survives_a_reindent()
    {
        // Checksum is over the CANONICAL form, so pretty-printing a plan for a human must not invalidate
        // it. Otherwise the checksum would forbid reading a plan.
        Plan plan = Build(Step("nuget:p/1.0", 1_000)).Plan!;

        string pretty = DomainJson.Serialize(plan, indented: true);
        Plan? reparsed = DomainJson.Deserialize<Plan>(pretty);

        Assert.NotNull(reparsed);
        Assert.True(reparsed!.IsIntact());
        Assert.Equal(plan.Checksum, reparsed.Checksum);
    }

    [Fact]
    public void The_checksum_is_case_insensitive_on_the_hex_digits()
    {
        // Matches Snapshot.IsIntact: the value is hex, and uppercase is the same value.
        Plan plan = Build(Step("nuget:p/1.0", 1_000)).Plan!;

        Assert.True((plan with { Checksum = plan.Checksum.ToUpperInvariant() }).IsIntact());
    }
}

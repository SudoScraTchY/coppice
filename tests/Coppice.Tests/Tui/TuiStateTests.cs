using Coppice.Cli;
using Coppice.Cli.Tui;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Tui;

/// <summary>
/// T-036's keyboard contract, without a terminal (FR-20).
/// <para>
/// The state machine is where a key becomes a decision, so it is where "fully keyboard-operable" and
/// "cancel at any point is safe" are either true or not. The renderer and the real gateway are tested
/// separately; nothing here needs a console to run.
/// </para>
/// </summary>
public sealed class TuiStateTests
{
    [Fact]
    public void The_tab_order_is_the_documented_one_and_wraps()
    {
        TuiState state = TuiFixtures.State();

        Assert.Equal(
            [TuiScreen.Dashboard, TuiScreen.Roots, TuiScreen.Items, TuiScreen.Problems, TuiScreen.PlanReview, TuiScreen.Quarantine],
            TuiState.TabOrder);

        var visited = new List<TuiScreen> { state.Screen };
        for (int i = 0; i < TuiState.TabOrder.Count; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
            visited.Add(state.Screen);
        }

        Assert.Equal([.. TuiState.TabOrder, TuiScreen.Dashboard], visited);

        state.HandleKey(TuiFixtures.Tab(shift: true));
        Assert.Equal(TuiScreen.Quarantine, state.Screen);
    }

    [Fact]
    public void Every_screen_is_built_from_labeled_blocks_with_key_help()
    {
        TuiState state = TuiFixtures.State();
        var frames = new List<TuiFrame> { state.Frame() };

        for (int i = 1; i < TuiState.TabOrder.Count; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
            frames.Add(state.Frame());
        }

        Plan plan = TuiFixtures.PreviewPlan();
        state.BeginApply(plan.PlanId, TuiFixtures.Bound(plan));
        frames.Add(state.Frame());

        Assert.Equal(7, frames.Select(f => f.Screen).Distinct().Count());

        foreach (TuiFrame frame in frames)
        {
            Assert.NotEmpty(frame.Blocks);
            Assert.NotEmpty(frame.Keys);
            Assert.All(frame.Blocks, block => Assert.False(string.IsNullOrWhiteSpace(block.Label)));

            string plain = TuiPlain.Render(frame);
            foreach (TuiBlock block in frame.Blocks)
            {
                Assert.Contains($"[{block.Label}]", plain, StringComparison.Ordinal);
            }

            Assert.DoesNotContain('\u001b', plain);
        }
    }

    [Fact]
    public void Dashboard_enter_drills_into_the_selected_screen()
    {
        TuiState state = TuiFixtures.State();

        state.HandleKey(TuiFixtures.Enter());
        Assert.Equal(TuiScreen.Roots, state.Screen);

        TuiState items = TuiFixtures.State();
        items.HandleKey(TuiFixtures.Down());
        items.HandleKey(TuiFixtures.Enter());
        Assert.Equal(TuiScreen.Items, items.Screen);
    }

    [Fact]
    public void Items_filter_and_sort_are_deterministic()
    {
        TuiState state = TuiFixtures.State();
        state.HandleKey(TuiFixtures.Tab());
        state.HandleKey(TuiFixtures.Tab());
        Assert.Equal(TuiScreen.Items, state.Screen);

        // Default sort: size descending.
        Assert.Equal(600UL, state.FilteredItems()[0].Size);

        state.HandleKey(TuiFixtures.Letter('o'));
        Assert.Equal(["dotnet-tool", "newtonsoft.json", "newtonsoft.json", "newtonsoft.json", "polly", "serilog", "serilog", "serilog"],
            state.FilteredItems().Select(i => i.Name));

        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.All(state.FilteredItems(), item => Assert.Equal(Risk.Safe, item.Risk));
        Assert.Equal(7, state.FilteredItems().Count);

        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.Single(state.FilteredItems());
        Assert.Equal(Risk.Review, state.FilteredItems()[0].Risk);

        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.Empty(state.FilteredItems());

        // Referenced, then unreferenced, then unknown, then back to all.
        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.Equal("polly", Assert.Single(state.FilteredItems()).Name);

        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.Equal(7, state.FilteredItems().Count);

        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.Empty(state.FilteredItems());

        state.HandleKey(TuiFixtures.Letter('f'));
        Assert.Equal(8, state.FilteredItems().Count);
    }

    [Fact]
    public void Space_toggles_a_step_and_the_second_a_applies_only_the_kept_steps()
    {
        TuiState state = TuiFixtures.State();
        for (int i = 0; i < 4; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
        }

        Assert.Equal(TuiScreen.PlanReview, state.Screen);
        Plan full = TuiFixtures.PreviewPlan();
        Assert.Equal(2, full.Steps.Count);

        state.HandleKey(TuiFixtures.Space());
        TuiIntent armed = state.HandleKey(TuiFixtures.Letter('a'));
        Assert.Equal(TuiIntentKind.None, armed.Kind);
        Assert.Contains("again", state.Frame().Status, StringComparison.OrdinalIgnoreCase);

        TuiIntent apply = state.HandleKey(TuiFixtures.Letter('a'));

        Assert.Equal(TuiIntentKind.Apply, apply.Kind);
        Assert.Single(apply.Plan!.Steps);
        Assert.NotEqual(full.PlanId, apply.Plan.PlanId);
        Assert.True(apply.Plan.IsIntact());
    }

    [Fact]
    public void A_second_a_is_required_and_escape_disarms_it_before_leaving()
    {
        TuiState state = TuiFixtures.State();
        for (int i = 0; i < 4; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
        }

        state.HandleKey(TuiFixtures.Letter('a'));
        state.HandleKey(TuiFixtures.Escape());

        Assert.Equal(TuiScreen.PlanReview, state.Screen);
        Assert.Contains("not confirmed", state.Frame().Status, StringComparison.OrdinalIgnoreCase);

        state.HandleKey(TuiFixtures.Escape());
        Assert.Equal(TuiScreen.Dashboard, state.Screen);
    }

    [Fact]
    public void An_empty_selection_cannot_be_applied()
    {
        TuiState state = TuiFixtures.State();
        for (int i = 0; i < 4; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
        }

        state.HandleKey(TuiFixtures.Space());
        state.HandleKey(TuiFixtures.Down());
        state.HandleKey(TuiFixtures.Space());

        TuiIntent intent = state.HandleKey(TuiFixtures.Letter('a'));

        Assert.Equal(TuiIntentKind.None, intent.Kind);
        Assert.Contains("No steps", state.Frame().Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escape_during_apply_requests_cancel_and_never_quits()
    {
        TuiState state = TuiFixtures.State();
        Plan plan = TuiFixtures.PreviewPlan();
        state.BeginApply(plan.PlanId, TuiFixtures.Bound(plan));

        TuiIntent intent = state.HandleKey(TuiFixtures.Escape());

        Assert.Equal(TuiIntentKind.None, intent.Kind);
        Assert.True(state.CancelRequested);
        Assert.Equal(TuiScreen.ApplyProgress, state.Screen);
        Assert.Contains("cancel", state.Frame().Status, StringComparison.OrdinalIgnoreCase);

        state.FinishApply(new ApplySummary(ApplyStatus.Cancelled, "Cancelled before step 2 of 2.", []));
        state.HandleKey(TuiFixtures.Escape());

        Assert.Equal(TuiScreen.Dashboard, state.Screen);
    }

    [Fact]
    public void Ctrl_c_during_apply_is_a_cancel_not_a_quit()
    {
        TuiState state = TuiFixtures.State();
        Plan plan = TuiFixtures.PreviewPlan();
        state.BeginApply(plan.PlanId, TuiFixtures.Bound(plan));

        TuiIntent intent = state.HandleKey(TuiFixtures.CtrlC());

        Assert.Equal(TuiIntentKind.None, intent.Kind);
        Assert.True(state.CancelRequested);
    }

    [Fact]
    public void Quarantine_restore_needs_a_confirmation_key()
    {
        TuiState state = TuiFixtures.State();
        for (int i = 0; i < 5; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
        }

        Assert.Equal(TuiScreen.Quarantine, state.Screen);

        state.HandleKey(TuiFixtures.Letter('r'));
        Assert.Contains("Press y", state.Frame().Status, StringComparison.Ordinal);

        TuiIntent restore = state.HandleKey(TuiFixtures.Letter('y'));

        Assert.Equal(TuiIntentKind.Restore, restore.Kind);
        Assert.Equal("q0001", restore.EntryId);
    }

    [Fact]
    public void An_applied_plan_cannot_be_applied_again_from_the_same_session()
    {
        TuiState state = TuiFixtures.State();
        for (int i = 0; i < 4; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
        }

        Plan plan = TuiFixtures.PreviewPlan();
        state.BeginApply(plan.PlanId, TuiFixtures.Bound(plan));
        state.FinishApply(new ApplySummary(ApplyStatus.Completed, string.Empty, []));
        state.HandleKey(TuiFixtures.Escape());

        for (int i = 0; i < 4; i++)
        {
            state.HandleKey(TuiFixtures.Tab());
        }

        state.HandleKey(TuiFixtures.Space());
        state.HandleKey(TuiFixtures.Letter('a'));
        state.HandleKey(TuiFixtures.Letter('a'));

        Assert.Contains("already ran", state.Frame().Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_scan_is_offered_from_every_browse_screen()
    {
        foreach (TuiScreen screen in TuiState.TabOrder)
        {
            TuiState state = TuiFixtures.State();
            while (state.Screen != screen)
            {
                state.HandleKey(TuiFixtures.Tab());
            }

            Assert.Equal(TuiIntentKind.Scan, state.HandleKey(TuiFixtures.Letter('s')).Kind);
        }
    }
}

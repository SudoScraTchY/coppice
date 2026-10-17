using System.Globalization;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Plans;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;
using RemovalAction = Coppice.Core.Domain.RemovalAction;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Tests.Apply;

/// <summary>
/// T-034: binding a plan to the machine as it is now (S-1).
/// <para>
/// The plan is an instruction written earlier; the roots are resolved this run. Every test here is about a
/// disagreement between the two, because agreement is the boring case and disagreement is where a tool
/// either refuses or deletes something it was never told about.
/// </para>
/// </summary>
public sealed class PlanBinderTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Packages = @"C:\Users\dev\.nuget\packages";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Item Package(string name, string version = "1.0.0", ulong size = 4096, string? lastWrite = null, string? path = null) =>
        new(
            "dotnet",
            "package",
            name,
            version,
            "nuget-packages",
            path ?? $@"{Packages}\{name}\{version}",
            size,
            Risk.Safe,
            Facts.From([new KeyValuePair<string, string>("lastWriteUtc", lastWrite ?? Stamp.ToString("O", CultureInfo.InvariantCulture))]));

    private static Snapshot SnapshotOf(params Item[] items) =>
        Snapshot.Create("snap_1", Stamp, items, [], ["dotnet"]);

    private static Plan PlanOf(string snapshotId, params PlanStep[] steps) =>
        PlanBuilder.Build(snapshotId, Policy.Default, steps).Plan!;

    private static PlanStep Delete(Item item, string? path = null) =>
        new(item.ItemId, Risk.Safe, "unused", RemovalAction.PathDelete(path ?? item.Path), item.Size);

    private static BindingRoot PackagesRoot(bool cleanable = true, string? allowlist = "*/*", bool installerOwned = false) =>
        new("nuget-packages", Packages, cleanable, installerOwned, allowlist);

    private static IReadOnlyList<BoundStep> Bind(Plan plan, Snapshot snapshot, IReadOnlyList<BindingRoot> roots, bool strict = false) =>
        PlanBinder.Bind(plan, snapshot, roots, Win, strict);

    [Fact]
    public void A_step_binds_to_the_live_root_and_carries_the_snapshot_fingerprint()
    {
        Item item = Package("newtonsoft.json");
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot()]));

        Assert.True(bound.IsBound);
        Assert.Equal(Packages, bound.GatewayStep!.Root);
        Assert.Equal(item.Path, bound.GatewayStep.Path);
        Assert.Equal("*/*", bound.GatewayStep.AllowlistPattern);
        Assert.Equal(Stamp, bound.GatewayStep.Fingerprint.LastWriteTimeUtc);
        Assert.Equal(item.Size, bound.GatewayStep.Fingerprint.Size);
        Assert.Equal("dotnet", bound.GatewayStep.Ecosystem);
    }

    [Fact]
    public void A_step_whose_item_is_not_in_the_snapshot_is_refused()
    {
        Item inPlan = Package("newtonsoft.json");
        Item inSnapshot = Package("other.package");
        Plan plan = PlanOf("snap_1", Delete(inPlan));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(inSnapshot), [PackagesRoot()]));

        Assert.False(bound.IsBound);
        Assert.Equal(GatewayRefusal.PlanInvalid, bound.Refusal);
        Assert.Contains("not in the plan's snapshot", bound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_is_bound_to_the_root_resolved_this_run_not_the_one_it_asserts()
    {
        // The snapshot's item path is under a root that no longer resolves; a different cache does. The
        // binder has no root containing the item, so it refuses rather than adopting the other one.
        Item item = Package("newtonsoft.json", path: @"D:\other-cache\newtonsoft.json\1.0.0");
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot()]));

        Assert.False(bound.IsBound);
        Assert.Equal(GatewayRefusal.PlanInvalid, bound.Refusal);
        Assert.Contains("No root resolved this run", bound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_root_that_did_not_validate_this_run_binds_nothing()
    {
        Item item = Package("newtonsoft.json");
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot(cleanable: false)]));

        Assert.False(bound.IsBound);
        Assert.Equal(GatewayRefusal.PlanInvalid, bound.Refusal);
        Assert.Contains("did not validate", bound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_location_with_no_allowlist_refuses_a_path_delete()
    {
        Item item = Package("newtonsoft.json");
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot(allowlist: null)]));

        Assert.False(bound.IsBound);
        Assert.Equal(GatewayRefusal.NotAllowlisted, bound.Refusal);
    }

    [Fact]
    public void A_plan_step_that_names_a_different_path_than_its_item_is_refused()
    {
        Item item = Package("newtonsoft.json");
        Plan plan = PlanOf("snap_1", Delete(item, $@"{Packages}\system.io\1.0.0"));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot()]));

        Assert.False(bound.IsBound);
        Assert.Equal(GatewayRefusal.PlanInvalid, bound.Refusal);
        Assert.Contains("but its snapshot item is", bound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_refuses_a_step_whose_snapshot_has_no_mtime()
    {
        Item item = Package("newtonsoft.json") with { Facts = Facts.Empty };
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot()], strict: true));

        Assert.False(bound.IsBound);
        Assert.Equal(GatewayRefusal.PlanInvalid, bound.Refusal);
        Assert.Contains("--strict", bound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_strict_a_missing_mtime_binds_with_the_epoch_rather_than_refusing()
    {
        Item item = Package("newtonsoft.json") with { Facts = Facts.Empty };
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot()]));

        Assert.True(bound.IsBound);
        Assert.Equal(DateTimeOffset.MinValue, bound.GatewayStep!.Fingerprint.LastWriteTimeUtc);
    }

    [Fact]
    public void An_installer_owned_root_is_marked_so_the_gateway_reports_instead_of_removing()
    {
        Item item = Package("newtonsoft.json");
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot(installerOwned: true)]));

        Assert.True(bound.IsBound);
        Assert.True(bound.GatewayStep!.InstallerOwned);
    }

    [Fact]
    public void A_report_only_step_binds_to_nothing_and_says_why()
    {
        Item item = Package("newtonsoft.json");
        PlanStep step = new(item.ItemId, Risk.Manual, "installer-owned", RemovalAction.ReportOnly("use the installer"), 0);
        Plan plan = PlanOf("snap_1", step);

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot()]));

        Assert.False(bound.IsBound);
        Assert.True(bound.IsReportOnly);
        Assert.Equal(GatewayRefusal.None, bound.Refusal);
        Assert.Equal("use the installer", bound.Message);
    }

    [Fact]
    public void A_native_step_binds_without_an_allowlist_because_there_is_no_path_to_match()
    {
        Item item = Package("newtonsoft.json");
        PlanStep step = new(item.ItemId, Risk.Safe, "tool-owned", RemovalAction.Native("dotnet nuget remove source x"), 0);
        Plan plan = PlanOf("snap_1", step);

        BoundStep bound = Assert.Single(Bind(plan, SnapshotOf(item), [PackagesRoot(allowlist: null)]));

        Assert.True(bound.IsBound);
        Assert.Equal(RemovalKind.NativeCommand, bound.GatewayStep!.Action.Kind);
        Assert.Equal(string.Empty, bound.GatewayStep.AllowlistPattern);
    }

    [Fact]
    public void The_most_specific_containing_root_wins()
    {
        Item item = Package("newtonsoft.json", path: $@"{Packages}\pinned\newtonsoft.json\1.0.0");
        Plan plan = PlanOf("snap_1", Delete(item));

        BoundStep bound = Assert.Single(Bind(
            plan,
            SnapshotOf(item),
            [PackagesRoot(), new BindingRoot("nuget-packages", $@"{Packages}\pinned", true, false, "*/*")]));

        Assert.True(bound.IsBound);
        Assert.Equal($@"{Packages}\pinned", bound.GatewayStep!.Root);
    }
}

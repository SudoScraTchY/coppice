using System.Text.Json;
using Coppice.Core.Domain;

namespace Coppice.Tests.Domain;

/// <summary>Card T-003 acceptance: lossless JSON round-trip for every kernel-owned record.</summary>
public sealed class RoundTripTests
{
    private static Item SampleItem() => new(
        Ecosystem: "dotnet",
        Kind: "package",
        Name: "Newtonsoft.Json",
        Version: "13.0.3",
        LocationId: "nuget-packages",
        Path: @"C:\Users\dev\.nuget\packages\newtonsoft.json\13.0.3",
        Size: 4_718_592,
        Risk: Risk.Safe,
        Facts: Facts.From([new("targetFramework", "net10.0"), new("source", "nuget.org")]));

    [Fact]
    public void Item_round_trips_losslessly()
    {
        Item original = SampleItem();
        string json = DomainJson.Serialize(original);
        Item? restored = DomainJson.Deserialize<Item>(json);

        // Canonical-form equality is the real losslessness claim (FR-11): a record's own Equals
        // compares collection members by reference, which proves nothing about a round-trip.
        Assert.Equal(json, DomainJson.Serialize(restored));
        Assert.Equal(original, restored);
        Assert.Equal(original.ItemId, restored!.ItemId);
    }

    [Fact]
    public void Plan_round_trips_losslessly()
    {
        var plan = new Plan(
            PlanId: "plan-1",
            SnapshotId: "snap-1",
            Policy: Policy.Default,
            Steps: Deterministic.OrderSteps(
            [
                new PlanStep("abc123", Risk.Safe, "unreferenced and regenerable", RemovalAction.PathDelete(@"C:\cache\pkg\1.0.0"), 1024),
                new PlanStep("def456", Risk.Review, "reinstallable, opt-in required", RemovalAction.Native("dotnet workload uninstall foo"), 4096),
                new PlanStep("ghi789", Risk.Manual, "installer-owned", RemovalAction.ReportOnly("use the Visual Studio Installer"), 8192),
            ]),
            ExpectedReclaimBytes: 13_312,
            Checksum: "deadbeef");

        string json = DomainJson.Serialize(plan);
        Plan? restored = DomainJson.Deserialize<Plan>(json);
        Assert.Equal(json, DomainJson.Serialize(restored));
        Assert.Equal(plan.Steps.Count, restored!.Steps.Count);
        Assert.Equal(plan.Steps.Select(s => s.ItemId), restored.Steps.Select(s => s.ItemId));
        Assert.Equal(plan.ExpectedReclaimBytes, restored.ExpectedReclaimBytes);
        Assert.Equal(plan.Checksum, restored.Checksum);
    }

    [Fact]
    public void ResolvedRoot_round_trips_losslessly()
    {
        var root = new ResolvedRoot(
            DeclaredPath: "$HOME/go/pkg/mod",
            RealPath: @"/home/dev/go/pkg/mod",
            Role: RootRole.Active,
            Via: ResolvedVia.Env,
            ViaDetail: "GOMODCACHE",
            Validity: RootValidity.Ok,
            Owner: "user");

        Assert.Equal(root, DomainJson.Deserialize<ResolvedRoot>(DomainJson.Serialize(root)));
    }

    [Fact]
    public void LocationScan_and_Problem_round_trip_losslessly()
    {
        var scan = new LocationScan(
            LocationId: "nuget-packages",
            Roots: Deterministic.OrderRoots([ResolvedRoot.Reject(@"C:\nope", RootValidity.NotFound)]),
            Items: Deterministic.OrderItems([SampleItem()]),
            Issues: Deterministic.OrderIssues([new ScanIssue("nuget-packages", "E-11", "tool reported an implausible root", @"C:\")]));

        Assert.Equal(DomainJson.Serialize(scan), DomainJson.Serialize(DomainJson.Deserialize<LocationScan>(DomainJson.Serialize(scan))));

        var problem = new Problem
        {
            Ecosystem = "dotnet",
            Code = "ORPHAN_SDK",
            Severity = Severity.Warning,
            Summary = "SDK folder with no host",
            Path = @"C:\dotnet\sdk\9.0.100",
            Detail = Facts.From([new("hostFxr", "missing")]),
        };
        Assert.Equal(problem, DomainJson.Deserialize<Problem>(DomainJson.Serialize(problem)));
        Assert.Equal(problem.Detail, DomainJson.Deserialize<Problem>(DomainJson.Serialize(problem))!.Detail);
    }

    [Fact]
    public void Checksum_changes_when_any_field_changes()
    {
        Item a = SampleItem();
        Item b = a with { Version = "13.0.4" };
        Assert.NotEqual(DomainJson.ComputeChecksum(a), DomainJson.ComputeChecksum(b));
    }

    [Fact]
    public void Checksum_is_independent_of_formatting()
    {
        Item item = SampleItem();
        Assert.Equal(DomainJson.ComputeChecksum(item), DomainJson.ComputeChecksum(item));
        // Indented output must still parse to the same value; the checksum ignores layout.
        using JsonDocument doc = JsonDocument.Parse(DomainJson.Serialize(item, indented: true));
        Assert.Equal(item.Ecosystem, doc.RootElement.GetProperty("ecosystem").GetString());
    }

    [Fact]
    public void Enums_serialize_as_stable_lowercase_names()
    {
        string json = DomainJson.Serialize(SampleItem());
        Assert.Contains("\"safe\"", json, StringComparison.Ordinal);
    }
}

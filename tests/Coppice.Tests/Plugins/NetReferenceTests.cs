using Coppice.Core.Domain;
using Coppice.Plugins.Net;
using Xunit;

namespace Coppice.Tests.Plugins;

/// <summary>
/// Card T-012 acceptance (FR-07, S-4, C-7). The assets fixture below is REAL output captured from
/// `dotnet restore` on a project with two package references, including the sha512 and the full file
/// list NuGet writes.
/// </summary>
public sealed class NetReferenceTests
{
    /// <summary>Real `obj/project.assets.json`, trimmed to the parts the reader consumes.</summary>
    private const string RealAssets = """
        {
          "version": 3,
          "targets": {
            "net10.0": {
              "Newtonsoft.Json/13.0.3": {
                "type": "package",
                "compile": { "lib/net6.0/Newtonsoft.Json.dll": {} },
                "runtime": { "lib/net6.0/Newtonsoft.Json.dll": {} }
              },
              "Serilog/4.0.0": {
                "type": "package",
                "compile": { "lib/net8.0/Serilog.dll": {} }
              },
              "Coppice.Ports/0.1.0": {
                "type": "project",
                "framework": ".NETCoreApp,Version=v10.0",
                "compile": { "bin/placeholder/Coppice.Ports.dll": {} }
              }
            }
          },
          "libraries": {
            "Newtonsoft.Json/13.0.3": {
              "sha512": "HrC5BXdl00IP9zeV+0Z848QWPAoCr9P3bDEZguI+gkLcBKAOxix/tLEAAHC+UvDNPv4a2d18lOReHMOagPa+zQ==",
              "type": "package",
              "path": "newtonsoft.json/13.0.3",
              "files": [ ".nupkg.metadata", ".signature.p7s", "lib/net6.0/Newtonsoft.Json.dll" ]
            },
            "Serilog/4.0.0": {
              "sha512": "abc==",
              "type": "package",
              "path": "serilog/4.0.0"
            }
          }
        }
        """;

    private const string GlobalJson = """
        {
          "sdk": {
            "version": "10.0.100",
            "rollForward": "latestFeature"
          }
        }
        """;

    // ---- assets parsing ----

    [Fact]
    public void Real_assets_file_yields_exact_package_versions()
    {
        IReadOnlyList<PackageReference> refs = NetReferenceReader.ParseAssets(RealAssets)!;

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.PackageId == "Newtonsoft.Json" && r.Version == "13.0.3");
        Assert.Contains(refs, r => r.PackageId == "Serilog" && r.Version == "4.0.0");
    }

    [Fact]
    public void Project_references_are_not_package_references()
    {
        // "Coppice.Ports" has type "project". Counting it would make every in-repo project look like
        // it protects a cached package, so an old version could never be cleaned.
        IReadOnlyList<PackageReference> refs = NetReferenceReader.ParseAssets(RealAssets)!;

        Assert.DoesNotContain(refs, r => r.PackageId == "Coppice.Ports");
    }

    [Fact]
    public void References_from_several_target_frameworks_are_merged_and_deduplicated()
    {
        // A multi-targeted project lists its packages once per framework. The same package appearing
        // under net8.0 and net10.0 is ONE dependency, and counting it twice would inflate the
        // "referenced by N projects" figure.
        const string multi = """
            {
              "targets": {
                "net8.0": { "Serilog/4.0.0": { "type": "package" } },
                "net10.0": { "Serilog/4.0.0": { "type": "package" }, "Newtonsoft.Json/13.0.3": { "type": "package" } }
              }
            }
            """;

        IReadOnlyList<PackageReference> refs = NetReferenceReader.ParseAssets(multi)!;

        Assert.Equal(2, refs.Count);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void An_unreadable_assets_file_is_unknown_not_unreferenced(string json)
    {
        // null means "we cannot tell". Returning an empty list would say "this project references
        // nothing", which is a much stronger claim and a much more dangerous one.
        Assert.Null(NetReferenceReader.ParseAssets(json));
    }

    [Fact]
    public void A_project_without_an_assets_file_reports_null()
    {
        var fs = new Coppice.Tests.Support.FakeFileSystem(Coppice.Ports.OperatingSystemKind.Linux);
        var reader = new NetReferenceReader(fs);

        // Not restored yet: the project may well reference everything once it is.
        Assert.Null(reader.ReadReferences("/work/src/App", '/'));
    }

    // ---- three-state resolution ----

    [Fact]
    public void A_package_used_by_two_projects_is_referenced_with_a_reason()
    {
        var resolver = new ReferenceResolver();
        resolver.AddProject("/work/a", [new PackageReference("Serilog", "4.0.0")]);
        resolver.AddProject("/work/b", [new PackageReference("Serilog", "4.0.0")]);

        ReferenceVerdict verdict = resolver.Resolve("Serilog", "4.0.0");

        Assert.Equal(Usage.Referenced, verdict.Usage);
        Assert.Equal(2, verdict.ReferencingProjects.Count);
        Assert.Equal("referenced by 2 projects", verdict.Reason);
    }

    [Fact]
    public void A_single_referencing_project_is_named_in_the_reason()
    {
        var resolver = new ReferenceResolver();
        resolver.AddProject("/work/a", [new PackageReference("Serilog", "4.0.0")]);

        Assert.Equal("referenced by 1 project (/work/a)", resolver.Resolve("Serilog", "4.0.0").Reason);
    }

    [Fact]
    public void A_version_mismatch_does_not_count_as_referenced()
    {
        // The cache holds 4.0.0 and 3.1.1; the project wants 4.0.0. Only the matching VERSION is
        // protected — that is what makes "keep latest N" meaningful.
        var resolver = new ReferenceResolver();
        resolver.AddProject("/work/a", [new PackageReference("Serilog", "4.0.0")]);

        Assert.Equal(Usage.Referenced, resolver.Resolve("Serilog", "4.0.0").Usage);
        Assert.Equal(Usage.Unreferenced, resolver.Resolve("Serilog", "3.1.1").Usage);
    }

    [Fact]
    public void An_unused_package_is_unreferenced_when_every_project_was_readable()
    {
        var resolver = new ReferenceResolver();
        resolver.AddProject("/work/a", [new PackageReference("Serilog", "4.0.0")]);
        resolver.AddProject("/work/b", []);

        ReferenceVerdict verdict = resolver.Resolve("Newtonsoft.Json", "13.0.3");

        Assert.Equal(Usage.Unreferenced, verdict.Usage);
        Assert.Contains("no project references Newtonsoft.Json", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_projects_scanned_nothing_is_unreferenced()
    {
        // The safety case: a user who has configured no project roots would otherwise be shown their
        // ENTIRE NuGet cache as deletable. Zero evidence is never proof of absence.
        var resolver = new ReferenceResolver();

        ReferenceVerdict verdict = resolver.Resolve("Newtonsoft.Json", "13.0.3");

        Assert.Equal(Usage.Unknown, verdict.Usage);
        Assert.Contains("no projects were scanned", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrestored_project_downgrades_unreferenced_to_unknown()
    {
        var resolver = new ReferenceResolver();
        resolver.AddProject("/work/a", [new PackageReference("Serilog", "4.0.0")]);
        resolver.AddProject("/work/unrestored", null);

        ReferenceVerdict verdict = resolver.Resolve("Newtonsoft.Json", "13.0.3");

        Assert.Equal(Usage.Unknown, verdict.Usage);
        Assert.Contains("could not be read", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_never_yields_safe()
    {
        // C-7 / 04-plugin-contract rule 4, enforced structurally so no caller can forget it.
        Assert.Equal(
            Risk.Review,
            ReferenceResolver.ApplyUsageFloor(ReferenceVerdict.Unknown("cannot tell"), Risk.Safe));

        Assert.Equal(
            Risk.Safe,
            ReferenceResolver.ApplyUsageFloor(ReferenceVerdict.Unreferenced(3, "X"), Risk.Safe));

        Assert.Equal(
            Risk.Safe,
            ReferenceResolver.ApplyUsageFloor(
                ReferenceVerdict.Referenced(["/work/a"]), Risk.Safe));
    }

    [Fact]
    public void The_usage_floor_never_lowers_a_higher_risk()
    {
        Assert.Equal(
            Risk.Manual,
            ReferenceResolver.ApplyUsageFloor(ReferenceVerdict.Unknown("cannot tell"), Risk.Manual));
    }

    // ---- global.json / S-4 ----

    [Fact]
    public void Global_json_pin_is_read()
    {
        SdkPin pin = NetReferenceReader.ReadGlobalJson(GlobalJson)!;

        Assert.Equal("10.0.100", pin.Version);
        Assert.Equal("latestFeature", pin.RollForward);
    }

    [Fact]
    public void A_pinned_sdk_blocks_proposal_of_a_different_version()
    {
        // S-4: a pin is a REFUSAL rule. A project pinned to 10.0.100 must never have 10.0.301
        // proposed for removal, because the next build would re-download it and the user would
        // have paid for nothing.
        SdkPin pin = NetReferenceReader.ReadGlobalJson(GlobalJson)!;

        Assert.False(SdkAllowsRemoval(pin, "10.0.100"), "the pinned version itself is in use");
        Assert.False(SdkAllowsRemoval(pin, "10.0.200"), "latestFeature rolls forward inside 10.0.x, so it is still the pin");
        Assert.False(SdkAllowsRemoval(pin, "10.0.301"), "same band, still reachable by roll-forward");

        // An OLD major is NOT protected: a project pinned to 10.x needs nothing from 9.x, and 9.x is
        // precisely the sort of superseded SDK the tool exists to offer. Freezing it would be the pin
        // protecting the wrong thing.
        Assert.True(SdkAllowsRemoval(pin, "9.0.300"));
    }

    [Fact]
    public void An_unpinned_sdk_may_be_proposed()
    {
        Assert.True(SdkAllowsRemoval(null, "10.0.301"));
    }

    /// <summary>
    /// The S-4 refusal rule: a version is proposable only when no project pins it, directly or via a
    /// roll-forward band that covers it.
    /// </summary>
    private static bool SdkAllowsRemoval(SdkPin? pin, string candidateVersion)
    {
        if (pin is null)
        {
            return true;
        }

        // latestFeature rolls forward within the pinned major.minor band, so anything in 10.0.x is
        // still "the pinned SDK" and must be kept.
        if (string.Equals(pin.RollForward, "latestFeature", StringComparison.OrdinalIgnoreCase)
            && string.Equals(SdkVersion.MajorOf(candidateVersion), SdkVersion.MajorOf(pin.Version), StringComparison.Ordinal))
        {
            return false;
        }

        return !string.Equals(candidateVersion, pin.Version, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(SdkVersion.BandOf(candidateVersion), SdkVersion.BandOf(pin.Version), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"sdk\":{}}")]
    [InlineData("")]
    public void An_unusable_global_json_yields_no_pin(string json) =>
        Assert.Null(NetReferenceReader.ReadGlobalJson(json));

    [Fact]
    public void A_referencing_project_is_recorded_once_even_if_scanned_twice()
    {
        var resolver = new ReferenceResolver();
        var refs = new[] { new PackageReference("Serilog", "4.0.0") };

        resolver.AddProject("/work/a", refs);
        resolver.AddProject("/work/a", refs);

        Assert.Single(resolver.Resolve("Serilog", "4.0.0").ReferencingProjects);
    }
}

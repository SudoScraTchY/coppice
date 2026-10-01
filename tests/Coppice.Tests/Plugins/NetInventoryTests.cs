using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Plugins.Net;
using Coppice.Ports;
using Coppice.Tests.Support;

namespace Coppice.Tests.Plugins;

/// <summary>
/// Card T-011 acceptance (FR-04, C-4). The SDK and locals fixtures are REAL captured output,
/// including the trailing backslash NuGet prints and the pre-release version that breaks naive
/// parsing.
/// </summary>
public sealed class NetInventoryTests
{
    private static ScanContext Context(FakeFileSystem fs, string root = "/cache") => new()
    {
        FileSystem = fs,
        OS = OperatingSystemKind.Linux,
        RootPath = root,
        LocationId = "nuget-packages",
    };

    /// <summary>Two packages, three versions each, with the files a real restore leaves behind.</summary>
    private static FakeFileSystem PackageFixture()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        foreach (string version in new[] { "13.0.1", "13.0.3", "13.0.2" })
        {
            CompletePackage(fs, "newtonsoft.json", version, size: 1000);
        }

        foreach (string version in new[] { "3.1.1", "3.1.2", "4.0.0" })
        {
            CompletePackage(fs, "serilog", version, size: 500);
        }

        return fs;
    }

    private static void CompletePackage(FakeFileSystem fs, string id, string version, int size)
    {
        string dir = $"/cache/{id}/{version}";
        fs.AddDirectory(dir);
        fs.AddFile($"{dir}/{id}.{version}.nupkg", new string('x', size));
        fs.AddFile($"{dir}/{id}.{version}.nupkg.metadata", "<metadata/>");
        fs.AddDirectory($"{dir}/{id}");                 // extracted nuspec folder
        fs.AddDirectory($"{dir}/lib/net10.0");
        fs.AddFile($"{dir}/lib/net10.0/{id}.dll", new string('y', size));
    }

    private static async Task<IReadOnlyList<Item>> EnumerateAsync(FakeFileSystem fs)
    {
        var enumerator = new NuGetPackageEnumerator(fs, OperatingSystemKind.Linux);
        var items = new List<Item>();

        await foreach (Item item in enumerator.EnumerateAsync(Context(fs)))
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task Three_versions_of_two_packages_yield_six_items()
    {
        IReadOnlyList<Item> items = await EnumerateAsync(PackageFixture());

        Assert.Equal(6, items.Count);
        Assert.Equal(3, items.Count(i => i.Name == "newtonsoft.json"));
        Assert.Equal(3, items.Count(i => i.Name == "serilog"));
    }

    [Fact]
    public async Task Each_item_carries_the_integrity_facts()
    {
        IReadOnlyList<Item> items = await EnumerateAsync(PackageFixture());
        Item item = items.Single(i => i.Name == "newtonsoft.json" && i.Version == "13.0.3");

        Assert.Equal("dotnet", item.Ecosystem);
        Assert.Equal("package", item.Kind);
        Assert.Equal("nuget-packages", item.LocationId);
        Assert.Equal("newtonsoft.json", item.Facts["packageId"]);
        Assert.Equal("true", item.Facts["nupkgPresent"]);
        Assert.Equal("true", item.Facts["metadataPresent"]);
        Assert.Equal("true", item.Facts["nuspecPresent"]);
        Assert.Equal("false", item.Facts["signaturePresent"]);
    }

    [Fact]
    public async Task A_half_written_package_is_visible_in_its_facts()
    {
        // 09's health check for "partial/corrupt package folders" needs this signal; a missing nupkg
        // is exactly the case that cannot be re-downloaded on demand.
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/broken/1.0.0")
            .AddFile("/cache/broken/1.0.0/lib.dll", "x");

        Item item = Assert.Single(await EnumerateAsync(fs));

        Assert.Equal("false", item.Facts["nupkgPresent"]);
        Assert.Equal("false", item.Facts["metadataPresent"]);
    }

    [Fact]
    public async Task Package_items_are_safe_tier()
    {
        // The global-packages cache is re-downloadable; this is what lets a default policy act.
        Assert.All(await EnumerateAsync(PackageFixture()), i => Assert.Equal(Risk.Safe, i.Risk));
    }

    [Fact]
    public async Task Item_ordering_is_deterministic_and_ordinal()
    {
        FakeFileSystem fs = PackageFixture();
        IReadOnlyList<Item> first = await EnumerateAsync(fs);
        IReadOnlyList<Item> second = await EnumerateAsync(fs);

        // C-4: the same tree yields the same order, every time.
        Assert.Equal(first.Select(i => i.ItemId), second.Select(i => i.ItemId));

        // And through the pipeline's own ordering, which is what a snapshot will hold.
        IReadOnlyList<Item> ordered = Deterministic.OrderItems(first);
        Assert.Equal(
            ["13.0.1", "13.0.2", "13.0.3", "3.1.1", "3.1.2", "4.0.0"],
            [.. ordered.Select(i => i.Version)]);
    }

    [Fact]
    public async Task Sizes_come_from_the_measured_tree()
    {
        IReadOnlyList<Item> items = await EnumerateAsync(PackageFixture());

        // Sizes are MEASURED over the whole version directory, not estimated from the nupkg alone:
        // the newtonsoft fixture holds a 1000-char nupkg, its metadata file, and a 1000-char dll.
        Item newtonsoft = items.First(i => i.Name == "newtonsoft.json");
        Assert.Equal(2011UL, newtonsoft.Size);

        // The point of measuring rather than estimating: the reclaim figure a user is shown is the
        // sum of these, so a missed sibling file would overstate what apply would free.
        Assert.Equal(1011UL, items.First(i => i.Name == "serilog").Size);
    }

    [Fact]
    public async Task Enumeration_performs_zero_filesystem_writes()
    {
        FakeFileSystem fs = PackageFixture();
        await EnumerateAsync(fs);

        Assert.Empty(fs.Writes);
    }

    // ---- SDK parsing from REAL captured output ----

    [Fact]
    public void Sdks_are_parsed_from_recorded_output()
    {
        IReadOnlyList<SdkInfo> sdks = DotnetOutputParser.ParseSdks(DotnetOutputFixtures.ListSdks);

        Assert.Equal(2, sdks.Count);
        Assert.Equal("9.0.300", sdks[0].Version);
        Assert.Equal(@"C:\Program Files\dotnet\sdk", sdks[0].InstallPath);
        Assert.Equal("9", sdks[0].Major);
        Assert.Equal("0.300", sdks[0].Band);
        Assert.False(sdks[0].IsPreview);
        Assert.Equal("10.0.301", sdks[1].Version);
    }

    [Fact]
    public void A_preview_sdk_is_parsed_and_flagged()
    {
        // The case that breaks naive parsing: the version contains a '-', so a split on the bracket
        // alone would produce a garbage version and lose the preview flag the health check needs.
        IReadOnlyList<SdkInfo> sdks = DotnetOutputParser.ParseSdks(DotnetOutputFixtures.ListSdksWithPreview);

        Assert.Equal(3, sdks.Count);
        SdkInfo preview = sdks.Single(s => s.IsPreview);
        Assert.Equal("7.0.100-preview.7.21379.14", preview.Version);
        Assert.Equal("7", preview.Major);
        Assert.Equal("0.100", preview.Band);

        Assert.False(sdks.Single(s => s.Version == "6.0.100").IsPreview);
        Assert.False(sdks.Single(s => s.Version == "8.0.401").IsPreview);
    }

    [Fact]
    public void Sdks_from_a_user_local_root_are_read_at_the_same_time_as_installer_sdks()
    {
        IReadOnlyList<SdkInfo> sdks = DotnetOutputParser.ParseSdks(DotnetOutputFixtures.ListSdksWithPreview);

        // The install path is recorded per SDK so `doctor` can tell "the installer left one behind"
        // from "this is a side-by-side user install".
        Assert.Contains(sdks, s => s.InstallPath.Contains(@"C:\Users\dev\.dotnet", StringComparison.Ordinal));
        Assert.Contains(sdks, s => s.InstallPath.Contains(@"C:\Program Files", StringComparison.Ordinal));
    }

    [Fact]
    public void Mac_sdks_parse_with_forward_slashes()
    {
        IReadOnlyList<SdkInfo> sdks = DotnetOutputParser.ParseSdks(DotnetOutputFixtures.ListSdksMac);

        Assert.Equal(2, sdks.Count);
        Assert.Equal("/usr/local/share/dotnet/sdk", sdks[0].InstallPath);
        Assert.Equal("/Users/dev/.dotnet/sdk", sdks[1].InstallPath);
    }

    // ---- version ordering ----

    [Theory]
    [InlineData("10.0.100", "9.0.300", 1)]
    [InlineData("9.0.300", "10.0.100", -1)]
    [InlineData("9.0.300", "9.0.300", 0)]
    [InlineData("9.0.400", "9.0.300", 1)]
    public void A_release_outranks_a_lower_one(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SdkVersion.Compare(a, b)));

    [Fact]
    public void A_release_outranks_a_preview_of_the_same_numeric_version()
    {
        // The opposite of a naive string compare, which puts "10.0.100-preview" AFTER "10.0.100"
        // because '-' (0x2D) > '.' (0x2E)... no: lexicographically "10.0.100-preview" > "10.0.100".
        // Either way, treating a preview as newer is how a tool "upgrades" to an older SDK.
        Assert.Equal(1, Math.Sign(SdkVersion.Compare("10.0.100", "10.0.100-preview.1")));
        Assert.Equal(-1, Math.Sign(SdkVersion.Compare("10.0.100-preview.1", "10.0.100")));
    }

    [Fact]
    public void Preview_builds_order_numerically_not_lexically()
    {
        Assert.Equal(1, Math.Sign(SdkVersion.Compare("7.0.100-preview.10", "7.0.100-preview.2")));
        Assert.Equal(1, Math.Sign(SdkVersion.Compare("7.0.100-preview.1.1", "7.0.100-preview.1")));
    }

    [Fact]
    public void Version_ordering_is_a_total_order_over_the_fixture_versions()
    {
        string[] sorted = [.. DotnetOutputParser.ParseSdks(DotnetOutputFixtures.ListSdksWithPreview)
            .Select(s => s.Version)
            .OrderBy(v => v, Comparer<string>.Create(SdkVersion.Compare))];

        Assert.Equal(
            ["6.0.100", "7.0.100-preview.7.21379.14", "8.0.401"],
            sorted);
    }

    [Fact]
    public void Build_metadata_does_not_change_ordering()
    {
        Assert.Equal(0, SdkVersion.Compare("9.0.100+sha.abcdef", "9.0.100"));
    }

    [Fact]
    public void Architecture_is_read_from_the_install_path_or_reported_unknown()
    {
        Assert.Equal("x64", SdkVersion.ArchitectureOf(@"C:\Program Files\x64\dotnet"));
        Assert.Equal("arm64", SdkVersion.ArchitectureOf(@"C:\Program Files\arm64\dotnet"));
        Assert.Equal("unknown", SdkVersion.ArchitectureOf(@"C:\Program Files\dotnet"));
    }

    // ---- nuget locals ----

    [Fact]
    public void Nuget_locals_are_parsed_with_the_trailing_separator_trimmed()
    {
        // Real NuGet prints "global-packages: …\packages\" with a trailing backslash. Keeping it
        // would make the value fail equality against the same path from another source, producing a
        // phantom second root for one directory.
        IReadOnlyList<LabeledPath> paths = DotnetOutputParser.ParseLabelValuePaths(DotnetOutputFixtures.NugetLocalsAll);

        LabeledPath global = paths.Single(p => p.Label == "global-packages");
        Assert.Equal(@"C:\Users\dev\.nuget\packages", global.Path);

        Assert.Equal(4, paths.Count);
        Assert.Contains(paths, p => p.Label == "http-cache" && p.Path == @"C:\Users\dev\AppData\Local\NuGet\v3-cache");
        Assert.Contains(paths, p => p.Label == "temp" && p.Path == @"C:\Windows\TEMP\NuGetScratch");
    }

    [Fact]
    public void The_real_windows_capture_parses()
    {
        // Captured on this machine, 2026-09-29.
        IReadOnlyList<LabeledPath> paths = DotnetOutputParser.ParseLabelValuePaths(DotnetOutputFixtures.NugetLocalsAllWindowsReal);

        Assert.Equal(@"C:\Users\SaintScraTchY\.nuget\packages", paths.Single(p => p.Label == "global-packages").Path);
        Assert.Equal(@"C:\Windows\TEMP\NuGetScratch", paths.Single(p => p.Label == "temp").Path);
    }

    [Fact]
    public void The_real_mac_capture_parses()
    {
        IReadOnlyList<LabeledPath> paths = DotnetOutputParser.ParseLabelValuePaths(DotnetOutputFixtures.NugetLocalsAllMac);

        Assert.Equal("/Users/dev/.local/share/NuGet/v3-cache", paths.Single(p => p.Label == "http-cache").Path);
        Assert.Equal("/Users/dev/.nuget/packages", paths.Single(p => p.Label == "global-packages").Path);
    }

    [Fact]
    public void A_tool_reported_path_matches_the_expanded_profile_default()
    {
        // The point of parsing the tool at all: when it answers, its answer wins over the default
        // (05, LR-1: tool > env > default). This proves the two spellings of one path are EQUAL
        // once trailing separators are trimmed.
        string fromTool = DotnetOutputParser
            .ParseLabelValuePaths(DotnetOutputFixtures.NugetLocalsAll)
            .Single(p => p.Label == "global-packages")
            .Path;

        var env = new FakeEnvironment(OperatingSystemKind.Windows)
            .With("USERPROFILE", @"C:\Users\dev");

        string fromDefault = NetDefaults.Expand(
            NetProfile.Find("nuget-packages")!.OsDefaults[OperatingSystemKind.Windows], env)!;

        Assert.Equal(fromDefault, fromTool);
    }

    // ---- other output formats ----

    [Fact]
    public void Runtimes_are_parsed_by_name_version_and_path()
    {
        IReadOnlyList<RuntimeInfo> runtimes = DotnetOutputParser.ParseRuntimes(DotnetOutputFixtures.ListRuntimes);

        Assert.Equal(3, runtimes.Count);
        RuntimeInfo core = runtimes.Single(r => r.Version == "10.0.9");
        Assert.Equal("Microsoft.NETCore.App", core.Name);
        Assert.Equal(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App", core.InstallPath);
        Assert.Equal("10", core.Major);
    }

    [Fact]
    public void Global_tools_are_parsed_and_the_header_is_skipped()
    {
        IReadOnlyList<GlobalTool> tools = DotnetOutputParser.ParseGlobalTools(DotnetOutputFixtures.GlobalTools);

        Assert.Equal(2, tools.Count);
        Assert.Equal(new GlobalTool("dotnet-ef", "10.0.8", "dotnet-ef"), tools[0]);
        Assert.Equal("3.0.71", tools[1].Version);
        Assert.Equal("libman", tools[1].Command);
    }

    [Fact]
    public void An_empty_tool_list_yields_no_tools_rather_than_a_tool_named_No()
    {
        Assert.Empty(DotnetOutputParser.ParseGlobalTools(DotnetOutputFixtures.GlobalToolsEmpty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("total garbage without any structure")]
    [InlineData("9.0.300 [unclosed bracket")]
    [InlineData(": no label")]
    public void Malformed_tool_output_is_skipped_never_thrown(string? output)
    {
        // Tool output is untrusted input: one stray line must not abort a scan.
        _ = DotnetOutputParser.ParseLabelValuePaths(output);
        _ = DotnetOutputParser.ParseSdks(output);
        _ = DotnetOutputParser.ParseRuntimes(output);
        _ = DotnetOutputParser.ParseGlobalTools(output);
    }

    [Fact]
    public void A_root_path_never_trims_to_empty()
    {
        // Trimming "/" to "" would make the filesystem root look like an empty string, which the
        // resolver would then treat as "no default configured".
        // "/" must not trim to "" — an empty root reads as "no default configured".
        Assert.Equal("/", DotnetOutputParser.TrimTrailingSeparators("/"));

        // A Windows drive root keeps its separator, which is what makes it a drive root rather
        // than a bare "C:" that the resolver would treat as drive-relative.
        Assert.Equal(@"C:\", DotnetOutputParser.TrimTrailingSeparators(@"C:\"));

        // A trailing separator on an ordinary path is still trimmed.
        Assert.Equal(@"/cache", DotnetOutputParser.TrimTrailingSeparators("/cache/"));
    }
}

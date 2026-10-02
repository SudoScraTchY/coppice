using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Plugins.Net;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using OSKind = Coppice.Ports.OperatingSystemKind;

namespace Coppice.Tests.Plugins;

/// <summary>
/// What the NuGet cache enumerator must NOT report (T-017 regression).
/// <para>
/// Every test here exists because a real bug shipped past a green suite. The golden reports exposed
/// it: the fixture's <c>dotnet-root</c> was read as a package cache, so the tool offered to delete a
/// package called "host", version "fxr". A second bug let <c>.tools</c> and the extracted
/// <c>lib/net8.0</c> tree through as versions. Both were invisible because every pre-golden test used
/// a hand-built fixture too small and too tidy to contain them.
/// </para>
/// </summary>
public sealed class NuGetPackageEnumeratorTests
{
    private static async Task<IReadOnlyList<Item>> EnumerateAsync(
        FakeFileSystem fs,
        string rootPath,
        string locationId = "nuget-packages")
    {
        var enumerator = new NuGetPackageEnumerator(fs, OSKind.Linux);
        var context = new ScanContext
        {
            FileSystem = fs,
            OS = OSKind.Linux,
            RootPath = rootPath,
            LocationId = locationId,
        };

        var items = new List<Item>();
        await foreach (Item item in enumerator.EnumerateAsync(context, CancellationToken.None))
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task Declines_a_root_that_is_not_the_nuget_packages_location()
    {
        // The pipeline offers every root to every enumerator. Without this guard a dotnet-root is
        // walked as if it were a package cache and reports "host"/"fxr" as a package.
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory("/usr/share/dotnet/host/fxr");
        fs.AddDirectory("/usr/share/dotnet/sdk");

        Assert.Empty(await EnumerateAsync(fs, "/usr/share/dotnet", locationId: "dotnet-root"));
    }

    [Fact]
    public async Task Reports_a_real_package_version()
    {
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory("/cache/newtonsoft.json/13.0.3");
        fs.AddFileOfSize("/cache/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", 4096);

        Item item = Assert.Single(await EnumerateAsync(fs, "/cache"));

        Assert.Equal("newtonsoft.json", item.Name);
        Assert.Equal("13.0.3", item.Version);
    }

    [Fact]
    public async Task Reports_a_prerelease_version()
    {
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory("/cache/spectre.console/0.51.2-preview.0.3");

        Item item = Assert.Single(await EnumerateAsync(fs, "/cache"));

        Assert.Equal("0.51.2-preview.0.3", item.Version);
    }

    [Theory]
    [InlineData(".tools")]
    [InlineData(".metadata")]
    [InlineData("lib")]
    [InlineData("ref")]
    [InlineData("build")]
    [InlineData("runtimes")]
    [InlineData("tools")]
    [InlineData("analyzers")]
    [InlineData("packageServices")]
    public async Task Does_not_report_package_internals_as_versions(string directory)
    {
        // These live BESIDE the versions inside a package id. Reporting any as a version invents a
        // cleanup target; lib/ref/runtimes nest again, so one package would report several phantoms
        // named after its target frameworks.
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory($"/cache/newtonsoft.json/{directory}");
        fs.AddDirectory("/cache/newtonsoft.json/13.0.3");

        IReadOnlyList<Item> items = await EnumerateAsync(fs, "/cache");

        Item item = Assert.Single(items);
        Assert.Equal("13.0.3", item.Version);
    }

    [Fact]
    public async Task Reports_every_version_of_a_multi_version_package()
    {
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory("/cache/serilog/2.0.0");
        fs.AddDirectory("/cache/serilog/3.0.1");
        fs.AddDirectory("/cache/serilog/3.1.1");

        IReadOnlyList<Item> items = await EnumerateAsync(fs, "/cache");

        // One item per VERSION: the stale version is what wastes space, and the version no project
        // references is what can be removed.
        Assert.Equal(["2.0.0", "3.0.1", "3.1.1"], items.Select(i => i.Version));
    }

    [Fact]
    public async Task Records_the_integrity_signals_the_health_check_needs()
    {
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory("/cache/polly/8.0.0");
        fs.AddFile("/cache/polly/8.0.0/polly.8.0.0.nupkg.metadata");
        fs.AddDirectory("/cache/polly/8.0.0/polly");

        Item item = Assert.Single(await EnumerateAsync(fs, "/cache"));

        Assert.Equal("true", item.Facts["metadataPresent"]);
        Assert.Equal("true", item.Facts["nuspecPresent"]);
        Assert.Equal("false", item.Facts["nupkgPresent"]);
        Assert.Equal("false", item.Facts["signaturePresent"]);
    }

    [Fact]
    public async Task An_incomplete_package_is_still_reported_with_its_missing_signals()
    {
        // A half-written package must be VISIBLE, not skipped — that is what makes it cleanable, and
        // silently dropping it would leave exactly the residue the tool exists to find.
        var fs = new FakeFileSystem(OSKind.Linux);
        fs.AddDirectory("/cache/halfwritten/1.0.0");

        Item item = Assert.Single(await EnumerateAsync(fs, "/cache"));

        Assert.Equal("false", item.Facts["nupkgPresent"]);
    }

    [Fact]
    public async Task Output_is_ordered_ordinally_regardless_of_enumeration_order()
    {
        var fs = new FakeFileSystem(OSKind.Linux);
        foreach (string name in new[] { "Zebra", "apple", "Banana", "cherry" })
        {
            fs.AddDirectory($"/cache/{name}/1.0.0");
        }

        IReadOnlyList<Item> items = await EnumerateAsync(fs, "/cache");

        // Ordinal, never culture-aware: a locale that sorts lowercase before uppercase would
        // otherwise reorder the report run to run.
        Assert.Equal(["Banana", "Zebra", "apple", "cherry"], items.Select(i => i.Name));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("13.0.3", true)]
    [InlineData("1.0.0-preview.1", true)]
    [InlineData("1.0.0+build.5", false)]
    [InlineData("lib", false)]
    [InlineData("net8.0", false)]
    [InlineData(".tools", false)]
    [InlineData("", false)]
    public void Version_detection_matches_what_nuget_actually_writes(string name, bool expected) =>
        Assert.Equal(expected, NetEcosystemLocations.LooksLikeVersion(name));
}

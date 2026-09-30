using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Ports;
using Coppice.Tests.Support;

namespace Coppice.Tests.Scanning;

/// <summary>
/// Card T-007 acceptance (FR-04, FR-05, NFR-04, NFR-06). Everything runs on the fake VFS, so no
/// test can touch a real machine and the zero-writes rule (C-1) is assertable.
/// </summary>
public sealed class ScanPipelineTests
{
    /// <summary>A cache root whose total size is known exactly, for the 2%-accuracy check.</summary>
    private static FakeFileSystem SizedFixture()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        // pkg-a: 1000 + 2000 = 3000 bytes
        fs.AddFileOfSize("/cache/pkg-a/1.0.0/a.bin", 1000)
          .AddFileOfSize("/cache/pkg-a/1.0.0/b.bin", 2000);

        // pkg-b: 5000 bytes
        fs.AddFileOfSize("/cache/pkg-b/2.0.0/c.bin", 5000);

        // pkg-c: 40 bytes, deliberately ragged so a size bug cannot cancel out
        fs.AddFileOfSize("/cache/pkg-c/3.0.0/d.bin", 40);

        return fs;
    }

    private static ScanPipeline Pipeline(FakeFileSystem fs, int concurrency = 4) =>
        new(fs, OperatingSystemKind.Linux, homeDirectory: "/home/dev", maxConcurrency: concurrency);

    [Fact]
    public async Task Sizes_match_fixture_truth_within_two_percent()
    {
        // FR-04: sizes must be within 2% of truth.
        FakeFileSystem fs = SizedFixture();
        ScanResult result = await Pipeline(fs).RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(8040UL, result.Usage.Total);

        Assert.Equal(3000UL, SizeOf(result, "pkg-a"));
        Assert.Equal(5000UL, SizeOf(result, "pkg-b"));
        Assert.Equal(40UL, SizeOf(result, "pkg-c"));
    }

    [Fact]
    public async Task A_hard_linked_file_is_counted_once()
    {
        // FR-05: a unique file must be counted once even when it has two names.
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddFileOfSize("/cache/pkg-a/1.0.0/real.bin", 4096, fileIdentity: "inode-1")
            .AddHardLink("/cache/pkg-a/1.0.0/alias.bin", "/cache/pkg-a/1.0.0/real.bin");

        ScanResult result = await Pipeline(fs).RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Equal(4096UL, SizeOf(result, "pkg-a"));
    }

    [Fact]
    public async Task Long_paths_scan_without_error()
    {
        // FR-05: paths beyond the legacy 260-char limit must not crash the scan. The fake stores
        // them verbatim; the real adapter relies on the long-path form at the IO boundary (E-8).
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);

        // The item path is the ROOT's immediate child (what the enumerator emits), so the depth has
        // to live inside the entry itself for the emitted path to exceed 260 characters.
        string entryName = string.Join('/', Enumerable.Repeat("a-rather-long-directory-name", 6));
        string payload = $"/cache/pkg-a/1.0.0/{entryName}/" + string.Join('/', Enumerable.Repeat("payload-directory", 4)) + "/file.bin";
        fs.AddFileOfSize(payload, 1234);

        ScanResult result = await Pipeline(fs).RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Empty(result.Issues);
        Assert.True(payload.Length > 260, $"fixture should exceed 260 chars, was {payload.Length}");
        Assert.Equal(1234UL, SizeOf(result, "pkg-a"));
    }

    [Fact]
    public async Task Two_runs_produce_byte_identical_output()
    {
        // NFR-06: determinism is the headline non-functional requirement, and the pipeline scans
        // roots in parallel — so this is the test that proves ordering is imposed, not inherited.
        FakeFileSystem fs = SizedFixture();

        ScanResult first = await Pipeline(fs).RunAsync(
        [
            new ScanRoot("nuget-packages", "/cache"),
            new ScanRoot("nuget-http", "/cache-http"),
            new ScanRoot("other", "/other"),
        ]);

        ScanResult second = await Pipeline(fs, concurrency: 8).RunAsync(
        [
            new ScanRoot("nuget-packages", "/cache"),
            new ScanRoot("nuget-http", "/cache-http"),
            new ScanRoot("other", "/other"),
        ]);

        Assert.Equal(first.ToJson(), second.ToJson());
    }

    [Fact]
    public async Task Output_order_does_not_depend_on_concurrency()
    {
        FakeFileSystem fs = SizedFixture();
        var roots = new List<ScanRoot>();
        for (int i = 0; i < 8; i++)
        {
            string root = $"/root{i}";
            fs.AddFileOfSize($"{root}/pkg{i}/1.0.0/x.bin", 100 + i);
            roots.Add(new ScanRoot($"loc{i}", root));
        }

        string serial = (await Pipeline(fs, concurrency: 1).RunAsync(roots)).ToJson();
        string parallel = (await Pipeline(fs, concurrency: 8).RunAsync(roots)).ToJson();

        Assert.Equal(serial, parallel);
    }

    [Fact]
    public async Task Scanning_performs_zero_filesystem_writes()
    {
        // C-1.
        FakeFileSystem fs = SizedFixture();
        await Pipeline(fs).RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task A_root_that_vanished_between_resolution_and_scan_is_reported_not_fatal()
    {
        var fs = SizedFixture();
        fs.AddDirectory("/other");

        ScanResult result = await Pipeline(fs).RunAsync(
        [
            new ScanRoot("nuget-packages", "/cache"),
            new ScanRoot("gone", "/does-not-exist"),
        ]);

        Assert.Equal(3, result.Items.Count);
        ScanIssue issue = Assert.Single(result.Issues);
        Assert.Equal("ROOT-VANISHED", issue.Code);
    }

    [Fact]
    public async Task A_root_with_no_entries_is_reported_as_empty_rather_than_silently_dropped()
    {
        // An empty location is a finding, not an absence: it often means the tool moved its cache.
        var fs = new FakeFileSystem(OperatingSystemKind.Linux).AddDirectory("/empty");
        fs.AddFileOfSize("/cache/pkg-a/1.0.0/a.bin", 100);

        ScanResult result = await Pipeline(fs).RunAsync(
        [
            new ScanRoot("nuget-packages", "/cache"),
            new ScanRoot("empty-loc", "/empty"),
        ]);

        Assert.Contains("empty-loc", result.LocationsWithZeroItems);
        Assert.DoesNotContain("nuget-packages", result.LocationsWithZeroItems);
    }

    [Fact]
    public async Task An_item_pointing_outside_its_root_is_dropped_with_an_issue()
    {
        // A plugin is untrusted. An enumerator that yields a path outside the root must not get it
        // into the plan, where it would later be validated by a different (weaker) code path.
        var fs = SizedFixture();
        var escaping = new StubEnumerator("escaping", "/etc", "secret", 9999);
        var pipeline = new ScanPipeline(fs, OperatingSystemKind.Linux, [escaping], "/home/dev");

        ScanResult result = await pipeline.RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.DoesNotContain(result.Items, i => i.Path.StartsWith("/etc", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Code == "ITEM-ESCAPES-ROOT");
    }

    [Fact]
    public async Task A_failing_enumerator_does_not_abort_the_others()
    {
        var fs = SizedFixture();
        var failing = new ThrowingEnumerator("boom");
        var pipeline = new ScanPipeline(
            fs,
            OperatingSystemKind.Linux,
            [failing, new DirectoryEntryEnumerator(fs)],
            "/home/dev");

        ScanResult result = await pipeline.RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Equal(3, result.Items.Count);
        Assert.Contains(result.Issues, i => i.Code == "ENUMERATOR-FAILED");
    }

    [Fact]
    public async Task Cancellation_stops_the_scan_promptly()
    {
        var fs = SizedFixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Pipeline(fs).RunAsync([new ScanRoot("nuget-packages", "/cache")], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Progress_is_reported_per_item()
    {
        var progress = new RecordingProgress();
        await Pipeline(SizedFixture()).RunAsync([new ScanRoot("nuget-packages", "/cache")], progress);

        Assert.NotEmpty(progress.Events);
        Assert.All(progress.Events, e => Assert.Equal("directory-entry", e.Stage));
    }

    [Fact]
    public async Task Usage_is_split_by_risk_tier()
    {
        // A single enumerator that assigns tiers, so the split is measured without two enumerators
        // both describing the same entry (which would double-count the total).
        var fs = SizedFixture();
        var pipeline = new ScanPipeline(
            fs,
            OperatingSystemKind.Linux,
            [new TieringEnumerator(fs, ("/cache/pkg-a", Risk.Safe), ("/cache/pkg-b", Risk.Review))],
            "/home/dev");

        ScanResult result = await pipeline.RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Equal(8040UL, result.Usage.Total);
        Assert.Equal(3000UL, result.Usage[Risk.Safe]);
        Assert.Equal(5040UL, result.Usage[Risk.Review]);
    }

    [Fact]
    public async Task Two_enumerators_describing_the_same_entry_do_not_double_count()
    {
        // Two providers both covering the same entry is a realistic plugin mistake. The ItemId is
        // the de-duplication key, so the plan can never contain the same path twice with two sizes.
        var fs = SizedFixture();
        var pipeline = new ScanPipeline(
            fs,
            OperatingSystemKind.Linux,
            [new DirectoryEntryEnumerator(fs), new DirectoryEntryEnumerator(fs)],
            "/home/dev");

        ScanResult result = await pipeline.RunAsync([new ScanRoot("nuget-packages", "/cache")]);

        Assert.Equal(8040UL, result.Usage.Total);
        Assert.Equal(result.Items.Count, result.Items.Select(i => i.ItemId).Distinct().Count());
    }

    [Theory]
    [InlineData("newtonsoft.json.13.0.3", "13.0.3", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("pkg-2", "2", true)]
    [InlineData("v2", "", false)]
    [InlineData("pkg-with-no-version", "", false)]
    [InlineData("trailing.", "", false)]
    [InlineData(".hidden", "", false)]
    public void Version_detection_is_honest_about_not_guessing(string name, string expected, bool parsed)
    {
        Assert.Equal(expected, DirectoryEntryEnumerator.DetectVersion(name, out bool actual));
        Assert.Equal(parsed, actual);
    }

    private static ulong SizeOf(ScanResult result, string name) =>
        result.Items.Single(i => i.Name == name).Size;
}

using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Scanning;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Quarantine;

/// <summary>
/// T-032: a scan never inventories quarantine content.
/// <para>
/// Quarantine lives at <c>&lt;root-parent&gt;/.coppice/quarantine/…</c>, outside every scanned root by
/// construction — but a user who pins a root at a PARENT of another root's quarantine (pinning
/// <c>~/go</c> while <c>~/go/pkg/mod</c> is also a root) puts coppice's own state inside a scanned tree.
/// Two layers guard against that: sizing ignores <c>.coppice</c> subtrees (so a fingerprint does not move
/// with every apply), and the pipeline drops any item an enumerator reports from under one.
/// </para>
/// </summary>
public sealed class ScanQuarantineSkipTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";

    [Fact]
    public async Task Quarantine_content_inside_a_scanned_item_is_not_measured_as_cache_bytes()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectory($@"{Root}\pkg\1.0.0");
        fs.AddFile($@"{Root}\pkg\1.0.0\payload.bin", new string('a', 100));

        // Another root's quarantine, nested under this root because the user pinned the parent. The 500
        // bytes under .coppice are coppice's own bookkeeping; sizing them into the item would make the
        // item's fingerprint change with every apply and prune.
        fs.AddDirectory($@"{Root}\pkg\1.0.0\.coppice\quarantine\cache\other\1.0.0");
        fs.AddFile($@"{Root}\pkg\1.0.0\.coppice\quarantine\cache\other\1.0.0\payload.bin", new string('b', 500));

        var pipeline = new ScanPipeline(fs, Win);
        ScanResult result = await pipeline.RunAsync([new ScanRoot("cache", Root)]);

        Item item = Assert.Single(result.Items);
        Assert.Equal("pkg", item.Name);

        // THE assertion: 100, not 600. The fake's MeasureSize and the real one must agree on ignoring
        // the .coppice subtree, or a test can pass against the fake and fail on a real machine.
        Assert.Equal(100UL, item.Size);
    }

    [Fact]
    public async Task An_item_reported_from_inside_quarantine_is_dropped_with_an_issue()
    {
        // A quarantine sitting DIRECTLY under the root: the default enumerator sees .coppice as an
        // immediate child and would happily inventory it — including a plan that quarantines the
        // quarantine. The pipeline refuses the item instead of trusting enumerators to filter.
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectory($@"{Root}\.coppice\quarantine\cache\pkg\1.0.0");
        fs.AddFile($@"{Root}\.coppice\quarantine\cache\pkg\1.0.0\payload.bin", new string('c', 4096));

        var pipeline = new ScanPipeline(fs, Win);
        ScanResult result = await pipeline.RunAsync([new ScanRoot("cache", Root)]);

        Assert.Empty(result.Items);
        Assert.Contains(result.Issues, i => i.Code == "QUARANTINE-SKIPPED");

        // And the scan stays read-only: skipping is not deleting.
        Assert.Empty(fs.Writes);
        Assert.True(fs.DirectoryExists($@"{Root}\.coppice\quarantine\cache\pkg\1.0.0"));
    }
}

using System.Diagnostics;
using Coppice.Application;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Coppice.Tests.Performance;

/// <summary>
/// T-053: a deterministic 30k-item / ~30 GB logical scan. The fake filesystem measures enumeration,
/// sorting and snapshot work without allocating 30 GB of real bytes; real-volume I/O is measured on a
/// reference machine, not in the unit suite.
/// </summary>
public sealed class ScanPerfTests
{
    private const string Cache = "/home/dev/.nuget/packages";
    private const int ItemCount = 30_000;
    private const int ItemBytes = 1024 * 1024;
    private static readonly DateTimeOffset Stamp = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public ScanPerfTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Thirty_thousand_items_scan_under_sixty_seconds()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        for (int i = 0; i < ItemCount; i++)
        {
            fs.AddSparseDirectoryOfSize($"{Cache}/pkg{i:D5}/1.0.0", ItemBytes);
        }

        fs.UseFastEnumeration = true;

        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var state = new FakeStateStore();
        var clock = new FakeClock(Stamp);
        var registry = EcosystemRegistry.CreateDefault(fs);
        var service = new ScanService(fs, new FakeProcessRunner(), env, state, clock, registry);

        var root = new Ports.ScanRoot(
            "nuget-packages",
            Cache,
            "package-cache",
            Ports.RootRole.Active,
            new Ports.FingerprintSpec(),
            "user",
            Ports.Risk.Safe);

        var stopwatch = Stopwatch.StartNew();
        ScanReport report = await service.RunAsync([root], [], CancellationToken.None, []);
        stopwatch.Stop();

        long bytes = report.Items.Sum(item => (long)item.Size);
        _output.WriteLine(
            $"scan: {report.Items.Count} items, {bytes} bytes, {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        Assert.Equal(ItemCount, report.Items.Count);
        Assert.True(bytes >= 30_000_000_000, $"expected about 30 GB of logical size, got {bytes} bytes");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"30k-item scan took {stopwatch.Elapsed.TotalSeconds:F1} s");
    }
}

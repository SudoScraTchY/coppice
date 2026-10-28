using Coppice.Cli.Tui;
using Coppice.Core.Domain;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Tui;

/// <summary>
/// T-074: a rescan and an apply preparation must resolve the same roots the Roots screen shows. A
/// configured pin reaches every TUI composition root, so the rescan finds the pinned cache the initial
/// load already reported instead of falling back to the environment default.
/// </summary>
public sealed class TuiBackendTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string DefaultCache = "/home/dev/.nuget/packages";
    private const string PinnedCache = "/home/dev/pinned-nuget";

    [Fact]
    public async Task A_rescan_resolves_the_pinned_cache_rather_than_the_environment_default()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory(DefaultCache)
            .AddFile($"{DefaultCache}/default.pkg/1.0.0/payload.bin", "default")
            .AddDirectory(PinnedCache)
            .AddFile($"{PinnedCache}/pinned.pkg/1.0.0/payload.bin", "pinned");

        var env = new FakeEnvironment(OperatingSystemKind.Linux).With("NUGET_PACKAGES", DefaultCache);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nuget-packages"] = PinnedCache,
        };

        var backend = new TuiBackend(
            fs,
            new FakeProcessRunner(),
            env,
            new FakeStateStore(),
            new FakeAppendLog(),
            new FakeClock(Stamp),
            [],
            pins);

        TuiData data = await backend.ScanAsync();

        Assert.NotNull(data.Snapshot);

        // The regression: without the pin, the rescan resolves NUGET_PACKAGES and finds only
        // default.pkg. With it, the pinned cache is the Active root and its package is scanned.
        // The environment root stays a live Additional candidate (a pin chooses the winner, it does
        // not silence the sources it replaced), so it is scanned too; that is the same set of roots
        // the Roots screen already showed.
        Assert.Contains(data.Snapshot!.Items, item => item.Name == "pinned.pkg");
        Assert.Contains(
            data.Snapshot.Items,
            item => item.Name == "pinned.pkg" && item.Path.StartsWith(PinnedCache + "/", StringComparison.Ordinal));

        RootReport nuget = Assert.Single(
            data.Roots.Roots,
            root => root.LocationId == "nuget-packages" && root.Root.Via == ResolvedVia.Pin);
        Assert.Equal(PinnedCache, nuget.Root.RealPath);
        Assert.Equal(RootRole.Active, nuget.Root.Role);
    }
}

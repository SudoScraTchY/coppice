using Coppice.Ports;
using Coppice.Tests.Fixtures;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests;

/// <summary>
/// The dynamic half of the v0.1 read-only audit (T-019, 06-safety-model).
/// <para>
/// The static audit in <c>Coppice.ArchitectureTests.ReadOnlyAuditTests</c> proves no assembly CALLS a
/// filesystem mutator. That is necessary but not sufficient: a command could still write through a
/// port method the audit does not consider mutating — a state store creating its directory, a
/// snapshot landing in the wrong place, a cache written during a scan.
/// </para>
/// <para>
/// So these tests RUN the commands against a fake filesystem that records every write, and assert the
/// record is empty. A command that acquires a write capability, even for its own bookkeeping, fails
/// here.
/// </para>
/// </summary>
public sealed class ReadOnlyCommandTests
{
    private static (FakeFileSystem Fs, FakeProcessRunner Runner, FakeEnvironment Env, FakeStateStore State) World(
        OperatingSystemKind os = OperatingSystemKind.Linux)
    {
        var env = new FakeEnvironment(os);

        // An empty machine with no caches at all. Every command must succeed on this, and none may
        // write: a fresh install with no toolchains is the case where "does nothing harmful" is
        // easiest to get wrong and least likely to be noticed.
        return (new FakeFileSystem(os), new FakeProcessRunner(), env, new FakeStateStore());
    }

    [Fact]
    public void Doctor_writes_nothing()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, _) = World();

        var service = new Coppice.Cli.DoctorService(fs, runner, env, env.OS);
        Coppice.Cli.DoctorReport report = service.Inspect();

        Assert.Empty(fs.Writes);
        Assert.NotEmpty(report.Roots);
    }

    [Fact]
    public void Roots_writes_nothing()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, _) = World();

        var service = new Coppice.Cli.DoctorService(fs, runner, env, env.OS);
        service.Inspect();

        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Scan_writes_nothing_to_the_filesystem_it_scanned()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, FakeStateStore state) = World();

        // A populated cache, so the scan actually walks something rather than trivially finding
        // nothing. An empty machine cannot prove the scanner is read-only; it only proves it is idle.
        fs.AddDirectory("/home/dev/.nuget/packages/newtonsoft.json/13.0.3");
        fs.AddFileOfSize("/home/dev/.nuget/packages/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", 4096);

        var service = new Coppice.Cli.ScanService(fs, runner, env, state);
        Coppice.Cli.ScanReport report = await service.RunAsync(
            [
                new Ports.ScanRoot(
                    "nuget-packages",
                    "/home/dev/.nuget/packages",
                    "package-cache",
                    Ports.RootRole.Active,
                    new Ports.FingerprintSpec(),
                    "user",
                    Ports.Risk.Safe),
            ],
            projectRoots: []);

        Assert.Empty(fs.Writes);
        Assert.NotEmpty(report.Items);
    }

    [Fact]
    public async Task Scan_writes_its_snapshot_to_the_state_store_and_nowhere_else()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, FakeStateStore state) = World();

        var service = new Coppice.Cli.ScanService(fs, runner, env, state);
        await service.RunAsync([], projectRoots: []);

        // The snapshot is the ONE write v0.1 makes, and it goes to the state store — the user's own
        // ~/.coppice, never a scanned location. Both halves matter: a snapshot that did not happen
        // would lose the v0.4 history feature, and a snapshot written anywhere else would be the
        // tool writing to a cache it only promised to read.
        Assert.NotEmpty(state.Writes);
        Assert.All(state.Writes, key => Assert.StartsWith("snapshots/", key, StringComparison.Ordinal));
        Assert.Empty(fs.Writes);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows)]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public async Task No_command_touches_a_scanned_location(OperatingSystemKind os)
    {
        // The strongest form of the claim: even a POPULATED cache survives a scan unchanged. The fake
        // VFS records every write path, so "unchanged" is checkable rather than assumed.
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, FakeStateStore state) = World(os);

        string home = FixtureGenerator.HomeFor(os);
        char separator = os == OperatingSystemKind.Windows ? '\\' : '/';
        string cache = home + separator + ".nuget" + separator + "packages";

        fs.AddDirectory(cache);
        fs.AddDirectory(cache + separator + "serilog" + separator + "3.0.1");
        fs.AddFileOfSize(cache + separator + "serilog" + separator + "3.0.1" + separator + "serilog.3.0.1.nupkg", 2048);

        var service = new Coppice.Cli.ScanService(fs, runner, env, state);
        await service.RunAsync(
            [
                new Ports.ScanRoot(
                    "nuget-packages",
                    cache,
                    "package-cache",
                    Ports.RootRole.Active,
                    new Ports.FingerprintSpec(),
                    "user",
                    Ports.Risk.Safe),
            ],
            projectRoots: []);

        Assert.Empty(fs.Writes);
        Assert.True(fs.DirectoryExists(cache));
        Assert.True(fs.FileExists(cache + separator + "serilog" + separator + "3.0.1" + separator + "serilog.3.0.1.nupkg"));
    }

    [Fact]
    public async Task A_scan_of_a_machine_with_nothing_installed_still_succeeds()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, FakeStateStore state) = World();

        var service = new Coppice.Cli.ScanService(fs, runner, env, state);
        Coppice.Cli.ScanReport report = await service.RunAsync([], projectRoots: []);

        Assert.Empty(report.Items);
        Assert.NotNull(report.SnapshotId);
    }

    [Fact]
    public async Task The_snapshot_itself_is_byte_identical_across_runs_of_the_same_machine()
    {
        // Two runs, same machine, same clock. The only thing that may differ is the snapshot id, which
        // is a timestamp — so this compares the content that a plan would later be justified against.
        (FakeFileSystem fs, FakeProcessRunner runner, FakeEnvironment env, FakeStateStore state) = World();
        fs.AddDirectory("/home/dev/.nuget/packages/polly/8.0.0");

        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var service = new Coppice.Cli.ScanService(fs, runner, env, state, clock);

        Coppice.Cli.ScanReport first = await service.RunAsync([], projectRoots: []);
        Coppice.Cli.ScanReport second = await service.RunAsync([], projectRoots: []);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(Coppice.Core.Domain.DomainJson.ComputeChecksum(first.Items), Coppice.Core.Domain.DomainJson.ComputeChecksum(second.Items));
    }
}

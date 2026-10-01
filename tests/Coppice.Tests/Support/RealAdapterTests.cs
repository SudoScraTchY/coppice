using Coppice.Adapters;
using Coppice.Ports;

namespace Coppice.Tests.Support;

/// <summary>
/// Card T-002 acceptance for the real adapters. These touch a temp directory, never a real toolchain
/// cache, and never the user's home.
/// </summary>
public sealed class RealAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coppice-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public RealAdapterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    [Fact]
    public void PhysicalFileSystem_round_trips_a_tree()
    {
        var fs = new PhysicalFileSystem();
        string nested = Path.Combine(_root, "pkg", "1.0.0");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "a.txt"), "hello");

        Assert.True(fs.DirectoryExists(nested));
        Assert.True(fs.FileExists(Path.Combine(nested, "a.txt")));
        Assert.Equal("hello", fs.ReadSmallText(Path.Combine(nested, "a.txt")));

        FileEntry entry = fs.GetEntry(Path.Combine(nested, "a.txt"))!;
        Assert.Equal(EntryKind.File, entry.Kind);
        Assert.Equal(5, entry.Length);
    }

    [Fact]
    public void MeasureSize_counts_each_file_once()
    {
        var fs = new PhysicalFileSystem();
        string dir = Path.Combine(_root, "sized");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[250]);

        Assert.Equal(350UL, fs.MeasureSize(dir));
    }

    [Fact]
    public void Enumeration_finds_nested_files()
    {
        var fs = new PhysicalFileSystem();
        Directory.CreateDirectory(Path.Combine(_root, "x", "y"));
        File.WriteAllText(Path.Combine(_root, "x", "y", "deep.txt"), "d");

        IReadOnlyList<FileEntry> entries = fs.EnumerateEntries(_root);
        Assert.Contains(entries, e => e.Name == "deep.txt");
    }

    [Fact]
    public void Move_and_delete_work()
    {
        var fs = new PhysicalFileSystem();
        string source = Path.Combine(_root, "movable");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "f.txt"), "x");

        fs.Move(source, Path.Combine(_root, "moved"));
        Assert.False(fs.DirectoryExists(source));
        Assert.True(fs.FileExists(Path.Combine(_root, "moved", "f.txt")));

        fs.DeleteFile(Path.Combine(_root, "moved"), recursive: true);
        Assert.False(fs.DirectoryExists(Path.Combine(_root, "moved")));
    }

    [Fact]
    public async Task ProcessRunner_captures_output_and_exit_code()
    {
        var runner = new SystemProcessRunner();
        (string file, string[] args) = Echo();
        ProcessResult result = await runner.RunAsync(new ProcessRequest
        {
            FileName = file,
            Arguments = args,
            Timeout = TimeSpan.FromSeconds(60),
        });

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("COPPICE_SMOKE", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunner_times_out_a_hanging_process()
    {
        var runner = new SystemProcessRunner();
        (string file, string[] args) = Hang();
        ProcessResult result = await runner.RunAsync(new ProcessRequest
        {
            FileName = file,
            Arguments = args,
            Timeout = TimeSpan.FromMilliseconds(400),
        });

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task ProcessRunner_reports_a_missing_executable_without_throwing()
    {
        var runner = new SystemProcessRunner();
        ProcessResult result = await runner.RunAsync(new ProcessRequest
        {
            FileName = "coppice-definitely-not-a-real-binary-xyz",
            Timeout = TimeSpan.FromSeconds(10),
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task FileSystemStateStore_round_trips_and_lists()
    {
        var store = new FileSystemStateStore(Path.Combine(_root, "state"), new SystemClock());
        await store.WriteAsync("snapshots/2026-01-01.json", "application/json", "{\"a\":1}");
        await store.WriteAsync("snapshots/2026-01-02.json", "application/json", "{\"a\":2}");

        StateDocument? doc = await store.ReadAsync("snapshots/2026-01-01.json");
        Assert.Equal("{\"a\":1}", doc!.Json);
        Assert.Equal(["snapshots/2026-01-01.json", "snapshots/2026-01-02.json"], store.ListKeys("snapshots/"));

        await store.DeleteAsync("snapshots/2026-01-01.json");
        Assert.Null(await store.ReadAsync("snapshots/2026-01-01.json"));
    }

    [Fact]
    public async Task StateStore_refuses_a_key_that_escapes_its_root()
    {
        var store = new FileSystemStateStore(Path.Combine(_root, "state2"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("../escape.json", "application/json", "{}"));
    }

    [Fact]
    public void SystemEnvironment_reports_a_known_os_and_home()
    {
        var env = new SystemEnvironment();
        Assert.NotEqual(OperatingSystemKind.Unknown, env.OS);
        Assert.False(string.IsNullOrEmpty(env.HomeDirectory));
    }

    [Fact]
    public void SystemEnvironment_lists_path_entries()
    {
        var env = new SystemEnvironment();
        Assert.NotEmpty(env.PathEntries);
    }

    // ArgumentList quotes each argument for us, so the command itself must NOT carry quotes:
    // wrapping it in quotes makes the shell try to execute the word `"echo` as a command, which
    // exits 2 instead of echoing — and the hanging-process test then times out on a process that
    // had already exited.
    private static (string File, string[] Args) Echo() => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/c", "echo COPPICE_SMOKE"])
        : ("/bin/sh", ["-c", "echo COPPICE_SMOKE"]);

    private static (string File, string[] Args) Hang() => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/c", "ping -n 30 127.0.0.1 > nul"])
        : ("/bin/sh", ["-c", "sleep 30"]);
}

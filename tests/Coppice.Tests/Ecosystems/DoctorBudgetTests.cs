using System.Collections.Concurrent;
using System.Diagnostics;
using Coppice.Application;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Coppice.Tests.Ecosystems;

/// <summary>
/// T-053: a per-probe timeout is not a total budget. Several hung tools must not hold `doctor` past
/// NFR-04's 5 s; the budget-expired probes are reported instead of looking absent.
/// </summary>
[Collection("Timing")]
public sealed class DoctorBudgetTests
{
    private readonly ITestOutputHelper _output;

    public DoctorBudgetTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_hung_tool_cannot_hold_doctor_past_its_total_budget()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var registry = EcosystemRegistry.CreateDefault(fs);
        var doctor = new DoctorService(
            fs,
            new BlockingProcessRunner(),
            env,
            env.OS,
            registry,
            probeBudget: TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        DoctorReport report = doctor.Inspect();
        stopwatch.Stop();
        _output.WriteLine($"doctor returned after {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine(string.Join("\n", report.Issues));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"doctor took {stopwatch.Elapsed.TotalMilliseconds:F0} ms with a 100 ms probe budget");
        Assert.Contains(report.Issues, issue => issue.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_slow_dotnet_probe_does_not_starve_the_other_ecosystems()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var runner = new SlowDotnetRunner();
        var registry = EcosystemRegistry.CreateDefault(fs);
        var doctor = new DoctorService(
            fs,
            runner,
            env,
            env.OS,
            registry,
            probeBudget: TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        DoctorReport report = doctor.Inspect();
        stopwatch.Stop();
        _output.WriteLine($"doctor returned after {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine(string.Join("\n", runner.Calls));

        Assert.Contains("go", runner.Calls);
        Assert.Contains("npm", runner.Calls);
        Assert.Contains(report.Issues, issue => issue.Contains("budget", StringComparison.OrdinalIgnoreCase));

        // The behavior above is the assertion that matters. This bound only catches a report that
        // never returns: on a 2-core CI runner the blocked group tasks can delay the thread pool's
        // cancellation callbacks by seconds even though the 500 ms budget has fired.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(6),
            $"doctor took {stopwatch.Elapsed.TotalMilliseconds:F0} ms with a 500 ms probe budget");
    }

    [Fact]
    public void A_group_fault_surfaces_without_leaking_the_detection_task()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var registry = EcosystemRegistry.CreateDefault(fs);
        var doctor = new DoctorService(
            fs,
            new ThrowingRunner(),
            env,
            env.OS,
            registry,
            probeBudget: TimeSpan.FromSeconds(1));

        // The fault must reach the caller (not be swallowed), while the concurrently running
        // detection task is observed by the group-fault path instead of leaking.
        Assert.Throws<NotSupportedException>(() => doctor.Inspect());
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.FileName == "go")
            {
                throw new NotSupportedException("synthetic group fault");
            }

            return Task.FromResult(new ProcessResult
            {
                ExitCode = 0,
                StandardOutput = "/not/a/cache",
                ResolvedFileName = request.FileName,
            });
        }
    }

    private sealed class SlowDotnetRunner : IProcessRunner
    {
        public ConcurrentBag<string> Calls { get; } = [];

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Calls.Add(request.FileName);

            if (request.FileName == "dotnet")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return new ProcessResult
            {
                ExitCode = 0,
                StandardOutput = "/not/a/cache",
                ResolvedFileName = request.FileName,
            };
        }
    }

    private sealed class BlockingProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}

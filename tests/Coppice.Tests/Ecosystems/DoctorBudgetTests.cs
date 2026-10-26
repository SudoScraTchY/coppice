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

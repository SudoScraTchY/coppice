using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Native;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Native;

/// <summary>
/// T-033: the native runner (FR-15, S-15, NFR-07, the 06 asymmetry rule).
/// <para>
/// Every execution test drives a native step THROUGH the gateway — the gateway is the only mutating
/// code path, and a native command mutates the machine — against a scripted <see cref="FakeProcessRunner"/>,
/// which is the sandbox the acceptance criteria name. The runner records every request, so "executes and
/// is audited with exit codes" is a comparison, not a claim: the test asserts the request the tool will
/// actually receive (file name, argument list, deadline) and the result the audit log will read.
/// </para>
/// </summary>
public sealed class NativeCommandTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";
    private const ulong RootSize = 4096;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// A cache root whose live state matches the fingerprint the step carries, so the drift gate lets a
    /// step through unless a test deliberately breaks the two apart.
    /// </summary>
    private static FakeFileSystem Cache()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\go-pkg\1.0.0", (int)RootSize);
        return fs;
    }

    private static GatewayStep Step(
        string commandLine,
        Risk risk = Risk.Safe,
        ulong fingerprintSize = RootSize) =>
        new()
        {
            ItemId = "go-mod-cache",
            Path = Root,
            Root = Root,
            AllowlistPattern = "",
            Risk = risk,
            Action = RemovalAction.Native(commandLine),
            Fingerprint = new SnapshotFingerprint(Root, fingerprintSize, DateTimeOffset.UnixEpoch),
        };

    private static (FakeFileSystem Fs, FakeProcessRunner Runner, Gateway Gateway) World(
        FakeFileSystem? fs = null,
        FakeProcessRunner? runner = null)
    {
        fs ??= Cache();
        runner ??= new FakeProcessRunner();
        return (fs, runner, new Gateway(fs, Win, clock: new FixedClock(), processes: runner));
    }

    // ---- FR-15: executes and is audited with exit codes ------------------------------------------------

    [Fact]
    public async Task A_sandboxed_native_command_executes_and_is_reported_with_its_exit_code()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, Gateway gateway) = World();
        runner.Respond("go", "cleaned module cache\n", exitCode: 0);

        GatewayStepResult result = await gateway.Execute(Step("go clean -modcache"), confirmed: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(GatewayOutcome.Executed, result.Result);
        Assert.Equal(GatewayRefusal.None, result.Refusal);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cleaned module cache\n", result.Output);

        // "Audited" starts with the request being exactly what the spec says the tool receives: the
        // parsed command, coppice's own deadline, and NO working directory or environment shaped by
        // the gateway — neutrality is the adapter's job (04-plugin-contract rule 2, E-11).
        ProcessRequest request = Assert.Single(runner.Calls);
        Assert.Equal("go", request.FileName);
        Assert.Equal(new[] { "clean", "-modcache" }, request.Arguments);
        Assert.Equal(Gateway.NativeCommandTimeout, request.Timeout);
        Assert.Null(request.WorkingDirectory);
        Assert.Null(request.Environment);

        // The mutation is the tool's, not coppice's: the fake VFS records no writes.
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task A_nonzero_exit_is_a_failed_step_not_a_crash()
    {
        (FakeFileSystem _, FakeProcessRunner runner, Gateway gateway) = World();
        runner.Fail("go", 3, "go: cannot clean: file is locked by another process");

        GatewayStepResult result = await gateway.Execute(Step("go clean -modcache"), confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayOutcome.Failed, result.Result);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("exit code 3", result.Message, StringComparison.Ordinal);
        Assert.Contains("file is locked", result.Message, StringComparison.Ordinal);
        Assert.NotEmpty(result.Error);
    }

    // ---- S-15 / NFR-07: timeouts -----------------------------------------------------------------------

    [Fact]
    public async Task A_hanging_tool_is_killed_at_the_deadline_and_fails_the_step()
    {
        (FakeFileSystem _, FakeProcessRunner runner, Gateway gateway) = World();
        runner.Hangs("go", TimeSpan.FromHours(1));

        GatewayStepResult result = await gateway.Execute(Step("go clean -modcache"), confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayOutcome.Failed, result.Result);
        Assert.Equal(GatewayRefusal.TimedOut, result.Refusal);
        Assert.Contains("killed after", result.Message, StringComparison.Ordinal);

        // No hang: the fake reports TimedOut immediately and the gateway maps it without waiting. The
        // real runner kills the whole process tree at the deadline (SystemProcessRunner, NFR-07).
    }

    // ---- the asymmetry gates ---------------------------------------------------------------------------

    [Fact]
    public async Task A_native_command_without_explicit_confirmation_is_refused()
    {
        (FakeFileSystem fs, FakeProcessRunner runner, Gateway gateway) = World();

        GatewayStepResult result = await gateway.Execute(Step("go clean -modcache"));

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.NeedsConfirmation, result.Refusal);
        Assert.Contains("--yes", result.Message, StringComparison.Ordinal);

        // Refusal means refusal: the tool was never invoked, and the cache is untouched.
        Assert.Empty(runner.Calls);
        Assert.Empty(fs.Writes);
        Assert.True(fs.DirectoryExists($@"{Root}\go-pkg\1.0.0"));
    }

    [Fact]
    public async Task A_manual_tier_native_command_is_refused_even_when_confirmed()
    {
        // S-7: confirmation is consent, not an elevation grant. A Review-tier native command (rustup
        // toolchain uninstall) can run confirmed; a Manual-tier one cannot, at any level of consent.
        (FakeFileSystem _, FakeProcessRunner runner, Gateway gateway) = World();

        GatewayStepResult result = await gateway.Execute(
            Step("msiexec /x somesdk", risk: Risk.Manual),
            confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.NeedsElevation, result.Refusal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_native_command_on_a_drifted_root_is_refused_even_without_strict()
    {
        // The asymmetry rule's "fingerprint-valid root", with no --strict escape hatch: there is no
        // quarantine to undo a native command with, so a snapshot mismatch always refuses.
        (FakeFileSystem _, FakeProcessRunner runner, Gateway gateway) = World();

        GatewayStepResult result = await gateway.Execute(
            Step("go clean -modcache", fingerprintSize: 999_999),
            confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.Drifted, result.Refusal);
        Assert.Contains("changed since the scan", result.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_native_command_on_a_root_that_has_vanished_is_refused()
    {
        // Unlike a path delete — where "gone" is the goal state — a native command is justified by a
        // snapshot of a cache that exists. A fake with nothing on it means the root is gone: drift.
        (FakeFileSystem _, FakeProcessRunner runner, Gateway gateway) = World(new FakeFileSystem(Win));

        GatewayStepResult result = await gateway.Execute(Step("go clean -modcache"), confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.Drifted, result.Refusal);
        Assert.Contains("no longer exists", result.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_native_command_without_a_configured_runner_is_refused()
    {
        var fs = Cache();
        var gateway = new Gateway(fs, Win, clock: new FixedClock());

        GatewayStepResult result = await gateway.Execute(Step("go clean -modcache"), confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.NativeUnavailable, result.Refusal);
        Assert.Contains("no process runner", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_native_step_with_an_empty_command_is_refused_as_a_plan_defect()
    {
        (FakeFileSystem _, FakeProcessRunner runner, Gateway gateway) = World();

        GatewayStepResult result = await gateway.Execute(Step("   "), confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.PlanInvalid, result.Refusal);
        Assert.Empty(runner.Calls);
    }

    // ---- the command-line splitter ---------------------------------------------------------------------

    [Theory]
    [InlineData("go clean -modcache", "go", "clean|-modcache")]
    [InlineData("\"C:\\Program Files\\Go\\bin\\go.exe\" clean -modcache", "C:\\Program Files\\Go\\bin\\go.exe", "clean|-modcache")]
    [InlineData("dotnet tool uninstall -g \"my tool\"", "dotnet", "tool|uninstall|-g|my tool")]
    [InlineData("rustup toolchain uninstall 1.83.0", "rustup", "toolchain|uninstall|1.83.0")]
    public void A_command_line_is_split_into_a_file_name_and_arguments(string commandLine, string expectedFile, string expectedArgs)
    {
        (string fileName, IReadOnlyList<string> arguments) = NativeCommandLine.Split(commandLine);

        Assert.Equal(expectedFile, fileName);
        Assert.Equal(expectedArgs.Split('|'), arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_command_line_yields_no_file_name(string commandLine)
    {
        (string fileName, IReadOnlyList<string> arguments) = NativeCommandLine.Split(commandLine);

        Assert.Equal(string.Empty, fileName);
        Assert.Empty(arguments);
    }
}

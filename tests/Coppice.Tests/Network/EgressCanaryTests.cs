using System.Net.Sockets;
using Xunit;

namespace Coppice.Tests.Network;

/// <summary>
/// The egress canary (T-018, NFR-02/NFR-03).
/// <para>
/// The egress-blocked CI job is supposed to prove the tool never touches the network. It cannot do
/// that on trust: if a future change added an <c>HttpClient</c>, the job would still pass, because a
/// blocked-egress environment only catches a network call if something actually tries one.
/// </para>
/// <para>
/// This test is the something. It deliberately opens a TCP connection to a public address and
/// asserts the connection FAILS. Under the blocked-egress job it passes, having proven egress really
/// is blocked. Run anywhere with egress, it fails — and that failure is the signal that the job's
/// green result is worthless.
/// </para>
/// <para>
/// It is tagged <c>Network</c> so the ordinary build runs exclude it. Excluding it is what makes the
/// canary meaningful: the normal suite must be network-free, so the one test that needs network must
/// be opt-in and run only in the job that is verifying it.
/// </para>
/// </summary>
[Trait("Category", "Network")]
public sealed class EgressCanaryTests
{
    // A well-known public address. Not a coppice host: the point is to prove the ENVIRONMENT blocks
    // egress, not that this project happens to have nothing to do with the destination.
    private const string CanaryHost = "1.1.1.1";
    private const int CanaryPort = 443;

    /// <summary>
    /// Fails loudly if egress is open. Skipped rather than failed in an ordinary test run, because a
    /// developer with internet access has not done anything wrong.
    /// </summary>
    [Fact]
    public async Task Egress_is_blocked_in_this_environment()
    {
        bool reachable = await CanReachAsync(CanaryHost, CanaryPort, TimeSpan.FromSeconds(5));

        // Reachable means the egress guard is not doing its job. Failing here is the whole point: a
        // green egress job that let a connection through would otherwise be indistinguishable from a
        // green egress job that blocked one.
        Assert.False(
            reachable,
            $"Egress to {CanaryHost}:{CanaryPort} SUCCEEDED. This job is supposed to run with egress "
            + "blocked. If you are running the suite locally with internet access, this failure is "
            + "expected — filter it out with --filter 'Category!=Network'. If you are running the "
            + "egress job in CI, the network isolation is broken and the job's result means nothing.");
    }

    /// <summary>
    /// The companion check: a connection to a name that cannot resolve must fail even when egress is
    /// open. This distinguishes "egress blocked" from "DNS is flaky", so a canary failure is
    /// actionable rather than ambiguous.
    /// </summary>
    [Fact]
    public async Task A_bogus_host_cannot_be_reached()
    {
        bool reachable = await CanReachAsync("this-host-does-not-exist.invalid", 443, TimeSpan.FromSeconds(5));
        Assert.False(reachable, "an unresolvable host was reachable, which is not how DNS works.");
    }

    private static async Task<bool> CanReachAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            Task connect = client.ConnectAsync(host, port);
            Task completed = await Task.WhenAny(connect, Task.Delay(timeout));

            return completed == connect && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            // Blocked, refused, timed out, or unresolvable: all of which mean "not reachable", which is
            // the only fact this helper is responsible for reporting.
            return false;
        }
    }
}

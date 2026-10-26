using Coppice.Application;
using Coppice.Core.Domain;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Config;

/// <summary>
/// T-052: a config pin must reach the resolver that every command uses, and it must still be refused by
/// the denylist and fingerprint gates. The resolution-level PinAuthorityTests prove the gates; these
/// tests prove the wiring from a config map into DoctorService.
/// </summary>
public sealed class ConfigPinWiringTests
{
    private const string Location = "nuget-packages";

    private static DoctorService Doctor(string pinPath, Action<FakeFileSystem>? setup = null)
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddDirectory("/home/dev");
        setup?.Invoke(fs);

        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var registry = EcosystemRegistry.CreateDefault(fs);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Location] = pinPath,
        };

        return new DoctorService(fs, new FakeProcessRunner(), env, env.OS, registry, pins);
    }

    private static ResolvedRoot PinRoot(DoctorReport report) =>
        Assert.Single(
            report.Roots,
            reportRoot => reportRoot.LocationId == Location && reportRoot.Root.Via == ResolvedVia.Pin).Root;

    [Fact]
    public void A_config_pin_reaches_the_resolver_and_is_reported_as_the_source()
    {
        DoctorService doctor = Doctor("/pin/cache", fs => fs.AddDirectory("/pin/cache/newtonsoft.json/1.0.0"));

        ResolvedRoot root = PinRoot(doctor.Inspect());

        Assert.Equal("/pin/cache", root.DeclaredPath);
        Assert.Equal(RootValidity.Ok, root.Validity);
    }

    [Fact]
    public void A_config_pin_to_a_denylisted_path_is_denied()
    {
        DoctorService doctor = Doctor("/etc", fs => fs.AddDirectory("/etc"));

        DoctorReport report = doctor.Inspect();
        ResolvedRoot root = PinRoot(report);

        Assert.Equal(RootValidity.Denied, root.Validity);
        Assert.Contains(report.Issues, issue => issue.Contains("DENIED", StringComparison.Ordinal));
    }

    [Fact]
    public void A_config_pin_that_fails_the_fingerprint_is_not_ok()
    {
        DoctorService doctor = Doctor("/home/dev/empty", fs =>
        {
            fs.AddDirectory("/home/dev/empty");
            fs.AddFile("/home/dev/empty/notes.txt", "not a package");
        });

        ResolvedRoot root = PinRoot(doctor.Inspect());

        Assert.Equal(RootValidity.FailsFingerprint, root.Validity);
    }
}

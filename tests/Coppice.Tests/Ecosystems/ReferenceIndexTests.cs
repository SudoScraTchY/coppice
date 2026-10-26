using Coppice.Plugins.Net;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
using ReferenceVerdict = Coppice.Ports.ReferenceVerdict;

namespace Coppice.Tests.Ecosystems;

/// <summary>
/// T-058: the port resolver must read each project's assets file once per project set, not once per
/// package. Re-reading per item is what turned a real 3,152-package scan into 190 s; this asserts the
/// index is built once and still returns the same verdicts.
/// </summary>
public sealed class ReferenceIndexTests
{
    [Fact]
    public void Resolve_reads_each_project_once_per_project_set()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Windows)
            .AddDirectory(@"C:\src\a\obj")
            .AddFile(@"C:\src\a\obj\project.assets.json", Assets("pkg.a/1.0.0", "pkg.b/2.0.0"))
            .AddDirectory(@"C:\src\b\obj")
            .AddFile(@"C:\src\b\obj\project.assets.json", Assets("pkg.a/1.0.0"));

        var projects = new Ports.ProjectSet(
        [
            new Ports.Project(@"C:\src\a", [], ["dotnet"]),
            new Ports.Project(@"C:\src\b", [], ["dotnet"]),
        ],
        ["dotnet"],
        []);

        var ecosystem = new DotnetEcosystem(fs);

        ReferenceVerdict first = ecosystem.Resolve("pkg.a", "1.0.0", projects);
        ReferenceVerdict second = ecosystem.Resolve("pkg.b", "2.0.0", projects);
        ReferenceVerdict missing = ecosystem.Resolve("pkg.c", "3.0.0", projects);

        Assert.Equal(Ports.Usage.Referenced, first.Usage);
        Assert.Equal(2, first.ReferencingProjects.Count);
        Assert.Equal(Ports.Usage.Referenced, second.Usage);
        Assert.Equal(Ports.Usage.Unreferenced, missing.Usage);

        // Two projects, one assets read each. Without the index this would be six reads for three
        // items, which is the shape that made the scan O(items x projects).
        Assert.Equal(2, fs.ReadSmallTextCount);
    }

    private static string Assets(params string[] packages) =>
        "{\"targets\":{\"net10.0\":{"
        + string.Join(",", packages.Select(p => $"\"{p}\":{{\"type\":\"package\"}}"))
        + "}}}";
}

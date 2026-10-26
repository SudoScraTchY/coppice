using Coppice.Adapters;
using Coppice.Ports;
using Xunit;

namespace Coppice.ArchitectureTests;

/// <summary>
/// T-050: the GUI's filesystem and process access stays behind a port.
/// <para>
/// The preference file used to be written from the GUI with <c>System.IO</c>. That was small, but it
/// made the shell depend on a real profile and gave it a second way to touch the disk. The port lives
/// in <see cref="Coppice.Ports"/> and the file-backed implementation in Coppice.Adapters; the GUI
/// project itself must contain neither a file API nor a process launch.
/// </para>
/// </summary>
public sealed class GuiBoundaryTests
{
    /// <summary>
    /// Tokens that mean "the GUI reached the real filesystem or launched a process". The source scan is
    /// deliberate: a type-level check cannot see a method body, and the compiler cannot drop a source
    /// dependency the way it drops an unused project reference.
    /// </summary>
    private static readonly string[] ForbiddenTokens =
    [
        "using System.IO",
        "using System.Diagnostics",
        "System.IO.",
        "System.Diagnostics.",
        "Environment.GetFolderPath",
        "File.",
        "Directory.",
        // `Path.` alone also matches the application helper `UserConfigPath.For`; pin the actual
        // System.IO members instead so the rule stays about filesystem access.
        "Path.Combine",
        "Path.GetFullPath",
        "Path.GetTempPath",
        "Path.DirectorySeparatorChar",
        "Path.AltDirectorySeparatorChar",
        "FileStream",
        "new Process",
    ];

    [Fact]
    public void The_gui_has_no_filesystem_or_process_access_outside_the_preferences_port()
    {
        string gui = Path.Combine(FindRepoRoot(), "src", "Coppice.Gui");
        Assert.True(Directory.Exists(gui), $"Could not find the GUI sources at {gui}.");

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string token in ForbiddenTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(gui, file)}: {token}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_preferences_port_is_in_ports_and_its_file_implementation_is_an_adapter()
    {
        Assert.Equal("Coppice.Ports", typeof(IShellPreferencesStore).Assembly.GetName().Name);
        Assert.Contains(
            typeof(JsonFileShellPreferencesStore).Assembly.GetTypes(),
            type => type.Name == "JsonFileShellPreferencesStore");
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "obj" or "bin");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coppice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

namespace Coppice.Core.Projects;

/// <summary>
/// A marker a project is identified by (FR-06). Plugins REGISTER markers rather than implementing
/// discovery themselves (04-plugin-contract), which is what keeps adding an ecosystem free of core
/// changes.
/// </summary>
public sealed record ProjectMarker
{
    public required string Id { get; init; }

    /// <summary>Exact file names that mark a project directory (<c>go.mod</c>, <c>Cargo.toml</c>).</summary>
    public IReadOnlyList<string> ExactNames { get; init; } = [];

    /// <summary>Extension-based markers, matched case-insensitively (<c>*.csproj</c>, <c>*.fsproj</c>).</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Ecosystem id the marker belongs to, for reporting and for resolver selection.</summary>
    public required string Ecosystem { get; init; }
}

/// <summary>
/// The registry of known markers. The core ships the four marker types named in the v0.1 scope
/// (09-ecosystems); plugins add more by registering, never by editing this list.
/// </summary>
public sealed class ProjectMarkerRegistry
{
    private readonly List<ProjectMarker> _markers = [];

    /// <summary>
    /// Directory names never descended into. Vendored and generated trees are skipped because a
    /// <c>node_modules/foo/package.json</c> is a dependency's manifest, not the user's project — and
    /// treating it as one would make the resolver report every transitive dependency as referenced.
    /// </summary>
    public static IReadOnlyList<string> DefaultSkippedDirectories { get; } =
        ["node_modules", "vendor", ".git", ".hg", ".svn", ".vs", ".idea", ".coppice", "bin", "obj", "target", "dist", "build"];

    public IReadOnlyList<ProjectMarker> Markers => _markers;

    public ProjectMarkerRegistry Register(ProjectMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        _markers.Add(marker);
        return this;
    }

    public static ProjectMarkerRegistry CreateDefault()
    {
        var registry = new ProjectMarkerRegistry();

        // 09-ecosystems, .NET section: *.csproj, *.fsproj, global.json, nuget.config.
        registry.Register(new ProjectMarker
        {
            Id = "dotnet-project",
            Ecosystem = "dotnet",
            Extensions = [".csproj", ".fsproj", ".vbproj"],
            ExactNames = ["global.json", "nuget.config"],
        });

        registry.Register(new ProjectMarker
        {
            Id = "go-module",
            Ecosystem = "go",
            ExactNames = ["go.mod", "go.work"],
        });

        registry.Register(new ProjectMarker
        {
            Id = "cargo-package",
            Ecosystem = "rust",
            ExactNames = ["Cargo.toml"],
        });

        registry.Register(new ProjectMarker
        {
            Id = "npm-package",
            Ecosystem = "node",
            ExactNames = ["package.json"],
        });

        return registry;
    }

    /// <summary>Every marker id satisfied by a single file name, most specific first.</summary>
    public IReadOnlyList<ProjectMarker> Match(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var matches = new List<ProjectMarker>();
        foreach (ProjectMarker marker in _markers)
        {
            bool exact = marker.ExactNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

            // Extension matching is case-insensitive because Windows and macOS filesystems are.
            bool extension = marker.Extensions.Any(ext =>
                fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && fileName.Length > ext.Length);

            if (exact || extension)
            {
                matches.Add(marker);
            }
        }

        return matches;
    }
}

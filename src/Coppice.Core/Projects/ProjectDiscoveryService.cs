using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Projects;

/// <summary>One discovered project and the markers that identified it (FR-06).</summary>
public sealed record Project(
    string RootPath,
    IReadOnlyList<string> MarkerIds,
    IReadOnlyList<string> Ecosystems)
{
    /// <summary>The marker file that made this directory a project root, for the report.</summary>
    public IReadOnlyList<string> MarkerFiles { get; init; } = [];
}

/// <summary>
/// The result of discovery, in the shape reference resolvers consume. An ecosystem with no projects
/// is recorded as empty rather than absent, so a resolver can say "I looked and found none"
/// instead of confusing "no projects" with "never scanned" (FR-07's three-state resolution).
/// </summary>
public sealed record ProjectSet(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<string> Ecosystems,
    IReadOnlyList<ScanIssue> Issues)
{
    public static ProjectSet Empty { get; } = new([], [], []);

    public bool IsEmpty => Projects.Count == 0;

    /// <summary>Projects that belong to an ecosystem, deterministically ordered.</summary>
    public IReadOnlyList<Project> For(string ecosystem) =>
        [.. Projects.Where(p => p.Ecosystems.Contains(ecosystem, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.RootPath, StringComparer.Ordinal)];
}

/// <summary>
/// The shared marker service (FR-06, T-008). Plugins register markers here rather than walking the
/// filesystem themselves — that is the whole point of "plugins propose, the kernel disposes".
/// <para>
/// Read-only, deterministic, and defensive about depth: a project tree can be enormous
/// (<c>node_modules</c> alone), so the skip-list is applied during descent, not after.
/// </para>
/// </summary>
public sealed class ProjectDiscoveryService
{
    private readonly IFileSystem _fs;
    private readonly ProjectMarkerRegistry _registry;
    private readonly IReadOnlySet<string> _skip;
    private readonly int _maxDepth;

    public ProjectDiscoveryService(
        IFileSystem fs,
        ProjectMarkerRegistry? registry = null,
        IEnumerable<string>? skipDirectories = null,
        int maxDepth = 12)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
        _registry = registry ?? ProjectMarkerRegistry.CreateDefault();
        _skip = new HashSet<string>(
            skipDirectories ?? ProjectMarkerRegistry.DefaultSkippedDirectories,
            StringComparer.OrdinalIgnoreCase);
        _maxDepth = maxDepth;
    }

    /// <summary>
    /// Scans the given roots for project markers. A root that does not exist is an issue, not a
    /// failure: users pass project roots from config and half of them are often wrong.
    /// </summary>
    public ProjectSet Discover(IReadOnlyList<string> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        var projects = new List<Project>();
        var issues = new List<ScanIssue>();
        var ecosystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string root in projectRoots.OrderBy(r => r, StringComparer.Ordinal))
        {
            if (!_fs.DirectoryExists(root))
            {
                issues.Add(new ScanIssue("projects", "PROJECT-ROOT-MISSING", $"Configured project root '{root}' does not exist.", root));
                continue;
            }

            foreach (Project project in Walk(root, 0))
            {
                if (!seen.Add(project.RootPath))
                {
                    continue;
                }

                projects.Add(project);
                foreach (string ecosystem in project.Ecosystems)
                {
                    ecosystems.Add(ecosystem);
                }
            }
        }

        // Deterministic order regardless of filesystem enumeration order (NFR-06).
        Project[] ordered = [.. projects
            .OrderBy(p => p.RootPath, StringComparer.Ordinal)
            .ThenBy(p => p.MarkerIds.Count == 0 ? string.Empty : p.MarkerIds[0], StringComparer.Ordinal)];

        return new ProjectSet(ordered, [.. ecosystems.OrderBy(e => e, StringComparer.Ordinal)], Deterministic.OrderIssues(issues));
    }

    private IEnumerable<Project> Walk(string directory, int depth)
    {
        if (depth > _maxDepth)
        {
            yield break;
        }

        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = _fs.EnumerateEntries(directory, new EnumerationRequest
            {
                FollowLinks = false,
                IncludeLinks = false,
                MaxDepth = 1,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var markerIds = new List<string>();
        var markerFiles = new List<string>();
        var projectEcosystems = new List<string>();
        var childDirectories = new List<string>();

        foreach (FileEntry entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Kind == EntryKind.Directory)
            {
                // The skip-list is applied HERE, during descent. Filtering afterwards would still
                // have walked the entire node_modules tree to find out what to ignore.
                if (!_skip.Contains(entry.Name))
                {
                    childDirectories.Add(entry.Path);
                }

                continue;
            }

            foreach (ProjectMarker marker in _registry.Match(entry.Name))
            {
                if (!markerIds.Contains(marker.Id, StringComparer.Ordinal))
                {
                    markerIds.Add(marker.Id);
                    markerFiles.Add(entry.Path);
                }

                if (!projectEcosystems.Contains(marker.Ecosystem, StringComparer.OrdinalIgnoreCase))
                {
                    projectEcosystems.Add(marker.Ecosystem);
                }
            }
        }

        if (markerIds.Count > 0)
        {
            yield return new Project(
                directory,
                markerIds,
                projectEcosystems)
            {
                MarkerFiles = markerFiles,
            };
        }

        // Recurse depth-first, deterministic by name. A directory that is itself a project is
        // still descended into: a Go monorepo has go.mod at the root AND in every service.
        foreach (string child in childDirectories)
        {
            foreach (Project nested in Walk(child, depth + 1))
            {
                yield return nested;
            }
        }
    }
}

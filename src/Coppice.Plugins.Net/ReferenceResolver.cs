using Coppice.Core.Domain;

namespace Coppice.Plugins.Net;

/// <summary>
/// Assigns Referenced / Unreferenced / Unknown to each cached package (FR-07).
/// <para>
/// The invariant that matters: <see cref="Usage.Unknown"/> never becomes
/// <see cref="Risk.Safe"/>. A package we cannot prove is unused must not be proposed for removal,
/// because "we could not tell" and "nobody needs it" are different facts (04-plugin-contract rule 4).
/// </para>
/// <para>
/// The Unreferenced verdict requires POSITIVE evidence: at least one project was actually read and
/// none of them referenced the package. Zero readable projects is Unknown, never Unreferenced —
/// otherwise a user with no configured project roots would be shown their entire cache as deletable.
/// </para>
/// </summary>
public sealed class ReferenceResolver
{
    private readonly Dictionary<string, List<string>> _referencedBy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreadableProjects = new(StringComparer.OrdinalIgnoreCase);
    private int _readableProjects;

    /// <summary>
    /// Records what a project references. A project whose references could not be read is counted as
    /// unreadable, which downgrades every Unreferenced verdict to Unknown.
    /// </summary>
    public void AddProject(string projectRoot, IReadOnlyList<PackageReference>? references)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);

        if (references is null)
        {
            _unreadableProjects.Add(projectRoot);
            return;
        }

        _readableProjects++;

        foreach (PackageReference reference in references)
        {
            string key = Key(reference.PackageId, reference.Version);
            if (!_referencedBy.TryGetValue(key, out List<string>? projects))
            {
                projects = [];
                _referencedBy[key] = projects;
            }

            if (!projects.Contains(projectRoot, StringComparer.OrdinalIgnoreCase))
            {
                projects.Add(projectRoot);
            }
        }
    }

    /// <summary>How many projects contributed readable evidence.</summary>
    public int ReadableProjectCount => _readableProjects;

    public int UnreadableProjectCount => _unreadableProjects.Count;

    /// <summary>Decides one package's usage, with a reason string fit for the plan.</summary>
    public ReferenceVerdict Resolve(string packageId, string version)
    {
        if (_referencedBy.TryGetValue(Key(packageId, version), out List<string>? projects))
        {
            return ReferenceVerdict.Referenced([.. projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)]);
        }

        if (_readableProjects == 0)
        {
            return ReferenceVerdict.Unknown(
                _unreadableProjects.Count == 0
                    ? "no projects were scanned, so no project can be said not to reference this"
                    : $"none of the {Count(_unreadableProjects.Count, "project")} found could be read "
                      + "(no obj/project.assets.json — the project has not been restored)");
        }

        if (_unreadableProjects.Count > 0)
        {
            return ReferenceVerdict.Unknown(
                $"{Count(_readableProjects, "project")} scanned, but {Count(_unreadableProjects.Count, "project")} "
                + "could not be read (not restored), so this cannot be proven unused");
        }

        return ReferenceVerdict.Unreferenced(_readableProjects, packageId);
    }

    /// <summary>
    /// The risk tier an item may carry given its usage. This is where the Unknown → Safe prohibition
    /// is enforced structurally rather than left to each caller to remember (C-7).
    /// </summary>
    public static Risk ApplyUsageFloor(ReferenceVerdict verdict, Risk declaredRisk)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (verdict.Usage == Usage.Unknown && declaredRisk == Risk.Safe)
        {
            return Risk.Review;
        }

        return declaredRisk;
    }

    private static string Key(string packageId, string version) => packageId + "/" + version;

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";
}

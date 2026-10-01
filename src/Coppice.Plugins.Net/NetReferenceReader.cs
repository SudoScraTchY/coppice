using System.Text.Json;
using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Plugins.Net;

/// <summary>A package version one project depends on.</summary>
public sealed record PackageReference(string PackageId, string Version);

/// <summary>
/// The three-state answer for one cached package (FR-07). There is deliberately no
/// "probably unreferenced": a package we cannot prove is unused must never be treated as unused.
/// </summary>
public sealed record ReferenceVerdict
{
    public required Usage Usage { get; init; }

    /// <summary>Projects that reference this exact id + version.</summary>
    public IReadOnlyList<string> ReferencingProjects { get; init; } = [];

    /// <summary>
    /// A sentence the user can read, because a bare "unused" is not an explanation (FR-07:
    /// "Referenced (9 projects)", "no lock files found").
    /// </summary>
    public required string Reason { get; init; }

    public static ReferenceVerdict Referenced(IReadOnlyList<string> projects) => new()
    {
        Usage = Usage.Referenced,
        ReferencingProjects = projects,
        Reason = projects.Count == 1
            ? $"referenced by 1 project ({projects[0]})"
            : $"referenced by {projects.Count} projects",
    };

    public static ReferenceVerdict Unreferenced(int scannedProjects, string packageId) => new()
    {
        Usage = Usage.Unreferenced,
        Reason = $"no project references {packageId}; {scannedProjects} project(s) were scanned",
    };

    public static ReferenceVerdict Unknown(string reason) => new() { Usage = Usage.Unknown, Reason = reason };
}

/// <summary>
/// Reads <c>obj/project.assets.json</c> and <c>global.json</c> (FR-07, S-4).
/// <para>
/// The assets file is NuGet's own record of what it resolved, so it is exact — no guessing about
/// which versions a project needs. A project with no assets file is NOT evidence of non-use: it is
/// evidence that we cannot tell, which is the <see cref="Usage.Unknown"/> case the spec is careful
/// about.
/// </para>
/// </summary>
public sealed class NetReferenceReader
{
    private const int MaxSmallFileBytes = 8 * 1024 * 1024;

    private readonly IFileSystem _fs;

    public NetReferenceReader(IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    /// <summary>Path of the assets file NuGet writes for a project directory.</summary>
    public static string AssetsPathFor(string projectDirectory, char separator) =>
        Combine(separator, projectDirectory, "obj", "project.assets.json");

    /// <summary>
    /// Package references declared by one project, or null when it has no assets file (unbuilt, or
    /// never restored). null is meaningfully different from an empty list.
    /// </summary>
    public IReadOnlyList<PackageReference>? ReadReferences(string projectDirectory, char separator)
    {
        string path = AssetsPathFor(projectDirectory, separator);
        if (!_fs.FileExists(path))
        {
            return null;
        }

        string json;
        try
        {
            json = _fs.ReadSmallText(path, MaxSmallFileBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ParseAssets(json);
    }

    /// <summary>
    /// Parses an assets file. Returns an empty list for a file that parses but has no package
    /// targets, and null for one that does not parse at all — a corrupt file is "unknown", never
    /// "unreferenced".
    /// </summary>
    public static IReadOnlyList<PackageReference>? ParseAssets(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("targets", out JsonElement targets))
            {
                return null;
            }

            var results = new List<PackageReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty target in targets.EnumerateObject())
            {
                foreach (JsonProperty entry in target.Value.EnumerateObject())
                {
                    string key = entry.Name;

                    // Keys are "Id/Version". Project references carry a "type" of "project" and must
                    // NOT count as package references — treating them as such would mark every
                    // in-repo project as protecting a package it does not use.
                    if (entry.Value.ValueKind == JsonValueKind.Object
                        && entry.Value.TryGetProperty("type", out JsonElement type)
                        && type.ValueKind == JsonValueKind.String
                        && string.Equals(type.GetString(), "project", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int slash = key.LastIndexOf('/');
                    if (slash <= 0)
                    {
                        continue;
                    }

                    string id = key[..slash];
                    string version = key[(slash + 1)..];
                    if (seen.Add(id + "/" + version))
                    {
                        results.Add(new PackageReference(id, version));
                    }
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An SDK pin from <c>global.json</c>, which is a refusal rule rather than a preference (S-4):
    /// a project pinned to an SDK must never have a DIFFERENT SDK proposed for removal.
    /// </summary>
    public static SdkPin? ReadGlobalJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sdk", out JsonElement sdk)
                || !sdk.TryGetProperty("version", out JsonElement version))
            {
                return null;
            }

            string pinned = version.GetString() ?? string.Empty;
            if (pinned.Length == 0)
            {
                return null;
            }

            string rollForward = sdk.TryGetProperty("rollForward", out JsonElement rf)
                && rf.ValueKind == JsonValueKind.String
                ? rf.GetString() ?? "latestPatch"
                : "latestPatch";

            return new SdkPin(pinned, rollForward);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Joins with the TARGET OS's separator, taken from the caller rather than the host.</summary>
    private static string Combine(char separator, string root, params string[] parts) =>
        root.TrimEnd('/', '\\') + separator + string.Join(separator, parts);
}

/// <summary>An SDK pin declared by a project's <c>global.json</c>.</summary>
public sealed record SdkPin(string Version, string RollForward);

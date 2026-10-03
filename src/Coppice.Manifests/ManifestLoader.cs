using System.Reflection;

using Coppice.Manifests.Toml;
using Coppice.Ports;

namespace Coppice.Manifests;

/// <summary>
/// Loads ecosystem manifests from disk and embedded resources (10-formats, FR-23).
/// <para>
/// The acceptance requirement is that a manifest dropped into the user's ecosystem directory is picked
/// up WITHOUT recompiling. That is what this type exists for, and the reason it reads through
/// <see cref="IFileSystem"/> rather than <c>File</c>: a manifest loader that cannot be tested is a
/// manifest loader nobody will trust with a path.
/// </para>
/// <para>
/// The only direct <c>System.IO</c> use is reading the assembly's own embedded resources, which are not
/// a filesystem at all. User-supplied files go through the port like everything else (NFR-08).
/// </para>
/// </summary>
public sealed class ManifestLoader
{
    private const string ResourceFolderMarker = ".manifests.";

    private readonly IFileSystem _fileSystem;

    public ManifestLoader(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Validates one manifest from text. Split out so a caller can validate content it already has (an
    /// embedded resource, a string in a test) without inventing a file for it.
    /// </summary>
    public ManifestValidationResult ValidateText(string text, ManifestOrigin origin, string sourceName) =>
        ManifestValidationResult.FromToml(text, origin, sourceName);

    /// <summary>
    /// Loads and validates every <c>*.toml</c> directly under <paramref name="directory"/>.
    /// <para>
    /// Returns a result per file rather than throwing: one broken manifest must not stop the others from
    /// loading, or a single typo in an optional profile takes out the whole report.
    /// </para>
    /// </summary>
    public IReadOnlyList<ManifestLoadResult> LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var results = new List<ManifestLoadResult>();

        if (!_fileSystem.DirectoryExists(directory))
        {
            return results;
        }

        foreach (FileEntry entry in EnumerateTomlFiles(directory))
        {
            results.Add(new ManifestLoadResult(
                entry.Path,
                ManifestOrigin.User,
                LoadFile(entry.Path, entry.Name)));
        }

        return results;
    }

    private ManifestValidationResult LoadFile(string path, string name)
    {
        string? content = _fileSystem.ReadSmallText(path);

        if (content is null)
        {
            return new ManifestValidationResult(
                [new ManifestError("file", name, "could not be read", "check the file exists and is readable")],
                [],
                1,
                null,
                string.Empty,
                [],
                ManifestOrigin.User,
                name);
        }

        return ManifestValidationResult.FromToml(content, ManifestOrigin.User, name);
    }

    /// <summary>
    /// Loads the built-in manifests compiled into this assembly. These are the profiles coppice ships; a
    /// user manifest with the same id shadows one (OQ-08, opt-in enable).
    /// </summary>
    public IReadOnlyList<ManifestLoadResult> LoadBuiltIn()
    {
        var results = new List<ManifestLoadResult>();
        Assembly assembly = typeof(ManifestLoader).Assembly;

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            int index = resourceName.IndexOf(ResourceFolderMarker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            string fileName = resourceName[(index + ResourceFolderMarker.Length)..];

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            results.Add(new ManifestLoadResult(
                fileName,
                ManifestOrigin.BuiltIn,
                ManifestValidationResult.FromToml(reader.ReadToEnd(), ManifestOrigin.BuiltIn, fileName)));
        }

        // Sorted by name so the load order does not depend on resource-table order (NFR-06).
        return [.. results.OrderBy(r => r.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Merges built-in and user manifests. A user manifest with the same id SHADOWS the built-in and
    /// produces a warning, because silently replacing a shipped profile is the kind of surprise that
    /// makes a tool untrustworthy (OQ-08).
    /// </summary>
    public IReadOnlyList<ManifestLoadResult> LoadAll(string? userDirectory)
    {
        List<ManifestLoadResult> merged = [.. LoadBuiltIn()];

        if (userDirectory is null)
        {
            return merged;
        }

        foreach (ManifestLoadResult candidate in LoadDirectory(userDirectory))
        {
            if (candidate.Id is not { } userId)
            {
                // A manifest too broken to name itself cannot shadow anything.
                merged.Add(candidate);
                continue;
            }

            int existing = merged.FindIndex(m => m.Id == userId);
            if (existing >= 0)
            {
                merged.RemoveAt(existing);
            }

            merged.Add(candidate with
            {
                Validation = candidate.Validation with
                {
                    Warnings =
                    [
                        .. candidate.Validation.Warnings,
                        new ManifestWarning("id", candidate.Validation.SourceName, $"shadows the built-in profile '{userId}'"),
                    ],
                },
            });
        }

        return merged;
    }

    private IReadOnlyList<FileEntry> EnumerateTomlFiles(string directory)
    {
        var paths = new List<FileEntry>();

        foreach (FileEntry entry in _fileSystem.EnumerateEntries(directory, new EnumerationRequest { MaxDepth = 1 }))
        {
            // Case-insensitive on every OS: a user on Windows or macOS will type `Go.TOML`, and a
            // profile that loads on Linux but not on the machine its author is typing on is a bug.
            // Dot-prefixed files are skipped so `.hidden.toml` and editor swap files stay out.
            if (entry.Kind != EntryKind.File ||
                !entry.Name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.StartsWith('.'))
            {
                continue;
            }

            paths.Add(entry);
        }

        // Sorted by name so the load order — and therefore the report order — does not depend on the
        // filesystem's enumeration order (NFR-06).
        return [.. paths.OrderBy(e => e.Name, StringComparer.Ordinal)];
    }
}

/// <summary>One manifest as loaded: where it came from, and whether it passed.</summary>
public sealed record ManifestLoadResult(string Path, ManifestOrigin Origin, ManifestValidationResult Validation)
{
    public bool IsValid => Validation.IsValid;

    public string? Id => Validation.Id;
}

using System.Threading;

using Coppice.Parsing;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;

namespace Coppice.Plugins.Rust;

/// <summary>
/// The Rust ecosystem plugin (T-023, FR-23, 09-ecosystems "rust").
/// <para>
/// Cargo is the interesting case in Phase 2: it has <em>no query command</em>. Every location except
/// <c>rustup-toolchains</c> is located by reading <c>CARGO_HOME</c> or falling back to a convention, so
/// the manifest marks those locations with a confidence penalty and the report says so. Nothing here
/// decides that — the penalty is declared in <c>rust.toml</c> and applied by the resolution engine, which
/// is the right place for it: a plugin that adjusted its own confidence could understate it.
/// </para>
/// <para>
/// Like every plugin, it reaches the outside world only through <see cref="ScanContext"/>.
/// </para>
/// </summary>
public sealed class RustEcosystem : IEcosystem, IInventoryProvider, IReferenceResolver
{
    /// <summary>Locations holding extracted crate sources at <c>{name}-{version}</c>.</summary>
    private const string RegistrySrcLocation = "cargo-registry-src";

    /// <summary>Locations holding toolchain installs.</summary>
    private const string ToolchainsLocation = "rustup-toolchains";

    private readonly IFileSystem? _fs;

    public RustEcosystem()
    {
    }

    public RustEcosystem(IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    public string Id => "rust";

    public IVersionOrdering Versions { get; } = new CrateVersionOrdering();

    /// <summary>
    /// Rust is present when the scan resolved a location it owns. Note that presence can be TRUE with a
    /// lowered confidence: cargo being "installed" is inferred from a directory, not confirmed by a tool.
    /// </summary>
    public bool IsPresent(ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Roots.Any(r => IsOurs(r.LocationId));
    }

    /// <summary>
    /// Enumerates crates and toolchains.
    /// <para>
    /// The registry's extracted sources sit at <c>src/{registry}/{name}-{version}</c>, so like Go the
    /// item is deeper than one level and the registry segment is a path component that varies by which
    /// registry is in use. The walk therefore descends and stops at the first <c>name-version</c> match.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<PortableItem> Discover(ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        foreach (ScanRoot root in ctx.Roots.Where(r => IsOurs(r.LocationId)))
        {
            if (!ctx.FileSystem.DirectoryExists(root.ResolvedPath))
            {
                continue;
            }

            foreach (PortableItem item in root.LocationId switch
            {
                RegistrySrcLocation => Crates(ctx, root, Id),
                ToolchainsLocation => Toolchains(ctx, root, Id),
                _ => [],
            })
            {
                yield return item;
            }
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<PortableItem> Crates(ScanContext ctx, ScanRoot root, string ecosystemId)
    {
        foreach ((FileEntry directory, string _) in WalkForCrates(ctx, root.ResolvedPath, depth: 0))
        {
            if (!TrySplitCrateVersion(directory.Name, out string name, out string version))
            {
                continue;
            }

            yield return Build(
                ecosystemId,
                root.LocationId,
                "crate",
                directory.Name,
                version,
                directory,
                // `name` is the bare crate name, which is what Cargo.lock matches on; the directory name
                // is what the report shows, because that is what the user sees on disk and it keeps two
                // versions of one crate distinguishable in a listing.
                $"extracted crate source (crate {name})");
        }
    }

    private static IEnumerable<PortableItem> Toolchains(ScanContext ctx, ScanRoot root, string ecosystemId)
    {
        foreach (FileEntry entry in Directories(ctx, root.ResolvedPath))
        {
            // rustup writes bookkeeping beside the toolchains: `update-hashes`, `settings.toml` and the
            // download temp dirs. None of them is an installed toolchain, and offering to remove one
            // would break rustup's own bookkeeping.
            if (entry.Name.StartsWith('.') || entry.Name is "update-hashes" or "settings.toml")
            {
                continue;
            }

            // A toolchain directory with no `bin` is a partial download, not an install.
            if (!ctx.FileSystem.DirectoryExists(Path.Combine(entry.Path, "bin")))
            {
                continue;
            }

            yield return Build(ecosystemId, root.LocationId, "toolchain", entry.Name, string.Empty, entry, "rustup toolchain");
        }
    }

    private static PortableItem Build(
        string ecosystemId,
        string locationId,
        string kind,
        string name,
        string version,
        FileEntry directory,
        string description) =>
        new(
            ItemId(locationId, kind, name, version),
            ecosystemId,
            kind,
            name,
            version,
            directory.Path,
            // Toolchains are installed binaries, not downloads. A crate is a regenerable download.
            kind == "toolchain" ? Risk.Review : Risk.Safe,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Required by C-2: without the location id an item's root cannot be proven.
                ["locationId"] = locationId,
                ["description"] = description,
            });

    private const int MaxRegistryDepth = 4;

    private static IEnumerable<(FileEntry Directory, string PathPrefix)> WalkForCrates(
        ScanContext ctx,
        string path,
        int depth)
    {
        foreach (FileEntry entry in Directories(ctx, path))
        {
            if (TrySplitCrateVersion(entry.Name, out _, out _))
            {
                yield return (entry, string.Empty);
                continue;
            }

            if (depth >= MaxRegistryDepth)
            {
                continue;
            }

            foreach ((FileEntry directory, string _) in WalkForCrates(ctx, entry.Path, depth + 1))
            {
                yield return (directory, string.Empty);
            }
        }
    }

    /// <summary>
    /// Splits a crate directory name <c>{name}-{version}</c>.
    /// <para>
    /// The separator is ambiguous — crates routinely contain hyphens (<c>serde-json</c>) — so the split
    /// searches RIGHT to LEFT for the last hyphen that is followed by a digit. Splitting on the first
    /// hyphen would turn <c>serde-json-1.0.0</c> into the crate "serde" at version "json-1.0.0", which
    /// does not exist and would never match a Cargo.lock.
    /// </para>
    /// </summary>
    public static bool TrySplitCrateVersion(string directoryName, out string name, out string version)
    {
        name = string.Empty;
        version = string.Empty;

        for (int i = directoryName.Length - 1; i > 0; i--)
        {
            if (directoryName[i] != '-')
            {
                continue;
            }

            string tail = directoryName[(i + 1)..];
            if (!LooksLikeCrateVersion(tail))
            {
                continue;
            }

            name = directoryName[..i];
            version = tail;
            return name.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// True for a crate version: dot-separated numeric components with an optional
    /// <c>-prerelease</c> and <c>+build</c> tail.
    /// <para>
    /// The dot requirement is what excludes the registry index directory. Cargo names it
    /// <c>index.crates.io-6f17d22bba15001f</c>, whose tail is a hex hash — digits and letters, no dot.
    /// Without this, the registry directory parses as a crate named <c>index.crates.io</c> at version
    /// <c>6f17d22bba15001f</c>, and the report would offer to delete the registry's own index as a crate.
    /// </para>
    /// <para>
    /// This also rejects <c>some-thing-42</c>, which is not a crate directory. Cargo always writes
    /// semver, so requiring semver costs nothing real and rules out a whole class of false item.
    /// </para>
    /// </summary>
    public static bool LooksLikeCrateVersion(string candidate)
    {
        if (candidate.Length < 3 || !char.IsAsciiDigit(candidate[0]))
        {
            return false;
        }

        string withoutBuild = candidate.Split('+')[0];
        string numeric = withoutBuild.Split('-')[0];

        string[] parts = numeric.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Three-state crate usage from <c>Cargo.lock</c> (FR-07).
    /// <para>
    /// A <c>Cargo.lock</c> is an exact resolution: cargo writes the precise version it will build, so a
    /// version not named there is not used by that project. That is a stronger statement than Go's
    /// <c>go.sum</c>, and it is treated as one — same three states, same refusal to guess.
    /// </para>
    /// </summary>
    public ReferenceVerdict Resolve(string itemName, string itemVersion, ProjectSet projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (projects.Projects.Count == 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no projects were discovered, so no crate can be proven unused",
            };
        }

        if (_fs is null)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no filesystem port was supplied, so Cargo.lock files cannot be read",
            };
        }

        var referencing = new List<string>();
        int readable = 0;
        int unreadable = 0;

        foreach (Project project in projects.Projects)
        {
            IReadOnlyList<(string Name, string Version)>? entries = ReadCargoLock(project.RootPath);

            if (entries is null)
            {
                unreadable++;
                continue;
            }

            readable++;

            if (entries.Any(e => IsSameCrate(e, itemName, itemVersion)))
            {
                referencing.Add(project.RootPath);
            }
        }

        if (referencing.Count > 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Referenced,
                ReferencingProjects = [.. referencing.Order(StringComparer.Ordinal)],
                Reason = $"locked by {referencing.Count} project(s) per Cargo.lock",
            };
        }

        if (unreadable > 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = $"{unreadable} project(s) could not be read (no Cargo.lock — run 'cargo build')",
            };
        }

        if (readable == 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no project could be read, so no crate can be proven unused",
            };
        }

        return new ReferenceVerdict
        {
            Usage = Ports.Usage.Unreferenced,
            Reason = $"not in any Cargo.lock ({readable} project(s) checked)",
        };
    }

    /// <summary>
    /// Cargo.lock names <c>name version</c> for every resolved crate. A <c>source</c> line names the
    /// registry, which is not a package and must not be read as one.
    /// </summary>
    private static bool IsSameCrate((string Name, string Version) entry, string itemName, string itemVersion) =>
        string.Equals(entry.Name, itemName, StringComparison.Ordinal)
        && string.Equals(entry.Version, itemVersion, StringComparison.Ordinal);

    private IReadOnlyList<(string Name, string Version)>? ReadCargoLock(string projectRoot)
    {
        char separator = projectRoot.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        string path = projectRoot + separator + "Cargo.lock";

        string content;

        try
        {
            content = _fs!.ReadSmallText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing lock file is the normal state for a project that has never been built.
            return null;
        }

        var entries = new List<(string Name, string Version)>();
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();

            // `[[package]]` begins a block; the name and version follow on the next lines. Parsing the
            // TOML structurally would be overkill — but a stray `name = "x"` elsewhere in the file must
            // not be picked up, so the block context is tracked rather than pattern-matched globally.
            if (line != "[[package]]")
            {
                continue;
            }

            string? name = null;
            string? version = null;

            for (int j = i + 1; j < lines.Length && lines[j].Trim() != "[[package]]"; j++)
            {
                string field = lines[j].Trim();

                if (field.StartsWith("name = ", StringComparison.Ordinal))
                {
                    name = Unquote(field["name = ".Length..]);
                }
                else if (field.StartsWith("version = ", StringComparison.Ordinal))
                {
                    version = Unquote(field["version = ".Length..]);
                }
            }

            if (name is not null && version is not null)
            {
                entries.Add((name, version));
            }
        }

        return entries;
    }

    private static string Unquote(string value)
    {
        string trimmed = value.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>v0.1 is read-only; the gateway plans removals in v0.3.</summary>
    public RemovalAction? Plan(PortableItem item) => null;

    private static IEnumerable<FileEntry> Directories(ScanContext ctx, string path)
    {
        foreach (FileEntry entry in ctx.FileSystem.EnumerateEntries(path, new EnumerationRequest { MaxDepth = 1 }))
        {
            if (entry.Kind == EntryKind.Directory)
            {
                yield return entry;
            }
        }
    }

    private static string ItemId(string locationId, string kind, string name, string version)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{locationId}:{kind}:{name}:{version}"));

        return System.Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static bool IsOurs(string locationId) =>
        locationId.StartsWith("cargo-", StringComparison.Ordinal)
        || locationId.StartsWith("rustup-", StringComparison.Ordinal);
}

/// <summary>Crate version ordering: semver, which is what Cargo.lock records.</summary>
public sealed class CrateVersionOrdering : IVersionOrdering
{
    public VersionComponents? TryParse(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string trimmed = version.StartsWith('v') ? version[1..] : version;
        string withoutBuild = trimmed.Split('+')[0];
        int dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);

        string pre = dash < 0 ? string.Empty : withoutBuild[(dash + 1)..];
        string numeric = dash < 0 ? withoutBuild : withoutBuild[..dash];

        string[] parts = numeric.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out int major))
        {
            return null;
        }

        int minor = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;

        return new VersionComponents(major, minor, patch, 0, pre.Length == 0 ? null : pre, null);
    }

    public int Compare(string? x, string? y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        VersionComponents? left = TryParse(x);
        VersionComponents? right = TryParse(y);

        // An unparseable version sorts below every parseable one, deterministically. Guessing a component
        // for "not-a-version" would let a malformed lock entry compare equal to a real version.
        if (left is null || right is null)
        {
            if (left is null && right is null)
            {
                return string.CompareOrdinal(x, y);
            }

            return left is null ? -1 : 1;
        }

        int result = left.Value.Major.CompareTo(right.Value.Major);
        if (result != 0)
        {
            return result;
        }

        result = left.Value.Minor.CompareTo(right.Value.Minor);
        if (result != 0)
        {
            return result;
        }

        result = left.Value.Patch.CompareTo(right.Value.Patch);
        if (result != 0)
        {
            return result;
        }

        // A release beats a prerelease of the same number.
        bool leftPre = left.Value.Prerelease is not null;
        bool rightPre = right.Value.Prerelease is not null;

        if (leftPre != rightPre)
        {
            return leftPre ? -1 : 1;
        }

        return string.CompareOrdinal(left.Value.Prerelease, right.Value.Prerelease);
    }
}

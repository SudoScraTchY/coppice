using System.Threading;

using Coppice.Ports;

namespace Coppice.Plugins.Node;

/// <summary>
/// The Node.js ecosystem plugin (T-024, FR-23, 09-ecosystems "node").
/// <para>
/// The whole plugin exists to express one asymmetry that the report must not blur: <c>npm-cache</c> is
/// content-addressed and therefore a WHOLE-LOCATION tier, while <c>npm-global</c> has ordinary
/// per-package structure. Both look like "node caches"; they can only be cleaned at completely different
/// granularities, and a report that treated them alike would either propose deleting individual blobs
/// that do not exist as items, or refuse to clean a cache that is entirely disposable.
/// </para>
/// <para>
/// Like every plugin, it reaches the outside world only through <see cref="ScanContext"/>.
/// </para>
/// </summary>
public sealed class NodeEcosystem : IEcosystem, IInventoryProvider, IReferenceResolver
{
    /// <summary>The content-addressed download cache. One item, never many.</summary>
    private const string CacheLocation = "npm-cache";

    /// <summary>Globally installed packages. One item per package.</summary>
    private const string GlobalLocation = "npm-global";

    /// <summary>npm's content store, inside the cache. Its presence is what makes a directory an npm cache.</summary>
    private const string ContentAddressedStore = "_cacache";

    private readonly IFileSystem? _fs;

    public NodeEcosystem()
    {
    }

    public NodeEcosystem(IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    public string Id => "node";

    public IVersionOrdering Versions { get; } = new SemVerVersionOrdering();

    /// <summary>
    /// Node is present when the scan resolved a location it owns. A resolved npm-cache proves npm has run
    /// here, since npm creates the cache directory itself.
    /// </summary>
    public bool IsPresent(ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Roots.Any(r => IsOurs(r.LocationId));
    }

    /// <summary>
    /// Enumerates the cache as ONE item and the global root as one item per package.
    /// <para>
    /// The asymmetry is the whole point. See the type summary for why.
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

            // The cache arm is a single item; the globals arm is many. Making both arms IEnumerable keeps
            // the switch well-typed instead of relying on a best common type that does not exist.
            IEnumerable<PortableItem> discovered = root.LocationId switch
            {
                CacheLocation => WholeCache(ctx, root, Id),
                GlobalLocation => GlobalPackages(ctx, root, Id),
                _ => [],
            };

            foreach (PortableItem item in discovered)
            {
                yield return item;
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The cache as a single item, or NOTHING when <c>_cacache</c> is absent.
    /// <para>
    /// Returning nothing rather than a placeholder matters: a directory that merely happens to be called
    /// <c>~/.npm</c> is not an npm cache, and reporting one would let a policy propose removing a user's
    /// npm configuration — <c>~/.npmrc</c> and the auth token live in that same directory tree.
    /// </para>
    /// <para>
    /// One item, not one per blob. The blobs are named by content hash and shared between packages, so
    /// there is no per-package identity to report and no path that would mean "remove this package".
    /// </para>
    /// </summary>
    private static PortableItem[] WholeCache(ScanContext ctx, ScanRoot root, string ecosystemId)
    {
        string store = System.IO.Path.Combine(root.ResolvedPath, ContentAddressedStore);

        if (!ctx.FileSystem.DirectoryExists(store))
        {
            return [];
        }

        return
        [
            new PortableItem(
                ItemId(root.LocationId, "cache", "npm-cache", string.Empty),
                ecosystemId,
                "cache",
                "npm-cache",
                string.Empty,
                store,

                // Safe: every byte is re-downloadable. But the RISK is about the unit, not the bytes — this
                // is the whole store or nothing, and the plan can only ever contain one step for it.
                Risk.Safe,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["locationId"] = root.LocationId,
                    ["granularity"] = "whole-location",
                    ["description"] = $"npm's content-addressed store ({ContentAddressedStore}); "
                        + "blobs are named by content hash and shared between packages, so per-package "
                        + "cleanup is not possible",
                    ["itemCount"] = "1",
                }),
        ];
    }

    private static IEnumerable<PortableItem> GlobalPackages(ScanContext ctx, ScanRoot root, string ecosystemId)
    {
        foreach (FileEntry entry in Directories(ctx, root.ResolvedPath))
        {
            // npm writes dot-prefixed bookkeeping beside packages, and a package directory without a
            // package.json is a partial install rather than a package.
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            string manifest = System.IO.Path.Combine(entry.Path, "package.json");
            if (!ctx.FileSystem.FileExists(manifest))
            {
                continue;
            }

            string? version = ReadVersion(ctx, manifest);

            yield return new PortableItem(
                ItemId(root.LocationId, "package", entry.Name, version ?? string.Empty),
                ecosystemId,
                "package",
                entry.Name,
                version ?? string.Empty,
                entry.Path,
                Risk.Review,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["locationId"] = root.LocationId,
                    ["granularity"] = "per-package",
                    ["description"] = "globally installed npm package",
                });
        }
    }

    /// <summary>
    /// Reads the version from a package.json. Returns null when it cannot be read, and the item is then
    /// reported with an empty version rather than a guess — an npm package's version matters for
    /// ordering, and inventing one would put the wrong entry in a "newest N" plan.
    /// </summary>
    private static string? ReadVersion(ScanContext ctx, string manifestPath)
    {
        string? content = ctx.FileSystem.ReadSmallText(manifestPath);

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(content);

            return document.RootElement.TryGetProperty("version", out System.Text.Json.JsonElement version)
                && version.ValueKind == System.Text.Json.JsonValueKind.String
                    ? version.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // A package.json that does not parse is a broken install. Reporting no version is honest;
            // reporting a fake one would be worse than either.
            return null;
        }
    }

    /// <summary>
    /// Three-state package usage from <c>package-lock.json</c> (FR-07).
    /// <para>
    /// npm's lockfile names a resolved version per dependency, so the same exact-version rule that applies
    /// to Rust's <c>Cargo.lock</c> applies here. The globals-vs-locals distinction the card mentions is
    /// NOT derivable from a lockfile: a lockfile describes a project's own dependencies, and says nothing
    /// about what is installed globally. So a global package that no lock mentions resolves Unknown,
    /// never Unreferenced — the honest answer, and the card explicitly calls it correct rather than
    /// guessed.
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
                Reason = "no projects were discovered, so no package can be proven unused",
            };
        }

        if (_fs is null)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no filesystem port was supplied, so package-lock.json files cannot be read",
            };
        }

        var referencing = new List<string>();
        int readable = 0;
        int unreadable = 0;

        foreach (Project project in projects.Projects)
        {
            IReadOnlyList<(string Name, string Version)>? entries = ReadLock(project.RootPath);

            if (entries is null)
            {
                unreadable++;
                continue;
            }

            readable++;

            if (entries.Any(e => IsSamePackage(e, itemName, itemVersion)))
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
                Reason = $"locked by {referencing.Count} project(s) per package-lock.json",
            };
        }

        if (unreadable > 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = $"{unreadable} project(s) could not be read (no package-lock.json — run 'npm install')",
            };
        }

        if (readable == 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no project could be read, so no package can be proven unused",
            };
        }

        return new ReferenceVerdict
        {
            Usage = Ports.Usage.Unreferenced,
            Reason = $"not in any package-lock.json ({readable} project(s) checked)",
        };
    }

    private static bool IsSamePackage((string Name, string Version) entry, string itemName, string itemVersion)
    {
        if (!string.Equals(entry.Name, itemName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // npm package names are lower-cased on publish, but a lockfile or a global install can still carry
        // a capital in hand-written data. Versions are compared exactly.
        return string.Equals(entry.Version, itemVersion, StringComparison.Ordinal);
    }

    private IReadOnlyList<(string Name, string Version)>? ReadLock(string projectRoot)
    {
        char separator = projectRoot.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        string path = projectRoot + separator + "package-lock.json";

        string content;

        try
        {
            content = _fs!.ReadSmallText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var entries = new List<(string Name, string Version)>();

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(content);

            if (!document.RootElement.TryGetProperty("packages", out System.Text.Json.JsonElement packages)
                || packages.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return entries;
            }

            foreach (System.Text.Json.JsonProperty entry in packages.EnumerateObject())
            {
                if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                // The NAME comes from the KEY, which is a path. A real package-lock.json has no `name`
                // field per entry — requiring one made every package read as Unreferenced, i.e. a populated
                // cache declared entirely unused. That is the direction that causes real harm.
                string? name = NameFromLockKey(entry.Name);

                string? version = entry.Value.TryGetProperty("version", out System.Text.Json.JsonElement v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString()
                        : null;

                if (name is not null && version is not null)
                {
                    entries.Add((name, version));
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A lockfile that does not parse is unreadable evidence, not evidence of absence. Reporting it
            // as empty would let every package in the cache claim to be unreferenced.
            return null;
        }

        return entries;
    }

    /// <summary>
    /// The package name from a lockfile key.
    /// <para>
    /// Keys are install paths: <c>""</c> for the root, <c>node_modules/left-pad</c>, and for a scoped
    /// package nested inside another, <c>node_modules/a/node_modules/@scope/b</c>. The name is the last
    /// <c>node_modules/</c> segment's remainder, which may itself be <c>@scope/name</c>.
    /// </para>
    /// <para>
    /// Returns null for the root entry and for a link/workspace entry, neither of which is a registry
    /// package — the root is the project itself.
    /// </para>
    /// </summary>
    public static string? NameFromLockKey(string key)
    {
        const string marker = "node_modules/";

        int last = key.LastIndexOf(marker, StringComparison.Ordinal);
        if (last < 0)
        {
            // Either the root ("" or the project name) or a workspace/link entry.
            return null;
        }

        string tail = key[(last + marker.Length)..].Trim();
        return tail.Length == 0 ? null : tail;
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
        string.Equals(locationId, CacheLocation, StringComparison.Ordinal)
        || string.Equals(locationId, GlobalLocation, StringComparison.Ordinal);
}

/// <summary>
/// npm semver ordering. npm versions are always plain semver — including the leading 'v' npm permits in a
/// package.json — so this is the narrowest version ordering of the three plugins.
/// </summary>
public sealed class SemVerVersionOrdering : IVersionOrdering
{
    public VersionComponents? TryParse(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string trimmed = version.TrimStart('v', '=', '^', '~');
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

        bool leftPre = left.Value.Prerelease is not null;
        bool rightPre = right.Value.Prerelease is not null;

        if (leftPre != rightPre)
        {
            return leftPre ? -1 : 1;
        }

        return string.CompareOrdinal(left.Value.Prerelease, right.Value.Prerelease);
    }
}

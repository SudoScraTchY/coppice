using System.Text.RegularExpressions;
using System.Threading;

using Coppice.Parsing;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;
using ParseResult = Coppice.Parsing.ParseResult;

namespace Coppice.Plugins.Go;

/// <summary>
/// The Go ecosystem plugin (T-022, FR-23, 09-ecosystems "go").
/// <para>
/// Go is a Tier-2 plugin in the sense that matters: its LOCATIONS come from <c>go.toml</c>, and this
/// class adds only the two capabilities pure data cannot express — knowing how a module cache is laid
/// out, and knowing how to read a <c>go.sum</c>. Everything about WHERE to look is declared, which is
/// the property T-030 exists to check.
/// </para>
/// <para>
/// Like the .NET plugin, it reaches the outside world only through <see cref="ScanContext"/>. It
/// holds no path of its own and spawns no process without going through <see cref="IProcessRunner"/>.
/// </para>
/// </summary>
public sealed class GoEcosystem : IEcosystem, IInventoryProvider, IReferenceResolver
{
    /// <summary>Locations whose immediate children are modules at <c>{escaped-module}@v{version}</c>.</summary>
    private const string ModCacheLocation = "go-mod-cache";

    /// <summary>Query for the authoritative module cache path.</summary>
    private const string GoModCacheQuery = "go env GOMODCACHE";

    private readonly IFileSystem? _fs;

    public GoEcosystem()
    {
    }

    /// <summary>The form the CLI builds.</summary>
    public GoEcosystem(IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    public string Id => "go";

    public IVersionOrdering Versions { get; } = new GoVersionOrdering();

    /// <summary>
    /// Go is present when the scan resolved the module cache. The question is about the roots, not the
    /// machine: a scan handed no Go locations cannot claim Go is installed.
    /// </summary>
    public bool IsPresent(ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Roots.Any(r => IsOurs(r.LocationId));
    }

    /// <summary>
    /// Enumerates modules. One item per module VERSION.
    /// <para>
    /// The layout is <c>{escaped-module}@v{version}</c>, where the module path has been escaped by Go:
    /// uppercase letters become <c>!</c>-prefixed lowercase, so <c>github.com/Azure/SDK</c> becomes
    /// <c>github.com/!azure/!s!d!k@v1.2.3</c>. Splitting on the LAST '@' matters for two reasons — a
    /// module path may itself contain '@' in a vanity import path, and a version cannot.
    /// </para>
    /// <para>
    /// The <c>cache/download</c> directory sits beside the module directories and is a download cache,
    /// not modules. It is skipped so the report does not offer to delete a module and its own archive
    /// as two separate items.
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
                ModCacheLocation => Modules(ctx, root, Id),
                _ => [],
            })
            {
                yield return item;
            }
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<PortableItem> Modules(ScanContext ctx, ScanRoot root, string ecosystemId)
    {

        // A module lives at the deepest level of its own path: `github.com/!azure/!s!d!k@v1.2.3` is three
        // segments below the cache root, not two. So the walk descends until it either finds a
        // module directory or runs out of depth — a fixed two-level walk (the shape the .NET plugin
        // uses for package-id/version) reports ZERO modules for every real Go cache while looking like
        // a machine that has never run `go get`.
        foreach ((FileEntry moduleDirectory, string modulePath) in WalkForModules(ctx, root.ResolvedPath, prefix: string.Empty, depth: 0))
        {
            if (!TrySplitModuleVersion(moduleDirectory.Name, out string leaf, out string version))
            {
                continue;
            }

            // `modulePath` is every path segment walked to reach this directory; the module name is that
            // path plus the leaf without its @version suffix.
            string module = modulePath.Length == 0 ? leaf : modulePath + "/" + leaf;

            // The escaped directory name is a fact worth carrying: a user looking at
            // `github.com/!azure/!s!d!k` needs to find that name in order to look it up.
            var facts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Required by C-2: without the location id an item's root cannot be proven, so
                // nothing downstream can establish containment. Caught by the conformance harness on
                // the Go plugin's first run — the fact is part of the contract, not a .NET habit.
                ["locationId"] = root.LocationId,
                ["escapedName"] = moduleDirectory.Name,
                ["resolvedFrom"] = root.LocationId,
            };

            yield return new PortableItem(
                ItemId(root.LocationId, "module", module, version),
                ecosystemId,
                "module",
                module,
                version,
                moduleDirectory.Path,
                // A module is a regenerable download, so Safe is the honest starting tier. Policy may
                // lower it; nothing here raises it.
                Risk.Safe,
                facts);
        }
    }

    /// <summary>
    /// Deepest a module path can nest, per <c>module.EscapePath</c>. Real paths are far shallower;
    /// the bound exists so a cache with a pathological tree cannot make the walk unbounded.
    /// </summary>
    private const int MaxModulePathDepth = 16;

    /// <summary>
    /// Yields every directory that is a module directory, descending through path segments.
    /// <para>
    /// Once a directory's own name parses as a module it is yielded and NOT descended into: Go writes
    /// a module's own subdirectories (<c>vendor</c>, internal test packages) beneath it, and those are
    /// part of the module rather than separate items.
    /// </para>
    /// </summary>
    private static IEnumerable<(FileEntry Directory, string PathPrefix)> WalkForModules(
        ScanContext ctx,
        string path,
        string prefix,
        int depth)
    {
        foreach (FileEntry entry in Directories(ctx, path))
        {
            // `cache/download` holds the .zip/.info archives, a cache of downloads rather than modules.
            // Reporting both would count every module's bytes twice.
            if (entry.Name is "cache")
            {
                continue;
            }

            if (TrySplitModuleVersion(entry.Name, out _, out _))
            {
                // The module's NAME is its whole path, not its last segment: the directory is
                // `.../github.com/stretchr/testify@v1.8.4` and the module is `github.com/stretchr/testify`.
                // Taking only `testify` would produce a name matching nothing in any go.sum, so every
                // reference lookup would miss and a full cache would read as entirely unreferenced.
                yield return (entry, prefix);
                continue;
            }

            if (depth >= MaxModulePathDepth)
            {
                continue;
            }

            string nested = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;

            foreach ((FileEntry directory, string modulePrefix) in WalkForModules(ctx, entry.Path, nested, depth + 1))
            {
                yield return (directory, modulePrefix);
            }
        }
    }

    /// <summary>
    /// Splits <c>{escaped-module}@v{version}</c>. The LAST '@' is the separator, and the version must
    /// carry Go's <c>v</c> prefix — <c>@master</c> is a checkout, not a released version.
    /// </summary>
    public static bool TrySplitModuleVersion(string directoryName, out string module, out string version)
    {
        module = string.Empty;
        version = string.Empty;

        int at = directoryName.LastIndexOf('@');
        if (at <= 0 || at == directoryName.Length - 1)
        {
            return false;
        }

        string tail = directoryName[(at + 1)..];
        if (tail.Length < 2 || tail[0] != 'v' || !char.IsAsciiDigit(tail[1]))
        {
            return false;
        }

        // Pseudo-versions and +incompatible/+build metadata are legal Go versions, so this only requires
        // the character after 'v' to be a digit and rejects the obvious non-versions.
        for (int i = 1; i < tail.Length; i++)
        {
            char c = tail[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '+'))
            {
                return false;
            }
        }

        module = directoryName[..at];
        version = tail;
        return true;
    }

    /// <summary>
    /// Three-state module usage from <c>go.sum</c> (FR-07).
    /// <para>
    /// A module is Referenced when some project's <c>go.sum</c> lists it at this version, or lists a
    /// NEWER version of the same module — Go's MVS picks the highest version present, so keeping an
    /// older one alongside is pure waste. Zero readable projects is Unknown, never Unreferenced: a user
    /// who never configured project roots must not be shown their whole module cache as deletable.
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
                Reason = "no projects were discovered, so no module can be proven unused",
            };
        }

        if (_fs is null)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no filesystem port was supplied, so go.sum files cannot be read",
            };
        }

        var referencing = new List<string>();
        int readable = 0;
        int unreadable = 0;

        foreach (Project project in projects.Projects)
        {
            IReadOnlyList<GoSumEntry>? entries = ReadGoSum(project.RootPath);

            if (entries is null)
            {
                unreadable++;
                continue;
            }

            readable++;

            if (entries.Any(e => IsSameModule(e, itemName, itemVersion)))
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
                Reason = $"required by {referencing.Count} project(s) per go.sum",
            };
        }

        // An unreadable project could require anything, so its modules stay Unknown even when every
        // OTHER project was read. Partial evidence does not prove absence.
        if (unreadable > 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = $"{unreadable} project(s) could not be read (no go.sum — run 'go mod tidy')",
            };
        }

        if (readable == 0)
        {
            return new ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no project could be read, so no module can be proven unused",
            };
        }

        return new ReferenceVerdict
        {
            Usage = Ports.Usage.Unreferenced,
            Reason = $"not in any go.sum ({readable} project(s) checked)",
        };
    }

    /// <summary>
    /// True when a <c>go.sum</c> entry names this module at this version OR at a newer one. Go's
    /// minimal version selection resolves to the highest requirement across the build, so a lower
    /// version in the cache alongside a higher one is dead weight.
    /// </summary>
    /// <summary>
    /// True when a <c>go.sum</c> entry proves this EXACT version's source is needed.
    /// <para>
    /// Two rules, and getting the second one wrong is what made a module cache unshrinkable:
    /// </para>
    /// <list type="number">
    /// <item>Only a CONTENT-hash line counts. A <c>/go.mod</c>-only entry means the module graph
    /// mentions this version, not that its source was fetched.</item>
    /// <item>Only the EXACT version counts. Go's minimal version selection builds the highest
    /// requirement in the graph, so a cached version BELOW that is the copy nothing will use. Declaring
    /// it Referenced because go.sum mentions something newer keeps every superseded module alive.</item>
    /// </list>
    /// <para>
    /// This is deliberately conservative in the direction that matters: an exact content-hash match is
    /// proof, and nothing weaker is treated as proof. A false Unreferenced is caught by policy's
    /// keep-latest-N; a false Referenced is permanent.
    /// </para>
    /// </summary>
    private static bool IsSameModule(GoSumEntry entry, string itemName, string itemVersion) =>
        entry.IsContentHash
        && string.Equals(entry.Module, itemName, StringComparison.Ordinal)
        && string.Equals(entry.Version, itemVersion, StringComparison.Ordinal);

    /// <summary>
    /// Go semver comparison: numeric parts, then release-beats-prerelease, then the prerelease text.
    /// <para>
    /// Internal rather than private because <see cref="GoVersionOrdering"/> must use the SAME comparison.
    /// Two orderings that are each internally consistent would pass C-6 while disagreeing about which
    /// module version is newest — and a plugin that disagrees with the kernel about "newest" produces a
    /// plan that keeps the wrong copy.
    /// </para>
    /// </summary>
    internal static int CompareVersions(string left, string right)
    {
        (int[] leftParts, string leftPre) = SplitVersion(left);
        (int[] rightParts, string rightPre) = SplitVersion(right);

        for (int i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            int a = i < leftParts.Length ? leftParts[i] : 0;
            int b = i < rightParts.Length ? rightParts[i] : 0;

            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        // Equal numbers: a release beats a pre-release of the same number, matching semver.
        if (leftPre.Length == 0 && rightPre.Length == 0)
        {
            return 0;
        }

        if (leftPre.Length == 0)
        {
            return 1;
        }

        if (rightPre.Length == 0)
        {
            return -1;
        }

        return string.CompareOrdinal(leftPre, rightPre);
    }

    private static (int[] Numbers, string Pre) SplitVersion(string version)
    {
        // Strip Go's mandatory 'v' prefix first. Without this, int.TryParse("v1") fails, EVERY component
        // becomes 0, and CompareVersions reports all versions equal — so "keep the newest 2" would keep
        // an arbitrary two.
        string trimmed = version.StartsWith('v') ? version[1..] : version;

        string withoutBuild = trimmed.Split('+')[0];
        int dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);

        string numeric = dash < 0 ? withoutBuild : withoutBuild[..dash];
        string pre = dash < 0 ? string.Empty : withoutBuild[(dash + 1)..];

        var numbers = new List<int>(3);
        foreach (string part in numeric.Split('.'))
        {
            // A non-numeric component means this is not a version at all. Reporting 0 silently would
            // make "abc" compare equal to "1.0.0", so it is flagged by returning fewer components than
            // the caller expects — and the caller only ever compares versions it has already validated.
            if (!int.TryParse(part, out int value))
            {
                break;
            }

            numbers.Add(value);
        }

        return ([.. numbers], pre);
    }

    /// <summary>
    /// Reads one project's <c>go.sum</c>, or null when it cannot be read. A missing file is null and
    /// never an empty list — null means "we cannot tell", and an empty list means "it requires
    /// nothing", which is a much stronger and much more dangerous assertion.
    /// </summary>
    private IReadOnlyList<GoSumEntry>? ReadGoSum(string projectRoot)
    {
        char separator = projectRoot.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        string path = projectRoot + separator + "go.sum";

        string content;

        try
        {
            content = _fs!.ReadSmallText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing go.sum is the NORMAL case for a project that has never run `go mod tidy`, so it
            // is null evidence rather than a failure. Letting the port's exception escape here would
            // abort resolution for every project on the machine.
            return null;
        }

        var entries = new List<GoSumEntry>();

        foreach (string line in OutputParsers.SplitLines(content))
        {
            // "module version[/go.mod] hash" — the trailing "/go.mod" marks the go.mod hash line rather
            // than the module content hash. Both name the same module@version, so either is evidence.
            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            bool isContentHash = !fields[1].EndsWith("/go.mod", StringComparison.Ordinal);
            string version = isContentHash ? fields[1] : fields[1][..^"/go.mod".Length];

            entries.Add(new GoSumEntry(fields[0], version, isContentHash));
        }

        return entries;
    }

    /// <summary>v0.1 is read-only, so this plugin never plans a removal — the gateway does, in v0.3.</summary>
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

    /// <summary>
    /// A stable id for one cache entry. The same location, kind, name and version must always hash to
    /// the same id, because that id is what two scans diff against (NFR-06).
    /// </summary>
    private static string ItemId(string locationId, string kind, string name, string version)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{locationId}:{kind}:{name}:{version}"));

        return System.Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static bool IsOurs(string locationId) =>
        string.Equals(locationId, ModCacheLocation, StringComparison.Ordinal);

    /// <summary>The query a caller runs to learn the module cache. Public so the resolver can be tested.</summary>
    public const string ModCacheQuery = GoModCacheQuery;
}

/// <summary>
/// One line of a <c>go.sum</c>.
/// <para>
/// <see cref="IsContentHash"/> is the load-bearing field. <c>go.sum</c> records two hashes per version:
/// the module CONTENT (<c>module version h1:…</c>) for the version the build actually selected, and
/// the <c>go.mod</c> file (<c>module version/go.mod h1:…</c>) for EVERY version anywhere in the module
/// graph. Only the first means source code is needed; the second means the graph mentions it.
/// </para>
/// <para>
/// Treating them alike is not a rounding error. It makes a module whose content was never fetched look
/// as though its source is required, and it makes every superseded version look live — which means a
/// module cache could never be reported as shrinkable.
/// </para>
/// </summary>
public sealed record GoSumEntry(string Module, string Version, bool IsContentHash);

/// <summary>
/// Go version ordering: semver with Go's <c>v</c> prefix, extended to pseudo-versions.
/// <para>
/// Prefixes are stripped rather than rejected, because every <c>go.sum</c> line carries one and a
/// parser that refused them would classify every module as Unknown.
/// </para>
/// </summary>
public sealed class GoVersionOrdering : IVersionOrdering
{
    public VersionComponents? TryParse(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string text = version.StartsWith('v') ? version[1..] : version;

        // Pseudo-versions end in a timestamp-commit form (-0.20191109021931-daa7c04131f5); only the
        // leading numerics are the version proper.
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        string pre = string.Empty;

        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
        }

        string[] parts = text.Split('.');
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

        return GoEcosystem.CompareVersions(x, y);
    }
}

/// <summary>
/// Anchored module-name check used by health checks: a Go module cache's depth-1 entries must look
/// like module directories.
/// <para>
/// The pattern is deliberately loose about the escape encoding, because <c>!</c>-escaping means a
/// module name contains characters that no other ecosystem's layout does. What it does insist on is the
/// presence of an <c>@v</c> version marker somewhere in the name, which is what distinguishes a module
/// from the <c>cache</c> download directory beside it.
/// </para>
/// </summary>
internal sealed partial class GoModuleLayout
{
    [GeneratedRegex(@"@v\d", RegexOptions.CultureInvariant)]
    private static partial Regex VersionMarker();

    public static bool LooksLikeModuleDirectory(string name) => VersionMarker().IsMatch(name);
}

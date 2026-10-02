using System.Text.RegularExpressions;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;
using EntryKind = Coppice.Ports.EntryKind;
using FileEntry = Coppice.Ports.FileEntry;
using Ports = Coppice.Ports;

namespace Coppice.Plugins.Net;

/// <summary>
/// The .NET ecosystem plugin (T-016, 04-plugin-contract).
/// <para>
/// Implements the four capabilities a Tier-1 compiled plugin may implement. Everything it does goes
/// through the ports it was handed: <see cref="Ports.ScanContext"/> is the only source of the
/// filesystem, the process runner and the environment, so the plugin has no way to reach the real
/// disk or spawn a process on its own.
/// </para>
/// <para>
/// The reference resolver reads each discovered project's <c>obj/project.assets.json</c>. That file
/// only exists after <c>dotnet restore</c>, which is why a missing one yields Unknown rather than
/// Unreferenced — see <see cref="Resolve"/>.
/// </para>
/// </summary>
public sealed class DotnetEcosystem : IEcosystem, IInventoryProvider, IReferenceResolver, IHealthChecker
{
    /// <summary>Parameterless for the conformance harness; the resolver degrades without a port.</summary>
    public DotnetEcosystem()
    {
    }

    /// <summary>The form the CLI builds: the plugin is handed the ports it will use.</summary>
    public DotnetEcosystem(Ports.IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    /// <summary>Locations whose contents are packages, in the 09 table's terms.</summary>
    private static readonly string[] PackageLocations = ["nuget-packages"];

    /// <summary>Locations whose immediate children are globally installed tools.</summary>
    private static readonly string[] ToolLocations = ["dotnet-tools"];

    /// <summary>Locations whose immediate children are installed workloads.</summary>
    private static readonly string[] WorkloadLocations = ["dotnet-workloads"];

    public string Id => NetProfile.EcosystemId;

    public IVersionOrdering Versions { get; } = new NetVersionOrdering();

    /// <summary>
    /// The filesystem port, captured for the reference resolver.
    /// <para>
    /// <see cref="IReferenceResolver.Resolve"/> takes only an item name, a version and a project set
    /// — it has no context parameter — yet answering it requires reading each project's
    /// <c>obj/project.assets.json</c>. The contract cannot pass the filesystem, so the plugin holds
    /// the port it was constructed with. That is safe because it is still only ever the PORT: no
    /// plugin method gains filesystem reach that the scan did not already hand over.
    /// </para>
    /// </summary>
    private readonly Ports.IFileSystem? _fs;

    /// <summary>
    /// .NET counts as present when the scan resolved at least one location it owns. Presence is a
    /// question about the roots, not the machine: a scan that was handed no .NET locations cannot
    /// claim .NET is installed, and must not.
    /// </summary>
    public bool IsPresent(Ports.ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return ctx.Roots.Any(r => IsOurs(r.LocationId));
    }

    /// <summary>
    /// Enumerates every package, tool and workload under the scan's roots.
    /// <para>
    /// One item per package VERSION, not per package id: the unit a cache wastes space on is a
    /// stale version, and the unit that can be deleted safely is a version no project references.
    /// NuGet's own bookkeeping directories (a leading dot) are skipped — they are package internals,
    /// not versions, and reporting them would invent cleanup targets that do not exist.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<Ports.PortableItem> Discover(Ports.ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        foreach (Ports.ScanRoot root in ctx.Roots.Where(r => IsOurs(r.LocationId)))
        {
            if (!ctx.FileSystem.DirectoryExists(root.ResolvedPath))
            {
                continue;
            }

            foreach (Ports.PortableItem item in root.LocationId switch
            {
                "nuget-packages" => Packages(ctx, root),
                "dotnet-tools" => FlatEntries(ctx, root, "tool", "toolName"),
                "dotnet-workloads" => FlatEntries(ctx, root, "workload", "workloadId"),
                _ => [],
            })
            {
                yield return item;
            }
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<Ports.PortableItem> Packages(Ports.ScanContext ctx, Ports.ScanRoot root)
    {
        foreach (FileEntry package in Directories(ctx, root.ResolvedPath))
        {
            foreach (FileEntry version in Directories(ctx, package.Path))
            {
                // ".tools" and ".metadata" live beside the versions inside a package id. A leading
                // dot marks NuGet's own bookkeeping, and its names are not versions.
                if (version.Name.StartsWith('.'))
                {
                    continue;
                }

                yield return Item(
                    ctx,
                    root,
                    "package",
                    package.Name,
                    version.Name,
                    version.Path,
                    ("packageId", package.Name));
            }
        }
    }

    private static IEnumerable<Ports.PortableItem> FlatEntries(
        Ports.ScanContext ctx,
        Ports.ScanRoot root,
        string kind,
        string nameFact)
    {
        foreach (FileEntry entry in Directories(ctx, root.ResolvedPath))
        {
            yield return Item(ctx, root, kind, entry.Name, entry.Name, entry.Path, (nameFact, entry.Name));
        }
    }

    private static Ports.PortableItem Item(
        Ports.ScanContext ctx,
        Ports.ScanRoot root,
        string kind,
        string name,
        string version,
        string path,
        params (string Key, string Value)[] facts) =>
        new(
            ItemId: ItemId(root.LocationId, kind, name, version),
            Ecosystem: NetProfile.EcosystemId,
            Kind: kind,
            Name: name,
            Version: version,
            RootPath: path,
            // The tier comes from the profile, never from the plugin's own judgement: a plugin that
            // could raise its own risk tier could quietly authorise its own deletion.
            Risk: root.Tier,
            Facts: BuildFacts(facts, root.LocationId));

    private static Dictionary<string, string> BuildFacts(
        (string Key, string Value)[] facts,
        string locationId)
    {
        Dictionary<string, string> built = new(facts.Length + 1, StringComparer.Ordinal)
        {
            ["locationId"] = locationId,
        };

        foreach ((string key, string value) in facts)
        {
            built[key] = value;
        }

        return built;
    }

    private static IEnumerable<FileEntry> Directories(Ports.ScanContext ctx, string path) =>
        ctx.FileSystem
            .EnumerateEntries(path, new EnumerationRequest { MaxDepth = 1 })
            .Where(e => e.Kind == EntryKind.Directory)
            // Deterministic order regardless of what the filesystem returned (NFR-06).
            .OrderBy(e => e.Name, StringComparer.Ordinal);

    private static bool IsOurs(string locationId) =>
        PackageLocations.Contains(locationId, StringComparer.Ordinal)
        || ToolLocations.Contains(locationId, StringComparer.Ordinal)
        || WorkloadLocations.Contains(locationId, StringComparer.Ordinal);

    /// <summary>
    /// Assigns Referenced / Unreferenced / Unknown for one package (FR-07).
    /// <para>
    /// The rule that matters: <c>Unreferenced</c> requires POSITIVE evidence. At least one project
    /// must have been successfully read and none of them may reference this exact id + version. Zero
    /// readable projects is <c>Unknown</c>, never <c>Unreferenced</c> — otherwise a user who never
    /// configured project roots would be shown their entire NuGet cache as deletable, which is the
    /// single most dangerous thing this tool could do.
    /// </para>
    /// <para>
    /// A project whose assets file is missing is counted but not trusted: its packages resolve to
    /// Unknown, because "we could not read it" and "it uses nothing" are different facts.
    /// </para>
    /// </summary>
    public Ports.ReferenceVerdict Resolve(string itemName, string itemVersion, Ports.ProjectSet projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (projects.Projects.Count == 0)
        {
            return new Ports.ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no projects were discovered, so no package can be proven unused",
            };
        }

        // Without a port there is no way to read an assets file, and guessing would be worse than
        // admitting it: every package would claim to be unused.
        if (_fs is null)
        {
            return new Ports.ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no filesystem port was supplied, so project references cannot be read",
            };
        }

        var reader = new NetReferenceReader(_fs);
        var referencing = new List<string>();
        int readable = 0;
        int unreadable = 0;

        foreach (Ports.Project project in projects.Projects)
        {
            char separator = project.RootPath.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
            IReadOnlyList<PackageReference>? references = reader.ReadReferences(project.RootPath, separator);

            if (references is null)
            {
                unreadable++;
                continue;
            }

            readable++;

            if (references.Any(r => IsSamePackage(r, itemName, itemVersion)))
            {
                referencing.Add(project.RootPath);
            }
        }

        if (referencing.Count > 0)
        {
            return new Ports.ReferenceVerdict
            {
                Usage = Ports.Usage.Referenced,
                ReferencingProjects = [.. referencing.Order(StringComparer.Ordinal)],
                Reason = $"referenced by {referencing.Count} project(s)",
            };
        }

        // An unreadable project could reference anything, so its packages stay Unknown even when
        // every OTHER project was read successfully. Partial evidence does not prove absence.
        if (unreadable > 0)
        {
            return new Ports.ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = $"{unreadable} project(s) could not be read (no obj/project.assets.json — run 'dotnet restore')",
            };
        }

        if (readable == 0)
        {
            return new Ports.ReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = "no project could be read, so no package can be proven unused",
            };
        }

        return new Ports.ReferenceVerdict
        {
            Usage = Ports.Usage.Unreferenced,
            Reason = $"no project references {itemName} {itemVersion} ({readable} project(s) checked)",
        };
    }

    /// <summary>
    /// NuGet package ids are case-insensitive; versions are not. A mismatch here would let a project
    /// referencing "Newtonsoft.Json 13.0.3" fail to match a cached "newtonsoft.json/13.0.3".
    /// </summary>
    private static bool IsSamePackage(PackageReference reference, string itemName, string itemVersion) =>
        string.Equals(reference.PackageId, itemName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(reference.Version, itemVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Health checks (09-ecosystems "Health checks" table, T-013).
    /// <para>
    /// Read-only through the ports: the checks read directory layout and, for the registered-tools
    /// comparison, ask <c>dotnet tool list -g</c> through <see cref="IProcessRunner"/>. Nothing here
    /// mutates anything, which is what C-9 asserts.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Ports.Problem>> CheckAsync(Ports.ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return await new NetHealthChecker(ctx).CheckAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// v0.1 is read-only (06-safety-model), so the plugin never plans a removal. Returning null is
    /// how a capability says "not supported" without throwing (LSP, 04-plugin-contract rule 5).
    /// </summary>
    public Ports.RemovalAction? Plan(Ports.PortableItem item) => null;

    /// <summary>
    /// A stable id for one cache entry: the same location, kind, name and version must always hash
    /// to the same id, because the id is what two scans diff against (NFR-06).
    /// </summary>
    private static string ItemId(string locationId, string kind, string name, string version)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{locationId}:{kind}:{name}:{version}"));

        return System.Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}

/// <summary>Singleton version ordering for .NET: NuGet SemVer, extended to the 4-part SDK form.</summary>
internal sealed class NetVersionOrdering : IVersionOrdering
{
    public VersionComponents? TryParse(string version)
    {
        if (!SdkVersion.TryParse(version, out SdkVersion.Parsed parsed))
        {
            return null;
        }

        return new VersionComponents(
            Number(parsed.Major),
            Number(parsed.Minor),
            Number(parsed.Patch),
            0,
            parsed.Preview,
            null);

        static int Number(string? part) =>
            int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
    }

    /// <summary>
    /// Delegates to the same comparison the inventory uses, so "newest" means one thing everywhere.
    /// A second, subtly different ordering here would let the plugin disagree with the kernel about
    /// which version is newest — and C-6 would still pass, because both are internally consistent.
    /// </summary>
    public int Compare(string? x, string? y) =>
        string.Equals(x, y, StringComparison.Ordinal) ? 0 : SdkVersion.Compare(x!, y!);
}

/// <summary>Every location id the .NET plugin owns, for presence checks and tests.</summary>
public static class NetEcosystemLocations
{
    public static IReadOnlyList<string> All { get; } = ["nuget-packages", "dotnet-tools", "dotnet-workloads", "dotnet-root"];

    private static readonly Regex VersionShaped = new(@"^\d+(\.\d+)+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="name"/> looks like a version rather than a package internals directory.</summary>
    public static bool LooksLikeVersion(string name) =>
        !name.StartsWith('.') && VersionShaped.IsMatch(name);
}

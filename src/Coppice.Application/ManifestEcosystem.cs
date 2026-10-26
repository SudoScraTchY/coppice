using System.Globalization;
using System.Runtime.CompilerServices;
using Coppice.Ports;
using Ports = Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// A conservative, cache-only plugin for a user-supplied manifest (FR-23, OQ-08).
/// <para>
/// A drop-in manifest can name locations, but it cannot ship code. This plugin treats each root as a
/// <c>{name}/{version}</c> cache and emits one Review-tier item per version. It has no reference
/// resolver, removal strategy or health checker, so the kernel's Unknown floor and report-only
/// behaviour apply: the ecosystem can be discovered, but not proposed for deletion.
/// </para>
/// </summary>
internal sealed class ManifestEcosystem : IEcosystem, IInventoryProvider
{
    private readonly IReadOnlyList<string> _locationIds;

    public ManifestEcosystem(string id, IReadOnlyList<string> locationIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(locationIds);

        Id = id;
        _locationIds = locationIds;
        Versions = new ManifestVersionOrdering();
    }

    public string Id { get; }

    public IVersionOrdering Versions { get; }

    public bool IsPresent(Ports.ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Roots.Any(root => _locationIds.Contains(root.LocationId, StringComparer.Ordinal));
    }

    public async IAsyncEnumerable<PortableItem> Discover(Ports.ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        foreach (Ports.ScanRoot root in ctx.Roots.Where(root => _locationIds.Contains(root.LocationId, StringComparer.Ordinal)))
        {
            if (!ctx.FileSystem.DirectoryExists(root.ResolvedPath))
            {
                continue;
            }

            foreach (FileEntry package in ctx.FileSystem.EnumerateEntries(
                root.ResolvedPath,
                new EnumerationRequest { MaxDepth = 1 }))
            {
                if (package.Kind != EntryKind.Directory)
                {
                    continue;
                }

                bool foundVersion = false;

                foreach (FileEntry version in ctx.FileSystem.EnumerateEntries(
                    package.Path,
                    new EnumerationRequest { MaxDepth = 1 }))
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();

                    if (version.Kind != EntryKind.Directory || Versions.TryParse(version.Name) is null)
                    {
                        continue;
                    }

                    foundVersion = true;
                    yield return Item(package.Name, version.Name, version.Path);
                }

                // Some caches store the version directly under the root instead of under a name.
                if (!foundVersion && Versions.TryParse(package.Name) is not null)
                {
                    yield return Item(Id, package.Name, package.Path);
                }
            }
        }

        await Task.CompletedTask;
    }

    private PortableItem Item(string name, string version, string path) =>
        new(
            ItemId: $"{Id}/{name}/{version}",
            Ecosystem: Id,
            Kind: "package",
            Name: name,
            Version: version,
            RootPath: path,
            Risk: Ports.Risk.Review,
            Facts: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifest"] = "user",
            });
}

/// <summary>
/// A total, semver-shaped order for manifest versions. Unknown strings fall back to ordinal comparison
/// so a user manifest can never make keep-latest non-deterministic.
/// </summary>
internal sealed class ManifestVersionOrdering : IVersionOrdering
{
    public VersionComponents? TryParse(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string core = version;
        string? prerelease = null;
        int plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        int dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = core[(dash + 1)..];
            core = core[..dash];
        }

        string[] parts = core.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }

        var numbers = new int[4];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new VersionComponents(numbers[0], numbers[1], numbers[2], numbers[3], prerelease, null);
    }

    public int Compare(string? left, string? right)
    {
        VersionComponents? a = left is null ? null : TryParse(left);
        VersionComponents? b = right is null ? null : TryParse(right);

        if (a is null || b is null)
        {
            return string.CompareOrdinal(left, right);
        }

        int result = a.Value.Major.CompareTo(b.Value.Major);
        if (result == 0)
        {
            result = a.Value.Minor.CompareTo(b.Value.Minor);
        }

        if (result == 0)
        {
            result = a.Value.Patch.CompareTo(b.Value.Patch);
        }

        if (result == 0)
        {
            result = a.Value.Build.CompareTo(b.Value.Build);
        }

        if (result != 0)
        {
            return result;
        }

        // A release sorts above its prereleases; otherwise compare the identifiers ordinally.
        if (a.Value.Prerelease is null && b.Value.Prerelease is not null)
        {
            return 1;
        }

        if (a.Value.Prerelease is not null && b.Value.Prerelease is null)
        {
            return -1;
        }

        return string.CompareOrdinal(a.Value.Prerelease, b.Value.Prerelease);
    }
}

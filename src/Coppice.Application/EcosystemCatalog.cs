using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Manifests;
using Coppice.Plugins.Go;
using Coppice.Plugins.Net;
using Coppice.Plugins.Node;
using Coppice.Plugins.Rust;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// The shipped ecosystem profiles, as data.
/// <para>
/// This is the extension point T-048 exists for. A new ecosystem is one entry here plus its manifest
/// and plugin; neither <see cref="ScanService"/> nor the kernel learns a new name. The catalog holds
/// only metadata, so <see cref="ScanWiring"/>, <see cref="ApplyWiring"/> and the plan preview can use
/// it without a filesystem or a process runner.
/// </para>
/// </summary>
public static class EcosystemCatalog
{
    private static readonly Lazy<IReadOnlyList<EcosystemLocation>> BuiltInLocations = new(BuildLocations);

    private static readonly Lazy<IReadOnlyDictionary<string, IVersionOrdering>> BuiltInOrderings = new(
        () => Definitions!.ToDictionary(
            d => d.Id,
            d => d.CreateVersions(),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>Every location of every shipped ecosystem, in profile order (dotnet, go, rust, node).</summary>
    public static IReadOnlyList<EcosystemLocation> Locations => BuiltInLocations.Value;

    /// <summary>
    /// Plugin-owned orderings keyed by ecosystem id. The plan preview consumes this table instead of
    /// knowing which ecosystems exist; a missing entry means "no ordering", never a guessed one.
    /// </summary>
    public static IReadOnlyDictionary<string, IVersionOrdering> VersionOrderings => BuiltInOrderings.Value;

    public static EcosystemLocation? FindLocation(string locationId) =>
        Locations.FirstOrDefault(l => string.Equals(l.Id, locationId, StringComparison.Ordinal));

    public static EcosystemLocation? FindLocation(string ecosystemId, string locationId) =>
        Locations.FirstOrDefault(l =>
            string.Equals(l.EcosystemId, ecosystemId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Id, locationId, StringComparison.Ordinal));

    /// <summary>
    /// Materialises the catalog with live plugin instances. One plugin per ecosystem is enough: the
    /// capabilities are read-only, and the filesystem port is the only state a plugin holds.
    /// <para>
    /// <paramref name="additional"/> is the registration seam: extra entries are appended to the
    /// shipped definitions, so a fifth ecosystem travels through exactly the same construction path as
    /// the built-ins. Duplicate ids are refused by <see cref="EcosystemRegistry"/>.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<EcosystemPlugin> CreatePlugins(
        IFileSystem fs,
        IEnumerable<EcosystemPlugin>? additional = null) =>
    [
        .. Definitions.Select(d => new EcosystemPlugin(
            d.Id,
            d.DisplayName,
            d.Locations(),
            d.CreatePlugin(fs),
            d.CreateVersions())),
        .. additional ?? [],
    ];

    private static IReadOnlyList<EcosystemLocation> BuildLocations() =>
    [
        .. Definitions.SelectMany(d => d.Locations()),
    ];

    // A definition is registration data: id, where its locations come from, how to build the plugin,
    // and the one piece of policy (an allowlist shape) that cannot be derived from a manifest layout.
    private static readonly IReadOnlyDictionary<string, string> NetAllowlists =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The NuGet global-packages cache is {id}/{version}; this is the shape the gateway
            // already validated before T-048 and it stays the only .NET path-delete allowlist.
            ["nuget-packages"] = "*/*",
        };

    private static readonly IReadOnlyList<EcosystemDefinition> Definitions =
    [
        new(
            Id: NetProfile.EcosystemId,
            DisplayName: "NET",
            ManifestId: null,
            CreatePlugin: fs => new DotnetEcosystem(fs),
            CreateVersions: () => new DotnetEcosystem().Versions,
            Allowlists: NetAllowlists,
            CodeLocations: NetLocations()),

        new(
            Id: "go",
            DisplayName: "Go",
            ManifestId: "go",
            CreatePlugin: fs => new GoEcosystem(fs),
            CreateVersions: () => new GoEcosystem().Versions,
            Allowlists: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Module paths nest arbitrarily (<host>/<org>/<name>@vX), which the gateway's
                // one-segment-per-`*` shape cannot express. No path-delete allowlist is the honest
                // answer; Go's native route is `go clean -modcache`.
                ["go-mod-cache"] = string.Empty,
            },
            CodeLocations: null),

        new(
            Id: "rust",
            DisplayName: "Rust",
            ManifestId: "rust",
            CreatePlugin: fs => new RustEcosystem(fs),
            CreateVersions: () => new RustEcosystem().Versions,
            Allowlists: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // src/<registry>/<name>-<version>: the registry segment is real even though the
                // manifest's layout names only the crate.
                ["cargo-registry-src"] = "*/*",
                ["rustup-toolchains"] = "*",
            },
            CodeLocations: null),

        new(
            Id: "node",
            DisplayName: "Node.js",
            ManifestId: "node",
            CreatePlugin: fs => new NodeEcosystem(fs),
            CreateVersions: () => new NodeEcosystem().Versions,
            Allowlists: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // layout is "" because the whole cache is the unit, but the item the plugin emits is
                // the `_cacache` child, so a one-segment allowlist describes it exactly.
                ["npm-cache"] = "*",
                ["npm-global"] = "*",
            },
            CodeLocations: null),
    ];

    /// <summary>
    /// The .NET table, converted once. Sources keep the spec's precedence; defaults stay per-OS
    /// because the NuGet temp and http-cache rows genuinely differ across platforms.
    /// </summary>
    private static IReadOnlyList<EcosystemLocation> NetLocations() =>
    [
        .. NetProfile.Locations.Select(location => new EcosystemLocation(
            NetProfile.EcosystemId,
            location.Id,
            Kebab(location.Kind.ToString()),
            location.ResolveAll ? ResolveMode.All : ResolveMode.First,
            new Ports.FingerprintSpec
            {
                RequiredPaths = location.Fingerprint.RequiredPaths,
                EntryPatterns = location.Fingerprint.EntryPatterns,
                LayoutRatio = location.Fingerprint.LayoutRatio,
            },
            location.Tier,
            location.Owner.ToString().ToLowerInvariant(),
            InstallerOwned: location.Owner == InstallOwner.Msi,
            AllowlistPattern: NetAllowlists.GetValueOrDefault(location.Id),
            ConfidencePenalty: 0,
            Sources: NetSources(location),
            DefaultPaths: new Dictionary<OperatingSystemKind, string>(location.OsDefaults),
            SupportedOperatingSystems: location.SupportedOperatingSystems)),
    ];

    private static IReadOnlyList<LocationSource> NetSources(NetLocation location)
    {
        var sources = new List<LocationSource>();

        if (!string.IsNullOrWhiteSpace(location.ToolQuery))
        {
            // `dotnet --list-sdks` prints an SDK directory; the location is the root above it.
            bool sdkQuery = string.Equals(location.Id, "dotnet-root", StringComparison.Ordinal);
            sources.Add(new LocationSource(
                ResolvedVia.Tool,
                Run: location.ToolQuery,
                Parse: sdkQuery ? ManifestParserKind.SdkBracketPaths : ManifestParserKind.LabelValue,
                Label: sdkQuery ? null : DotnetToolLabel(location.Id),
                PathIsSdkInstall: sdkQuery));
        }

        sources.AddRange(location.EnvVariables.Select(variable =>
            new LocationSource(ResolvedVia.Env, Variable: variable)));

        return sources;
    }

    /// <summary>
    /// The label a `dotnet nuget locals` query files its answer under. The location ids and the tool's
    /// labels are not identical (`nuget-http-cache` is `http-cache`), and reading the wrong label
    /// silently falls through to a default.
    /// </summary>
    private static string DotnetToolLabel(string locationId) => locationId switch
    {
        "nuget-packages" => "global-packages",
        "nuget-http-cache" => "http-cache",
        "nuget-temp" => "temp",
        _ => locationId,
    };

    /// <summary>
    /// Converts a manifest location. Everything is declared data. The allowlist is NOT derived from
    /// <c>layout</c>: a location must be explicitly listed in the definition's allowlist table to be
    /// eligible for a path delete. Layout is display metadata and can change without a safety review,
    /// so deriving a delete shape from it would silently grant deletion to a new layout.
    /// </summary>
    private static EcosystemLocation FromManifest(EcosystemDefinition definition, LocationRecord record)
    {
        var defaults = new Dictionary<OperatingSystemKind, string>();
        var sources = new List<LocationSource>();

        foreach (ManifestSource source in record.Sources)
        {
            if (source.Via == ResolvedVia.Default)
            {
                if (!string.IsNullOrWhiteSpace(source.Path))
                {
                    defaults[OperatingSystemKind.Windows] = source.Path;
                    defaults[OperatingSystemKind.Linux] = source.Path;
                    defaults[OperatingSystemKind.MacOS] = source.Path;
                }

                continue;
            }

            sources.Add(new LocationSource(
                source.Via,
                Run: source.Run,
                Parse: source.Parse,
                Variable: source.Var,
                PathTemplate: source.Path));
        }

        // Fail-closed: an unlisted location, or an empty entry, gets no path-delete allowlist.
        string? configuredAllowlist = definition.Allowlists.TryGetValue(record.Id, out string? configured)
            && configured.Length > 0
                ? configured
                : null;

        return new EcosystemLocation(
            definition.Id,
            record.Id,
            record.KindText,
            record.Resolve,
            new Ports.FingerprintSpec
            {
                RequiredPaths = record.Fingerprint.RequiredMarkers,
                EntryPatterns = record.Fingerprint.EntryPatterns,
                LayoutRatio = record.Fingerprint.LayoutRatio,
            },
            record.RiskCeiling switch
            {
                "safe" => Risk.Safe,
                "review" => Risk.Review,
                _ => Risk.Review,
            },
            record.InstallerOwned ? "installer" : "user",
            record.InstallerOwned,
            configuredAllowlist,
            record.ConfidencePenalty,
            sources,
            defaults,
            [OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS]);
    }

    private static string Kebab(string pascal)
    {
        var builder = new System.Text.StringBuilder(pascal.Length + 4);

        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private sealed record EcosystemDefinition(
        string Id,
        string DisplayName,
        string? ManifestId,
        Func<IFileSystem, IEcosystem> CreatePlugin,
        Func<IVersionOrdering> CreateVersions,
        IReadOnlyDictionary<string, string> Allowlists,
        IReadOnlyList<EcosystemLocation>? CodeLocations)
    {
        /// <summary>
        /// Manifest-backed locations are loaded lazily on first use, so a definition whose profile is
        /// missing (or invalid) contributes nothing rather than taking the whole catalog down.
        /// </summary>
        public IReadOnlyList<EcosystemLocation> Locations()
        {
            if (CodeLocations is not null)
            {
                return CodeLocations;
            }

            ManifestLoadResult? manifest = ManifestLoader.LoadBuiltInResources()
                .FirstOrDefault(m => string.Equals(m.Id, ManifestId, StringComparison.Ordinal));

            if (manifest is null || !manifest.IsValid)
            {
                return [];
            }

            return
            [
                .. manifest.Validation.Locations.Select(record => FromManifest(this, record)),
            ];
        }
    }
}

/// <summary>One ecosystem as the registry sees it: its locations plus live capability implementations.</summary>
public sealed record EcosystemPlugin(
    string Id,
    string DisplayName,
    IReadOnlyList<EcosystemLocation> Locations,
    IEcosystem Instance,
    IVersionOrdering Versions);

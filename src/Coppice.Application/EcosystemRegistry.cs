using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// The application's view of which ecosystems can run this scan.
/// <para>
/// The registry is the only place that maps a location id to the plugin that owns it. The scan
/// coordinator asks the registry; it never switches on an ecosystem name. Duplicate ids are refused
/// at construction, because a location claimed by two plugins would make "who scans this root?"
/// depend on registration order.
/// </para>
/// </summary>
public sealed class EcosystemRegistry
{
    private readonly IReadOnlyList<EcosystemPlugin> _ecosystems;
    private readonly Dictionary<string, EcosystemPlugin> _byEcosystem;
    private readonly Dictionary<string, (EcosystemPlugin Plugin, EcosystemLocation Location)> _byLocation;

    public EcosystemRegistry(IEnumerable<EcosystemPlugin> ecosystems)
    {
        ArgumentNullException.ThrowIfNull(ecosystems);

        _ecosystems = [.. ecosystems];
        _byEcosystem = new Dictionary<string, EcosystemPlugin>(StringComparer.OrdinalIgnoreCase);
        _byLocation = new Dictionary<string, (EcosystemPlugin, EcosystemLocation)>(StringComparer.Ordinal);

        foreach (EcosystemPlugin ecosystem in _ecosystems)
        {
            if (!_byEcosystem.TryAdd(ecosystem.Id, ecosystem))
            {
                throw new ArgumentException($"Ecosystem '{ecosystem.Id}' is registered twice.", nameof(ecosystems));
            }

            foreach (EcosystemLocation location in ecosystem.Locations)
            {
                if (!_byLocation.TryAdd(location.Id, (ecosystem, location)))
                {
                    throw new ArgumentException(
                        $"Location '{location.Id}' is claimed by more than one ecosystem.",
                        nameof(ecosystems));
                }
            }
        }
    }

    /// <summary>
    /// The shipped catalog, materialised against this run's filesystem port.
    /// <para>
    /// <paramref name="additional"/> is how a not-yet-shipped ecosystem is registered. Production
    /// composition roots call this with no extra entries; a fifth-ecosystem test calls it with one, so
    /// the test and production share the same registration factory rather than a test-only constructor.
    /// </para>
    /// </summary>
    public static EcosystemRegistry CreateDefault(
        IFileSystem fs,
        IEnumerable<EcosystemPlugin>? additional = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        return new EcosystemRegistry(EcosystemCatalog.CreatePlugins(fs, additional));
    }

    public IReadOnlyList<EcosystemPlugin> Ecosystems => _ecosystems;

    public IReadOnlyList<EcosystemLocation> Locations =>
        [.. _ecosystems.SelectMany(e => e.Locations)];

    public EcosystemPlugin? FindEcosystem(string ecosystemId) =>
        _byEcosystem.GetValueOrDefault(ecosystemId);

    public (EcosystemPlugin Plugin, EcosystemLocation Location)? FindLocation(string locationId) =>
        _byLocation.TryGetValue(locationId, out (EcosystemPlugin Plugin, EcosystemLocation Location) found)
            ? found
            : null;

    /// <summary>
    /// Version orderings for every registered ecosystem. The plan preview uses this instead of naming
    /// ecosystems itself, so "keep newest" is correct for a plugin added after the preview was written.
    /// </summary>
    public IReadOnlyDictionary<string, IVersionOrdering> VersionOrderings =>
        _ecosystems.ToDictionary(e => e.Id, e => e.Versions, StringComparer.OrdinalIgnoreCase);
}

using System.Globalization;
using System.Runtime.CompilerServices;
using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Ports;
using CoreItem = Coppice.Core.Domain.Item;
using CoreScanContext = Coppice.Core.Scanning.ScanContext;
using PortsScanContext = Coppice.Ports.ScanContext;

namespace Coppice.Application;

/// <summary>
/// Adapts every registered plugin's <see cref="IInventoryProvider"/> to the kernel's enumerator shape.
/// <para>
/// There is exactly ONE adapter, not one per ecosystem: it asks the registry which plugin owns the
/// root and hands that plugin a portable context. A new ecosystem therefore needs no entry here.
/// Containment, quarantine skipping, de-duplication and ordering stay in <see cref="ScanPipeline"/>,
/// where they were already enforced for every root.
/// </para>
/// </summary>
internal sealed class EcosystemInventoryEnumerator : IEntryEnumerator
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly EcosystemRegistry _registry;
    private readonly Ports.ProjectSet _projects;
    private readonly IClock _clock;

    public EcosystemInventoryEnumerator(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        EcosystemRegistry registry,
        Ports.ProjectSet projects,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(clock);

        _fs = fs;
        _runner = runner;
        _env = env;
        _registry = registry;
        _projects = projects;
        _clock = clock;
    }

    public string Id => "ecosystem-inventory";

    public async IAsyncEnumerable<CoreItem> EnumerateAsync(
        CoreScanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        (EcosystemPlugin Plugin, EcosystemLocation Location)? owner = _registry.FindLocation(context.LocationId);

        if (owner is null || owner.Value.Plugin.Instance is not IInventoryProvider inventory)
        {
            // Declining an unowned root is not an error: the pipeline offers every root to every
            // provider, and a provider that cannot answer for this id must stay silent.
            yield break;
        }

        EcosystemPlugin plugin = owner.Value.Plugin;
        EcosystemLocation location = owner.Value.Location;

        var portable = new PortsScanContext(
            [location.ToScanRoot(context.RootPath, Ports.RootRole.Active)],
            _projects,
            _fs,
            _runner,
            _env,
            _clock,
            cancellationToken);

        await foreach (PortableItem item in inventory.Discover(portable).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return ToCoreItem(item, location, context.LocationId);
        }
    }

    /// <summary>
    /// One translation from a plugin's portable item to the kernel's item.
    /// <para>
    /// `referenceName` is the name a resolver matches against (Rust's directory is `serde-1.0.197`,
    /// but Cargo.lock names `serde`); it is a fact so the adapter needs no ecosystem knowledge. The
    /// last-write instant is recorded here for every ecosystem, which is what makes a later
    /// `apply --strict` drift check possible without asking the plugin to carry it.
    /// </para>
    /// </summary>
    private CoreItem ToCoreItem(PortableItem item, EcosystemLocation location, string locationId)
    {
        string path = item.RootPath;
        string name = item.Facts.TryGetValue("referenceName", out string? referenceName)
            && referenceName.Length > 0
                ? referenceName
                : item.Name;

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["locationId"] = locationId,
        };

        if (_fs.GetEntry(path) is { } entry)
        {
            facts["lastWriteUtc"] = entry.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
        }

        foreach (KeyValuePair<string, string> fact in item.Facts)
        {
            facts[fact.Key] = fact.Value;
        }

        // The root's location is authoritative: a plugin cannot re-label an item into another
        // location and thereby change which root the gateway later binds it to.
        facts["locationId"] = locationId;

        return new CoreItem(
            Ecosystem: location.EcosystemId,
            Kind: item.Kind,
            Name: name,
            Version: item.Version,
            LocationId: locationId,
            Path: path,
            Size: _fs.MeasureSize(path),
            Risk: item.Risk switch
            {
                Ports.Risk.Safe => Risk.Safe,
                Ports.Risk.Review => Risk.Review,
                _ => Risk.Manual,
            },
            Facts: Facts.From(facts));
    }
}

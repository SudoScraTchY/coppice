using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Projects;
using Coppice.Core.Scanning;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;
using CoreProject = Coppice.Core.Projects.Project;
using CoreProjectSet = Coppice.Core.Projects.ProjectSet;
using CoreScanRoot = Coppice.Core.Scanning.ScanRoot;
using PortProject = Coppice.Ports.Project;
using PortProjectSet = Coppice.Ports.ProjectSet;
using PortReferenceVerdict = Coppice.Ports.ReferenceVerdict;

namespace Coppice.Application;

/// <summary>What one scan found, ready to render (FR-18).</summary>
public sealed record ScanReport(
    string SnapshotId,
    OperatingSystemKind OS,
    IReadOnlyList<Item> Items,
    IReadOnlyList<ScanIssue> Issues,
    IReadOnlyList<Problem> Problems,
    UsageBreakdown Usage,
    IReadOnlyList<string> Ecosystems,
    int ProjectCount,
    string[] OnboardingHints);

/// <summary>
/// Runs a full read-only scan and stores it (T-015), across every registered ecosystem (T-048).
/// <para>
/// The pipeline is: discover projects → enumerate inventory through each root's owning plugin →
/// resolve references with that plugin's resolver → snapshot. Every stage is read-only; the only
/// write is the snapshot, which goes to the state store, never to a scanned location.
/// </para>
/// </summary>
public sealed class ScanService
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly IStateStore _state;
    private readonly IClock _clock;
    private readonly EcosystemRegistry _registry;

    public ScanService(IFileSystem fs, IProcessRunner runner, IEnvironment env, IStateStore state, IClock? clock = null)
        : this(fs, runner, env, state, clock, registry: null)
    {
    }

    /// <summary>
    /// The registry is injectable so a new ecosystem can be exercised end to end without editing this
    /// class. Production wiring passes null and gets the shipped catalog.
    /// </summary>
    public ScanService(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        IStateStore state,
        IClock? clock,
        EcosystemRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);

        _fs = fs;
        _runner = runner;
        _env = env;
        _state = state;
        _clock = clock ?? new DefaultClock();
        _registry = registry ?? EcosystemRegistry.CreateDefault(fs);
    }

    /// <summary>
    /// Scans the cleanable roots. <paramref name="projectRoots"/> come from the repeatable
    /// <c>--projects</c> flag; without them, reference resolution cannot prove anything and everything
    /// stays Unknown.
    /// </summary>
    public Task<ScanReport> RunAsync(IReadOnlyList<Ports.ScanRoot> roots, IReadOnlyList<string> projectRoots) =>
        RunAsync(roots, projectRoots, CancellationToken.None);

    public async Task<ScanReport> RunAsync(
        IReadOnlyList<Ports.ScanRoot> roots,
        IReadOnlyList<string> projectRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(projectRoots);

        OperatingSystemKind os = _env.OS;

        CoreProjectSet projects = new ProjectDiscoveryService(_fs).Discover(projectRoots);
        PortProjectSet portProjects = ToPortProjects(projects);

        // One enumerator, driven by the registry: every root is handed to the plugin that owns its
        // location id. The kernel still applies containment, the quarantine skip, de-duplication and
        // ordering exactly as it did for the single .NET enumerator.
        var pipeline = new ScanPipeline(
            _fs,
            os,
            [new EcosystemInventoryEnumerator(_fs, _runner, _env, _registry, portProjects, _clock)],
            _env.HomeDirectory);

        var coreRoots = roots.Select(r => new CoreScanRoot(r.LocationId, r.ResolvedPath)).ToList();

        ScanResult result = await pipeline.RunAsync(coreRoots, progress: null, cancellationToken).ConfigureAwait(false);

        // Classify usage now, while the projects are in hand. Resolving later — after the snapshot —
        // would leave stored items with a usage verdict that no longer matches the evidence.
        var annotated = new List<Item>(result.Items.Count);
        foreach (Item item in result.Items)
        {
            PortReferenceVerdict verdict = ResolveUsage(item, portProjects);
            annotated.Add(item with
            {
                Facts = item.Facts.With(
                [
                    new KeyValuePair<string, string>("usage", verdict.Usage.ToString()),
                    new KeyValuePair<string, string>("usageReason", Sanitize(verdict.Reason)),
                ]),
                // The Unknown -> Safe floor, applied here so every stored item carries a tier that
                // already respects it for EVERY ecosystem, not only .NET.
                Risk = UsageFloor.Apply(verdict.Usage, item.Risk),
            });
        }

        Item[] ordered = [.. Deterministic.OrderItems(annotated)];

        IReadOnlyList<string> ecosystems = CollectEcosystems(roots, ordered);

        Snapshot snapshot = Snapshot.Create(
            Snapshot.FormatId(_clock.UtcNow),
            _clock.UtcNow,
            ordered,
            result.Issues,
            ecosystems);

        await new SnapshotStore(_state, _clock)
            .SaveAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);

        // Health checks run through the PLUGIN's capability, not a CLI-local copy (T-016). The kernel
        // asks a question; the ecosystem answers it. Only ecosystems that implement the capability are
        // asked, and each problem keeps the id of the ecosystem that reported it.
        var pluginContext = new Ports.ScanContext(
            roots,
            portProjects,
            _fs,
            _runner,
            _env,
            _clock,
            cancellationToken);

        var problems = new List<CoreDomain.Problem>();
        foreach (EcosystemPlugin ecosystem in _registry.Ecosystems)
        {
            if (ecosystem.Instance is not IHealthChecker health)
            {
                continue;
            }

            IReadOnlyList<Ports.Problem> reported =
                await health.CheckAsync(pluginContext).ConfigureAwait(false);

            problems.AddRange(reported.Select(p => ToCoreProblem(p, ecosystem.Id)));
        }

        problems.Sort(static (a, b) =>
        {
            int bySeverity = b.Severity.CompareTo(a.Severity);
            if (bySeverity != 0)
            {
                return bySeverity;
            }

            int byCode = string.CompareOrdinal(a.Code, b.Code);
            return byCode != 0 ? byCode : string.CompareOrdinal(a.Path ?? string.Empty, b.Path ?? string.Empty);
        });

        string[] hints = BuildOnboardingHints(projects, ordered);

        return new ScanReport(
            snapshot.SnapshotId,
            os,
            ordered,
            result.Issues,
            problems,
            UsageBreakdown.FromItems(ordered),
            ecosystems,
            projects.Projects.Count,
            hints);
    }

    /// <summary>
    /// The owning plugin answers the usage question. A registered ecosystem with no resolver, or an
    /// item whose ecosystem is not registered, is Unknown — never Unreferenced.
    /// </summary>
    private PortReferenceVerdict ResolveUsage(Item item, PortProjectSet projects)
    {
        EcosystemPlugin? ecosystem = _registry.FindEcosystem(item.Ecosystem);

        if (ecosystem?.Instance is not IReferenceResolver resolver)
        {
            return new PortReferenceVerdict
            {
                Usage = Ports.Usage.Unknown,
                Reason = $"no reference resolver is registered for ecosystem '{item.Ecosystem}'",
            };
        }

        return resolver.Resolve(item.Name, item.Version, projects);
    }

    /// <summary>
    /// Ecosystems the snapshot covers: those that own a scanned root, plus any ecosystem an item
    /// declares. Sorted ordinally so the stored list is deterministic for the same fixture.
    /// </summary>
    private IReadOnlyList<string> CollectEcosystems(
        IReadOnlyList<Ports.ScanRoot> roots,
        IReadOnlyList<Item> items) =>
    [
        .. roots
            .Select(r => _registry.FindLocation(r.LocationId)?.Location.EcosystemId)
            .Concat(items.Select(i => i.Ecosystem))
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Projects in the portable shape the plugin boundary speaks. Core and Ports declare the same
    /// record twice — deliberately, so a plugin cannot reach kernel behaviour through a discovery
    /// result — and this is the single translation between them.
    /// </summary>
    private static PortProjectSet ToPortProjects(CoreProjectSet projects) => new(
        [.. projects.Projects.Select(p => new PortProject(p.RootPath, p.MarkerIds, p.Ecosystems) { MarkerFiles = p.MarkerFiles })],
        projects.Ecosystems,
        [.. projects.Issues.Select(i => new Ports.PortableScanIssue(i.Code, i.Summary, i.Path))]);

    /// <summary>Maps one portable problem into the Core shape the report and snapshot render.</summary>
    private static CoreDomain.Problem ToCoreProblem(Ports.Problem problem, string ecosystemId) => new()
    {
        Ecosystem = ecosystemId,
        Code = problem.Code,
        Severity = problem.Severity switch
        {
            Ports.Severity.Error => CoreDomain.Severity.Error,
            Ports.Severity.Warning => CoreDomain.Severity.Warning,
            _ => CoreDomain.Severity.Info,
        },
        Summary = problem.Summary,
        Path = string.IsNullOrEmpty(problem.Path) ? null : problem.Path,
        Detail = CoreDomain.Facts.From(
            [.. problem.Data
                .Select(kv => new KeyValuePair<string, string>(kv.Key, Sanitize(kv.Value)))
                .Append(new KeyValuePair<string, string>("locationId", problem.LocationId ?? string.Empty))]),
    };

    /// <summary>
    /// 07: onboarding hints are derived from what the scan found, never from an ecosystem id. With
    /// no project roots every item is Unknown and nothing can be considered Safe; with projects but
    /// no readable lock/assets file the same holds until the user restores. A machine with nothing
    /// to scan gets no hint, because there is nothing useful to suggest.
    /// </summary>
    private static string[] BuildOnboardingHints(CoreProjectSet projects, IReadOnlyList<Item> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        if (projects.IsEmpty)
        {
            return
            [
                "No projects were found. Reference resolution needs project roots: pass --projects <path>. "
                + "Until then every package is Unknown and nothing is Safe.",
            ];
        }

        if (items.All(i => ItemUsage.Of(i) == Usage.Unknown))
        {
            return
            [
                $"Found {projects.Projects.Count} project(s), but no lock or assets file could be read. "
                + "Run your package manager's restore so coppice can tell what is actually in use.",
            ];
        }

        return [];
    }

    /// <summary>
    /// Facts keys allow only ASCII letters, digits and underscores, and the VALUE goes into a
    /// reason a user reads — so quotes and newlines are stripped rather than allowed to break the
    /// table and the CSV export downstream.
    /// </summary>
    private static string Sanitize(string reason) =>
        reason.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();

    private sealed class DefaultClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

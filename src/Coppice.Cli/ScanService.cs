using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Projects;
using Coppice.Core.Scanning;
using Coppice.Core.Snapshots;
using Coppice.Plugins.Net;
using Coppice.Ports;

namespace Coppice.Cli;

/// <summary>What one scan found, ready to render (FR-18).</summary>
public sealed record ScanReport(
    string SnapshotId,
    OperatingSystemKind OS,
    IReadOnlyList<Item> Items,
    IReadOnlyList<ScanIssue> Issues,
    UsageBreakdown Usage,
    IReadOnlyList<string> Ecosystems,
    int ProjectCount,
    string[] OnboardingHints);

/// <summary>
/// Runs a full read-only scan and stores it (T-015).
/// <para>
/// The pipeline is: resolve roots → discover projects → read references → enumerate inventory →
/// snapshot. Every stage is read-only; the only write is the snapshot, which goes to the state
/// store, never to a scanned location.
/// </para>
/// </summary>
public sealed class ScanService
{
    private readonly IFileSystem _fs;
    private readonly IEnvironment _env;
    private readonly IStateStore _state;
    private readonly IClock _clock;

    public ScanService(IFileSystem fs, IEnvironment env, IStateStore state, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);

        _fs = fs;
        _env = env;
        _state = state;
        _clock = clock ?? new DefaultClock();
    }

    /// <summary>
    /// Scans the cleanable roots. <paramref name="projectRoots"/> come from config or
    /// <c>--projects</c>; without them, reference resolution cannot prove anything and everything
    /// stays Unknown.
    /// </summary>
    public async Task<ScanReport> RunAsync(
        IReadOnlyList<ScanRoot> roots,
        IReadOnlyList<string> projectRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(projectRoots);

        OperatingSystemKind os = _env.OS;

        var projects = new ProjectDiscoveryService(_fs).Discover(projectRoots);

        var pipeline = new ScanPipeline(
            _fs,
            os,
            [new NuGetPackageEnumerator(_fs, os)],
            _env.HomeDirectory);

        ScanResult result = await pipeline.RunAsync(roots, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Classify usage now, while the projects are in hand. Resolving later — after the snapshot —
        // would leave stored items with a usage verdict that no longer matches the evidence.
        var resolver = new ReferenceResolver();
        var reader = new NetReferenceReader(_fs);
        char separator = os == OperatingSystemKind.Windows ? '\\' : '/';

        foreach (Project project in projects.Projects)
        {
            resolver.AddProject(project.RootPath, reader.ReadReferences(project.RootPath, separator));
        }

        var annotated = new List<Item>(result.Items.Count);
        foreach (Item item in result.Items)
        {
            ReferenceVerdict verdict = resolver.Resolve(item.Name, item.Version);
            annotated.Add(item with
            {
                Facts = item.Facts.With(
                [
                    new KeyValuePair<string, string>("usage", verdict.Usage.ToString()),
                new KeyValuePair<string, string>("usageReason", Sanitize(verdict.Reason)),
            ]),
                // The Unknown -> Safe floor, applied here so every stored item carries a tier that
                // already respects it.
                Risk = ReferenceResolver.ApplyUsageFloor(verdict, item.Risk),
            });
        }

        Item[] ordered = [.. Deterministic.OrderItems(annotated)];

        Snapshot snapshot = Snapshot.Create(
            Snapshot.FormatId(_clock.UtcNow),
            _clock.UtcNow,
            ordered,
            result.Issues,
            [.. result.Items.Select(i => i.Ecosystem).Where(e => e.Length > 0).Distinct().DefaultIfEmpty(NetProfile.EcosystemId)]);

        await new SnapshotStore(_state, _clock)
            .SaveAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);

        string[] hints = BuildOnboardingHints(projects, resolver, ordered);

        return new ScanReport(
            snapshot.SnapshotId,
            os,
            ordered,
            result.Issues,
            UsageBreakdown.FromItems(ordered),
            [NetProfile.EcosystemId],
            projects.Projects.Count,
            hints);
    }

    /// <summary>
    /// 07: the first run with no project roots prints an onboarding hint, because without projects
    /// every item is Unknown and nothing can be considered Safe. Without this the tool looks broken
    /// on a fresh machine.
    /// </summary>
    private static string[] BuildOnboardingHints(ProjectSet projects, ReferenceResolver resolver, IReadOnlyList<Item> items)
    {
        var hints = new List<string>();

        if (projects.IsEmpty)
        {
            hints.Add(
                "No projects were found. Reference resolution needs project roots: pass --projects <path> "
                + "or set [projects] roots in config.toml. Until then every package is Unknown and nothing is Safe.");
        }
        else if (resolver.ReadableProjectCount == 0)
        {
            hints.Add(
                $"Found {projects.Projects.Count} project(s), but none has an obj/project.assets.json. "
                + "Run 'dotnet restore' so coppice can tell what is actually in use.");
        }

        if (items.Count > 0 && hints.Count == 0 && resolver.UnreadableProjectCount > 0)
        {
            hints.Add(
                $"{resolver.UnreadableProjectCount} project(s) could not be read. Packages they use are "
                + "reported as Unknown, not as unused.");
        }

        return [.. hints];
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

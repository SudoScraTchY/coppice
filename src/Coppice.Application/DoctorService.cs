using System.Text.Json;

using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Manifests;
using Coppice.Parsing;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// One resolved location candidate, ready to report (FR-02).
/// <para>
/// Every candidate the resolver considered gets one of these — the winner, an additional root, an
/// inactive default, and every rejected path. <see cref="Size"/> is measured only when the candidate
/// exists, is a directory, and is safe to walk; <see cref="SizeNote"/> says why it is absent instead
/// of leaving a blank that reads like zero.
/// </para>
/// </summary>
public sealed record RootReport(
    string LocationId,
    ResolvedRoot Root,
    string? Problem,
    string? EcosystemId = null,
    string? Owner = null,
    ulong? Size = null,
    string? SizeNote = null)
{
    /// <summary>True when two live sources disagreed and no pin settled it (LR-4). Never cleanable.</summary>
    public bool Ambiguous => Root.Validity == RootValidity.Ambiguous;
}

/// <summary>The whole machine's location picture.</summary>
public sealed record DoctorReport(
    OperatingSystemKind OS,
    IReadOnlyList<RootReport> Roots,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Ecosystems)
{
    /// <summary>Every declared ecosystem's presence and the evidence for it (FR-01).</summary>
    public IReadOnlyList<EcosystemDetectionReport> Detection { get; init; } = [];

    /// <summary>True only for the report <c>roots</c> builds, which is the verb that pays for sizing.</summary>
    public bool SizesMeasured { get; init; }
}

/// <summary>
/// Builds the doctor/roots view from the real machine (T-014), across every registered ecosystem
/// (T-048).
/// <para>
/// Read-only by construction: this touches the filesystem only through <see cref="IFileSystem"/>
/// enumeration and the resolver, and runs tool queries only through <see cref="IProcessRunner"/>
/// with a neutral working directory (LR-6). It has no code path that can delete anything. Locations
/// come from <see cref="EcosystemRegistry"/>, so a manifest-backed profile is resolved through the
/// same engine, fingerprint gate and provenance reporting as the code-backed .NET one.
/// </para>
/// </summary>
public sealed class DoctorService
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly OperatingSystemKind _os;
    private readonly EcosystemRegistry _registry;
    private readonly IReadOnlyDictionary<string, string> _pins;
    private readonly TimeSpan _probeBudget;

    public DoctorService(IFileSystem fs, IProcessRunner runner, IEnvironment env)
        : this(fs, runner, env, env.OS, registry: null)
    {
    }

    public DoctorService(IFileSystem fs, IProcessRunner runner, IEnvironment env, OperatingSystemKind os)
        : this(fs, runner, env, os, registry: null)
    {
    }

    public DoctorService(IFileSystem fs, IProcessRunner runner, IEnvironment env, OperatingSystemKind os, EcosystemRegistry? registry, IReadOnlyDictionary<string, string>? pins = null, TimeSpan? probeBudget = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);

        _fs = fs;
        _runner = runner;
        _env = env;
        _os = os;
        _registry = registry ?? EcosystemRegistry.CreateDefault(fs);
        _pins = pins ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _probeBudget = probeBudget ?? TimeSpan.FromSeconds(4);
    }

    /// <summary>The machine's home directory, used only to abbreviate paths for display.</summary>
    public string? HomeDirectory => _env.HomeDirectory;

    /// <summary>Exposed so the commands can hand the SAME registry to scan and apply wiring.</summary>
    public EcosystemRegistry Registry => _registry;

    /// <summary>Exposed so ScanService can use the same filesystem and environment.</summary>
    public IFileSystem Fs => _fs;

    /// <summary>Exposed so ScanService can use the same environment.</summary>
    public IEnvironment Env => _env;

    /// <summary>Exposed so ScanService can use the same state store.</summary>
    public IStateStore State => new FileSystemStateStore(_fs, _env);

    /// <summary>
    /// The longest a single tool query may run. A probe that hangs must not hold the whole report:
    /// doctor is an interactive verb, and T-048 added up to ten tool-backed locations. Cancellation
    /// short-circuits the rest, so this is the worst one probe can add to a cancelled report.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Resolves every location every registered ecosystem declares for this OS and reports what was
    /// found. This is the read-only surface `doctor`, `roots` and `scan` share; it never mutates.
    /// </summary>
    public DoctorReport Inspect() => Inspect(CancellationToken.None);

    /// <summary>
    /// The same report, with each existing root candidate's size measured (FR-02). Sizing walks the
    /// tree, so only <c>roots</c> asks for it: <c>doctor</c> is an interactive health check and must
    /// stay inside its budget (NFR-04).
    /// </summary>
    public DoctorReport InspectWithSizes(CancellationToken cancellationToken = default) =>
        Inspect(cancellationToken, measureSizes: true);

    /// <summary>
    /// The cancellable form the GUI drives. The token is passed to every tool probe and checked
    /// between locations, so cancelling a scan does not wait for the remaining tools to answer.
    /// <para>
    /// The check after each probe is deliberate: <see cref="Coppice.Adapters.SystemProcessRunner"/> reports a
    /// cancelled child as <c>TimedOut</c> rather than throwing, so the token must be re-asserted for
    /// cancellation to be observable regardless of which runner implementation is installed.
    /// </para>
    /// </summary>
    public DoctorReport Inspect(CancellationToken cancellationToken) =>
        Inspect(cancellationToken, measureSizes: false);

    private DoctorReport Inspect(CancellationToken cancellationToken, bool measureSizes)
    {
        var roots = new List<RootReport>();
        var issues = new List<string>(_registry.ManifestErrors);

        // NFR-04: doctor is an interactive health check. Probes run concurrently in one group per
        // ecosystem, so a slow tool in one ecosystem cannot consume the budget before the others are
        // asked. The shared budget is the wall-clock cap; each tool query still has ProbeTimeout.
        // Sizing is caller-cancellable and runs only for `roots`.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_probeBudget);

        EcosystemLocation[] locations = [.. _registry.Locations.Where(l => l.AppliesTo(_os))];
        (EcosystemLocation Location, int Index)[][] groups = [.. locations
            .Select((location, index) => (Location: location, Index: index))
            .GroupBy(x => x.Location.EcosystemId)
            .Select(group => group.ToArray())];

        // Presence is a separate question and runs alongside location probing. A starved location
        // probe must not silently erase the detection answer.
        Task<IReadOnlyList<EcosystemDetectionReport>?> detectionTask = Task.Run(() =>
        {
            try
            {
                return (IReadOnlyList<EcosystemDetectionReport>?)new EcosystemDetector(_fs, _runner, _env)
                    .Detect(_registry.Ecosystems, budget.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }, cancellationToken);

        Task<GroupReport>[] groupTasks = [.. groups.Select(group => Task.Run(
            () => ProcessGroup(group, budget.Token, cancellationToken),
            cancellationToken))];

        GroupReport[] groupReports;
        try
        {
            groupReports = Task.WhenAll(groupTasks).GetAwaiter().GetResult();
        }
        catch
        {
            // A group fault is about to leave this method: cancel the shared budget and observe the
            // concurrently-running detection task so it neither hangs nor raises an unobserved
            // exception after the CTS is disposed. Do this BEFORE caller-cancellation can rethrow,
            // or a non-OCE detection fault would escape unobserved.
            budget.Cancel();
            _ = detectionTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        foreach (GroupReport group in groupReports)
        {
            roots.AddRange(group.Roots);
        }

        // Keep the issue order the sequential implementation produced: original location order.
        issues.AddRange(groupReports
            .SelectMany(group => group.Issues)
            .OrderBy(issue => issue.Index)
            .Select(issue => issue.Issue));

        roots.Sort(static (a, b) =>
        {
            int byLocation = string.CompareOrdinal(a.LocationId, b.LocationId);
            return byLocation != 0 ? byLocation : string.CompareOrdinal(a.Root.RealPath, b.Root.RealPath);
        });

        // An ecosystem is "present" when at least one of its locations actually resolved to a path.
        // A default that does not exist is reported as a root, but it is not evidence of an install.
        string[] ecosystems =
        [
            .. roots
                .Where(r => r.Root.Validity != RootValidity.NotFound)
                .Select(r => _registry.FindLocation(r.LocationId)?.Location.EcosystemId)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];

        // FR-01: presence is its own question, answered by the profile's declared probe — not by
        // whether a cache happens to exist. The registry supplies both the list and the descriptors.
        IReadOnlyList<EcosystemDetectionReport> detection;
        try
        {
            IReadOnlyList<EcosystemDetectionReport>? detected = detectionTask.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            detection = detected ?? [];
            if (detected is null)
            {
                issues.Add("[ecosystems] detection budget exhausted; presence was not answered");
            }
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            detection = [];
            issues.Add("[ecosystems] detection budget exhausted; presence was not answered");
        }

        // Sizing is deliberately OUTSIDE the probe budget. The `roots` verb measures whole caches,
        // and the 21 GB NuGet root took longer than the 4 s budget: doing it inside the groups
        // cancelled the probes still queued behind it (go-bin and npm-global vanished). Probing
        // answers "which candidates exist"; sizing answers "how large are they" and is caller-
        // cancellable. It cannot starve discovery.
        if (measureSizes)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ulong? size, string? sizeNote) = MeasureRoot(roots[i].Root, cancellationToken);
                roots[i] = roots[i] with { Size = size, SizeNote = sizeNote };
            }
        }

        return new DoctorReport(_os, roots, issues, ecosystems)
        {
            Detection = detection,
            SizesMeasured = measureSizes,
        };
    }

    /// <summary>
    /// Probes one ecosystem's locations sequentially, sharing the doctor's wall-clock budget with the
    /// other ecosystems. Grouping keeps a slow .NET tool from starving Go, Node and Rust, while root
    /// and issue ordering stays deterministic in the caller. Sizing happens after every group returns.
    /// </summary>
    private GroupReport ProcessGroup(
        (EcosystemLocation Location, int Index)[] group,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        var engine = new ResolutionEngine(_fs, _os, _env.HomeDirectory);
        var validator = new FingerprintValidator(_fs, _os);
        var roots = new List<RootReport>();
        var issues = new List<(int Index, string Issue)>();

        foreach ((EcosystemLocation location, int index) in group)
        {
            callerToken.ThrowIfCancellationRequested();

            if (budgetToken.IsCancellationRequested)
            {
                issues.Add((index, "[doctor] probe budget exhausted; the remaining locations were not probed"));
                break;
            }

            LocationSpec spec;
            try
            {
                spec = BuildSpec(location, budgetToken);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                issues.Add((index, $"[{location.Id}] probe budget exhausted; this location and the remaining probes were not resolved"));
                break;
            }

            ResolutionOutcome outcome = engine.Resolve(spec, validator.AsPredicate(FingerprintFor(location)));

            issues.AddRange(outcome.Issues.Select(i => (index, $"[{location.Id}] {i.Code}: {i.Summary}")));

            foreach (ResolvedRoot root in outcome.Roots)
            {
                roots.Add(new RootReport(
                    location.Id,
                    root,
                    FixHint(root),
                    location.EcosystemId,
                    location.Owner,
                    null,
                    null));
            }

            // A location that resolved to nothing at all still deserves a line: "no idea where
            // dotnet-root is" is more useful to a user than silence.
            if (outcome.Roots.Count == 0)
            {
                issues.Add((index, $"[{location.Id}] no candidate resolved from any source"));
            }
        }

        return new GroupReport(roots, issues);
    }

    private sealed record GroupReport(
        IReadOnlyList<RootReport> Roots,
        IReadOnlyList<(int Index, string Issue)> Issues);


    /// <summary>
    /// Measures one candidate, or explains why not (FR-02).
    /// <para>
    /// A denied root is never walked, even to size it: the denylist says "do not follow this", and a
    /// recursive read is following it. A path that is not a directory has no tree to measure. Any I/O
    /// or permission failure is a note, not an exception — one unreadable cache must not take the
    /// whole report down.
    /// </para>
    /// </summary>
    private (ulong? Size, string? Note) MeasureRoot(ResolvedRoot root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (root.Validity == RootValidity.Denied)
        {
            return (null, "not measured: a denied root is never followed");
        }

        if (!_fs.DirectoryExists(root.RealPath))
        {
            return (null, "not measured: the path is not a directory");
        }

        try
        {
            ulong size = _fs.MeasureSize(root.RealPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return (size, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"not measured: {ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()}");
        }
    }

    /// <summary>
    /// Builds a location's source chain: pin, then tool, then env, then the per-OS default. The order
    /// is the engine's, and the engine's precedence table enforces it. Manifest and code profiles
    /// both arrive here as the same descriptor.
    /// </summary>
    internal LocationSpec BuildSpec(EcosystemLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        var sources = new Dictionary<ResolvedVia, SourceResult>();
        string? pin = null;

        foreach (LocationSource source in location.Sources)
        {
            switch (source.Via)
            {
                case ResolvedVia.Pin:
                    pin = ExpandDefault(source.PathTemplate, _env);
                    break;

                case ResolvedVia.Tool:
                    if (RunToolQuery(source, cancellationToken) is { } toolPath)
                    {
                        sources[ResolvedVia.Tool] = SourceResult.Of(
                            toolPath,
                            ResolvedVia.Tool,
                            source.Run,
                            isLive: !source.Inactive);
                    }

                    break;

                case ResolvedVia.Env:
                    if (!string.IsNullOrWhiteSpace(source.Variable)
                        && _env.GetVariable(source.Variable) is { Length: > 0 } value)
                    {
                        sources[ResolvedVia.Env] = SourceResult.Of(
                            AppendRelative(value, source.PathTemplate, _os),
                            ResolvedVia.Env,
                            source.Variable,
                            isLive: !source.Inactive);
                    }

                    break;

                // Config, registry and OS-file rungs are reserved for the config workstream; a
                // descriptor that declares one is reported as "no candidate" rather than guessed at.
                default:
                    break;
            }
        }

        // A config pin chooses a path; it does not grant authority. It goes through the same resolver
        // and every denylist/fingerprint gate as a tool answer, only with the Pin rung on top.
        if (_pins.TryGetValue(location.Id, out string? configuredPin))
        {
            pin = ExpandDefault(configuredPin, _env);
        }

        // Defaults carry unexpanded placeholders (~, {home}, {user}, %VAR%). They MUST be expanded
        // before the resolver sees them: an unexpanded "%LOCALAPPDATA%\NuGet\v3-cache" is not a path,
        // and reporting it as a not-found root tells the user their cache is missing when it is not.
        var defaults = new Dictionary<OperatingSystemKind, string>();
        foreach ((OperatingSystemKind os, string raw) in location.DefaultPaths)
        {
            string? expanded = ExpandDefault(raw, _env);
            if (!string.IsNullOrEmpty(expanded))
            {
                defaults[os] = expanded;
            }
        }

        return new LocationSpec
        {
            Id = location.Id,
            Mode = location.Mode,
            OS = _os,
            Pin = pin,
            Sources = sources,
            OsDefaults = defaults,
            ConfidencePenalty = location.ConfidencePenalty,
        };
    }

    /// <summary>
    /// Expands a manifest path. <c>{home}</c> is the manifest vocabulary; the rest (<c>~</c>,
    /// <c>{user}</c>, <c>%VAR%</c>, <c>$VAR</c>) is the profile vocabulary the .NET table already uses.
    /// </summary>
    internal static string? ExpandDefault(string? raw, IEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string text = raw;

        if (text.Contains("{home}", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(environment.HomeDirectory))
            {
                return null;
            }

            text = text.Replace("{home}", environment.HomeDirectory, StringComparison.Ordinal);
        }

        return Plugins.Net.NetDefaults.Expand(text, environment);
    }

    /// <summary>
    /// Appends a relative suffix to a source value, using the TARGET OS's separator. Rust's
    /// <c>CARGO_HOME</c> names the cargo home, not the registry, so a manifest that declares
    /// <c>path = "registry"</c> means "the registry under that home".
    /// </summary>
    internal static string AppendRelative(string root, string? relative, OperatingSystemKind os)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return root;
        }

        char separator = os == OperatingSystemKind.Windows ? '\\' : '/';
        string tail = relative.Replace('\\', separator).Replace('/', separator).Trim(separator);

        return root.TrimEnd('/', '\\') + separator + tail;
    }

    /// <summary>
    /// Asks the tool where its cache is. Returns null on ANY failure — a missing tool, a non-zero
    /// exit, a timeout, or output we cannot parse — because a tool that cannot answer must not stop
    /// the scan; the next source in the chain gets its turn (E-11).
    /// <para>
    /// Cancellation is different from failure: it propagates, because the caller asked to stop and a
    /// half-resolved report is not an answer. The probe is bounded by <see cref="ProbeTimeout"/> so
    /// an unresponsive tool cannot outlive the verb's budget.
    /// </para>
    /// </summary>
    private string? RunToolQuery(LocationSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Run))
        {
            return null;
        }

        string[] parts = source.Run.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ProcessResult result = _runner.RunAsync(
                new ProcessRequest
                {
                    FileName = parts[0],
                    Arguments = [.. parts.Skip(1)],
                    // Neutral cwd: a stray nuget.config in the current project must not skew a
                    // machine-level answer (LR-6).
                    WorkingDirectory = null,
                    Timeout = ProbeTimeout,
                },
                cancellationToken).GetAwaiter().GetResult();

            // SystemProcessRunner turns a cancelled child into a TimedOut result instead of
            // throwing, so cancellation has to be re-asserted here to be observable.
            cancellationToken.ThrowIfCancellationRequested();

            if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            string? value = ParseToolOutput(result.StandardOutput, source);
            if (value is null)
            {
                return null;
            }

            if (source.PathIsSdkInstall)
            {
                // Every SDK lives under <root>/sdk/<version>; the root is the parent.
                int lastSeparator = value.LastIndexOfAny(['/', '\\']);
                value = lastSeparator > 0 ? value[..lastSeparator] : value;
            }

            return AppendRelative(value, source.PathTemplate, _os);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one value out of a tool's output with the manifest's declared parser.
    /// <para>
    /// `label-value` falls back to bare lines because `go env GOMODCACHE` prints the value with no
    /// label in real Go, while the recorded/fixture form does include one. Both are the same answer;
    /// treating the bare form as unparseable would silently discard the authoritative source.
    /// </para>
    /// </summary>
    private static string? ParseToolOutput(string output, LocationSource source)
    {
        ParseResult parsed = source.Parse switch
        {
            ManifestParserKind.Line => OutputParsers.ParseLines(output),
            ManifestParserKind.SdkBracketPaths => OutputParsers.ParseBracketPaths(output),
            ManifestParserKind.JsonPath when source.Label is { Length: > 0 } => OutputParsers.ParseJsonPath(output, source.Label),
            ManifestParserKind.LabelValue when source.Label is { Length: > 0 } => OutputParsers.ParseLabelValueFor(output, source.Label),
            ManifestParserKind.LabelValue => OutputParsers.ParseLabelValue(output),
            _ => ParseResult.Empty,
        };

        if (parsed.AbsoluteValues.Count == 0 && source.Parse == ManifestParserKind.LabelValue)
        {
            // Bare value form, e.g. `go env GOMODCACHE` -> `/home/u/go/pkg/mod`.
            parsed = OutputParsers.ParseLines(output);
        }

        return parsed.AbsoluteValues.FirstOrDefault();
    }

    internal static Fingerprint FingerprintFor(EcosystemLocation location)
    {
        // A location whose profile declares no layout check gets none. Passing a 0 ratio with no
        // patterns would make every entry "not matching" and reject a perfectly good cache.
        if (location.Fingerprint.RequiredPaths.Count == 0
            && location.Fingerprint.LayoutRatio <= 0
            && location.Fingerprint.EntryPatterns.Count == 0)
        {
            return new Fingerprint { LocationId = location.Id, MinimumMatchRatio = 0 };
        }

        return new Fingerprint
        {
            LocationId = location.Id,
            RequiredPaths = location.Fingerprint.RequiredPaths,
            EntryPatterns = location.Fingerprint.EntryPatterns,
            MinimumMatchRatio = location.Fingerprint.LayoutRatio,
        };
    }

    // ---- rendering ----

    /// <summary>
    /// The concrete fix for a root that will not be cleaned. This travels with the report because it
    /// is the answer to "why not?", not a table decoration; both the CLI and a GUI screen show it.
    /// </summary>
    private static string FixHint(ResolvedRoot root) => root.Validity switch
    {
        RootValidity.Denied =>
            "this path is on the safety denylist; it will never be cleaned. If it is genuinely a "
            + "toolchain cache, move it out of the protected location and pin it with --root.",

        RootValidity.FailsFingerprint =>
            "this path exists but does not look like a cache for this ecosystem, so it will not be "
            + "cleaned. Check whether an environment variable points at the wrong place.",

        RootValidity.Ambiguous =>
            "two sources disagree about which copy is in use. Pin the correct one with "
            + "--root <location-id>=<path> to resolve this.",

        RootValidity.NotFound =>
            "this path does not exist. Nothing to do; it may be a default for a tool you do not have.",

        RootValidity.NeedsElevation =>
            "this path needs elevation to modify. Install it per-user or leave it alone.",

        _ => string.Empty,
    };

    /// <summary>
    /// Machine-readable output. Paths are REAL here, never shortened or abbreviated. Every candidate
    /// appears — including the ones that will not be cleaned — because a report that showed only the
    /// winner could not answer "why is my cache not listed?" (FR-02).
    /// </summary>
    public static string ToJson(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return JsonSerializer.Serialize(
            new
            {
                schema = 1,
                os = report.OS.ToString(),
                sizesMeasured = report.SizesMeasured,
                ecosystems = report.Ecosystems,
                detection = report.Detection.Select(d => new
                {
                    ecosystem = d.EcosystemId,
                    displayName = d.DisplayName,
                    present = d.Present,
                    source = d.Source,
                    detail = d.Detail,
                }),
                roots = report.Roots.Select(r => new
                {
                    locationId = r.LocationId,
                    ecosystem = r.EcosystemId,
                    owner = r.Owner,
                    path = r.Root.RealPath,
                    declaredPath = r.Root.DeclaredPath,
                    role = r.Root.Role.ToString(),
                    validity = r.Root.Validity.ToString(),
                    ambiguous = r.Ambiguous,
                    via = r.Root.Via.ToString(),
                    viaDetail = r.Root.ViaDetail,
                    cleanable = r.Root.IsCleanable,
                    sizeBytes = r.Size,
                    sizeNote = r.SizeNote,
                    problem = r.Problem,
                }),
                issues = report.Issues,
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
    }
}

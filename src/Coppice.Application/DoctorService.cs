using System.Text.Json;
using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Core.Scanning;
using Coppice.Plugins.Net;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>One resolved location, ready to report (FR-02).</summary>
public sealed record RootReport(string LocationId, ResolvedRoot Root, string? Problem);

/// <summary>The whole machine's location picture.</summary>
public sealed record DoctorReport(
    OperatingSystemKind OS,
    IReadOnlyList<RootReport> Roots,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Ecosystems);

/// <summary>
/// Builds the doctor/roots view from the real machine (T-014).
/// <para>
/// Read-only by construction: this touches the filesystem only through <see cref="IFileSystem"/>
/// enumeration and the resolver, and runs tool queries only through <see cref="IProcessRunner"/>
/// with a neutral working directory (LR-6). It has no code path that can delete anything.
/// </para>
/// </summary>
public sealed class DoctorService
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly OperatingSystemKind _os;

    public DoctorService(IFileSystem fs, IProcessRunner runner, IEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);

        _fs = fs;
        _runner = runner;
        _env = env;
        _os = env.OS;
    }

    public DoctorService(IFileSystem fs, IProcessRunner runner, IEnvironment env, OperatingSystemKind os)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);

        _fs = fs;
        _runner = runner;
        _env = env;
        _os = os;
    }

    /// <summary>The machine's home directory, used only to abbreviate paths for display.</summary>
    public string? HomeDirectory => _env.HomeDirectory;

    /// <summary>Exposed so ScanService can use the same filesystem and environment.</summary>
    public IFileSystem Fs => _fs;

    /// <summary>Exposed so ScanService can use the same environment.</summary>
    public IEnvironment Env => _env;

    /// <summary>Exposed so ScanService can use the same state store.</summary>
    public IStateStore State => new FileSystemStateStore(_fs, _env);

    /// <summary>
    /// Resolves every .NET location the profile declares and reports what was found. This is the
    /// whole of v0.1's read-only surface; it never mutates.
    /// </summary>
    public DoctorReport Inspect()
    {
        var engine = new ResolutionEngine(_fs, _os, _env.HomeDirectory);
        var validator = new FingerprintValidator(_fs, _os);

        var roots = new List<RootReport>();
        var issues = new List<string>();

        foreach (NetLocation location in NetProfile.ForOperatingSystem(_os))
        {
            LocationSpec spec = BuildSpec(location);
            ResolutionOutcome outcome = engine.Resolve(spec, validator.AsPredicate(FingerprintFor(location)));

            issues.AddRange(outcome.Issues.Select(i => $"[{location.Id}] {i.Code}: {i.Summary}"));

            foreach (ResolvedRoot root in outcome.Roots)
            {
                roots.Add(new RootReport(location.Id, root, FixHint(root)));
            }

            // A location that resolved to nothing at all still deserves a line: "no idea where
            // dotnet-root is" is more useful to a user than silence.
            if (outcome.Roots.Count == 0)
            {
                issues.Add($"[{location.Id}] no candidate resolved from any source");
            }
        }

        roots.Sort(static (a, b) =>
        {
            int byLocation = string.CompareOrdinal(a.LocationId, b.LocationId);
            return byLocation != 0 ? byLocation : string.CompareOrdinal(a.Root.RealPath, b.Root.RealPath);
        });

        return new DoctorReport(_os, roots, issues, [NetProfile.EcosystemId]);
    }

    /// <summary>
    /// Builds a location's source chain: pin, then tool, then env, then the per-OS default. The
    /// order is the profile's, and the engine's precedence table enforces it.
    /// </summary>
    internal LocationSpec BuildSpec(NetLocation location)
    {
        var sources = new Dictionary<ResolvedVia, SourceResult>();

        string? toolPath = RunToolQuery(location);
        if (toolPath is not null)
        {
            sources[ResolvedVia.Tool] = SourceResult.Of(toolPath, ResolvedVia.Tool, location.ToolQuery);
        }

        foreach (string variable in location.EnvVariables)
        {
            string? value = _env.GetVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                sources[ResolvedVia.Env] = SourceResult.Of(value, ResolvedVia.Env, variable);
                break;
            }
        }

        // Config and registry sources arrive in v0.2 with the manifest loader; the chain order is
        // already fixed in LocationSpec.PrecedenceOrder, so adding them is additive.
        // Defaults carry unexpanded placeholders (~, {user}, %VAR%). They MUST be expanded before
        // the resolver sees them: an unexpanded "%LOCALAPPDATA%\NuGet\v3-cache" is not a path, and
        // reporting it as a not-found root tells the user their cache is missing when it is not.
        var defaults = new Dictionary<OperatingSystemKind, string>();
        foreach ((OperatingSystemKind os, string raw) in location.OsDefaults)
        {
            string? expanded = NetDefaults.Expand(raw, _env);
            if (!string.IsNullOrEmpty(expanded))
            {
                defaults[os] = expanded;
            }
        }

        return new LocationSpec
        {
            Id = location.Id,
            Mode = location.ResolveAll ? ResolveMode.All : ResolveMode.First,
            OS = _os,
            Sources = sources,
            OsDefaults = defaults,
        };
    }

    /// <summary>
    /// Asks the tool where its cache is. Returns null on ANY failure — a missing tool, a non-zero
    /// exit, a timeout, or output we cannot parse — because a tool that cannot answer must not stop
    /// the scan; the next source in the chain gets its turn (E-11).
    /// </summary>
    private string? RunToolQuery(NetLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.ToolQuery))
        {
            return null;
        }

        string[] parts = location.ToolQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        try
        {
            ProcessResult result = _runner.RunAsync(new ProcessRequest
            {
                FileName = parts[0],
                Arguments = [.. parts.Skip(1)],
                // Neutral cwd: a stray nuget.config in the current project must not skew a
                // machine-level answer (LR-6).
                WorkingDirectory = null,
                Timeout = TimeSpan.FromSeconds(10),
            }).GetAwaiter().GetResult();

            if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            return ExtractPath(location, result.StandardOutput);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the answer out of the tool's output. `nuget locals` prints several labels and we want
    /// the one for this location; `--list-sdks` prints bracketed paths and we want the root they
    /// share, because a location is a ROOT, not one SDK's folder.
    /// </summary>
    private static string? ExtractPath(NetLocation location, string output)
    {
        IReadOnlyList<LabeledPath> labeled = DotnetOutputParser.ParseLabelValuePaths(output);
        if (labeled.Count > 0)
        {
            // `nuget-packages` maps to the `global-packages` label; the id and the label differ.
            string label = location.Id switch
            {
                "nuget-packages" => "global-packages",
                _ => location.Id,
            };

            LabeledPath? match = labeled.FirstOrDefault(p => string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase));
            return match?.Path;
        }

        IReadOnlyList<SdkInfo> sdks = DotnetOutputParser.ParseSdks(output);
        if (sdks.Count > 0)
        {
            // Every SDK lives under <root>/sdk/<version>; the root is the grandparent.
            string sdkFolder = sdks[0].InstallPath;
            int lastSeparator = sdkFolder.LastIndexOfAny(['/', '\\']);
            return lastSeparator > 0 ? sdkFolder[..lastSeparator] : sdkFolder;
        }

        return null;
    }

    internal static Fingerprint FingerprintFor(NetLocation location)
    {
        // A location whose profile declares no layout check gets none. Passing a 0 ratio with no
        // patterns would make every entry "not matching" and reject a perfectly good cache.
        if (location.Fingerprint.IsEmpty)
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

    /// <summary>Machine-readable output. Paths are REAL here, never shortened or abbreviated.</summary>
    public static string ToJson(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return JsonSerializer.Serialize(
            new
            {
                schema = 1,
                os = report.OS.ToString(),
                ecosystems = report.Ecosystems,
                roots = report.Roots.Select(r => new
                {
                    locationId = r.LocationId,
                    path = r.Root.RealPath,
                    declaredPath = r.Root.DeclaredPath,
                    role = r.Root.Role.ToString(),
                    validity = r.Root.Validity.ToString(),
                    via = r.Root.Via.ToString(),
                    viaDetail = r.Root.ViaDetail,
                    cleanable = r.Root.IsCleanable,
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

using Coppice.Ports;
using EntryKind = Coppice.Ports.EntryKind;
using FileEntry = Coppice.Ports.FileEntry;
using OperatingSystemKind = Coppice.Ports.OperatingSystemKind;

namespace Coppice.Plugins.Net;

/// <summary>
/// The .NET health checks from the 09-ecosystems "Health checks" table (T-013, re-homed for T-016).
/// <para>
/// Read-only by construction: every read goes through <see cref="Ports.IFileSystem"/> and the only
/// two questions the SDK needs answered come through <see cref="Ports.IProcessRunner"/>. Nothing
/// here writes, moves or deletes, which is what C-9 asserts against the fake VFS.
/// </para>
/// <para>
/// A check reports only what it can PROVE. When a prerequisite is missing — no dotnet-root was
/// scanned, the CLI could not be run, a layout directory does not exist — the check returns nothing
/// rather than guessing. A health report full of speculation is worse than a short one, because the
/// user cannot tell which lines are facts.
/// </para>
/// </summary>
public sealed class NetHealthChecker
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    private readonly Ports.ScanContext _ctx;

    public NetHealthChecker(Ports.ScanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
    }

    private Ports.IFileSystem Fs => _ctx.FileSystem;

    private Ports.IEnvironment Env => _ctx.Environment;

    /// <summary>
    /// Path join that never touches System.IO: the port speaks in strings (NFR-08). The separator
    /// comes from the OS the scan context reports, because a plugin holding a Windows path on a
    /// Linux host must join it the way Windows would or every lookup misses.
    /// </summary>
    private char Separator => _ctx.Environment.OS == OperatingSystemKind.Windows ? '\\' : '/';

    private string Join(string a, string b) => a.TrimEnd('/', '\\') + Separator + b;

    private string Join(string a, string b, string c) => Join(Join(a, b), c);

    /// <summary>
    /// Runs every check and returns problems, most severe first (FR-10).
    /// <para>
    /// Ordering is total and content-derived — severity, then code, then path — so two scans of the
    /// same machine produce byte-identical reports (NFR-06). A health list that reshuffles between
    /// runs reads as noise and trains people to ignore it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Ports.Problem>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<Ports.Problem>();

        HashSet<string> knownSdks = await KnownSdkVersionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> onDiskSdks = SdkVersionsOnDisk();

        problems.AddRange(CheckDotnetRootLayout());
        problems.AddRange(CheckDotnetRootEnvironment());
        problems.AddRange(CheckPathEntries());
        problems.AddRange(CheckOrphanedSdks(onDiskSdks, knownSdks));
        problems.AddRange(CheckSdkInToolsLocation());
        problems.AddRange(CheckDuplicateRoots());
        problems.AddRange(CheckPreviewSuperseded(onDiskSdks));
        problems.AddRange(CheckSupersededMajorSdks(onDiskSdks));
        problems.AddRange(CheckPinnedSdkAvailable(onDiskSdks));
        problems.AddRange(await CheckUnregisteredToolShimsAsync(cancellationToken).ConfigureAwait(false));

        problems.Sort(static (a, b) =>
        {
            int bySeverity = b.Severity.CompareTo(a.Severity);
            return bySeverity != 0
                ? bySeverity
                : string.CompareOrdinal(a.Code, b.Code) is int byCode && byCode != 0
                    ? byCode
                    : string.CompareOrdinal(a.Path ?? string.Empty, b.Path ?? string.Empty);
        });

        return problems;
    }

    private IEnumerable<Ports.ScanRoot> RootsFor(string locationId) =>
        _ctx.Roots.Where(r => r.LocationId == locationId && r.Role != Ports.RootRole.Inactive);

    private IEnumerable<string> ResolvedPathsFor(string locationId) =>
        RootsFor(locationId).Select(r => r.ResolvedPath).Distinct(StringComparer.OrdinalIgnoreCase);

    // ---- 1. A dotnet-root that resolved "ok" but has none of the layout directories ----

    /// <summary>
    /// A dotnet-root with neither <c>sdk/</c>, <c>host/</c> nor <c>host/fxr/</c> is not an install.
    /// All three must be MISSING for this to fire: an SDK-only side-by-side folder or a runtime-only
    /// one is unusual but real, and flagging it would be a false alarm.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckDotnetRootLayout()
    {
        foreach (string root in ResolvedPathsFor("dotnet-root"))
        {
            bool hasSdk = Fs.DirectoryExists(Join(root, "sdk"));
            bool hasHost = Fs.DirectoryExists(Join(root, "host"));
            bool hasFxr = Fs.DirectoryExists(Join(Join(root, "host"), "fxr"));

            if (hasSdk || hasHost || hasFxr)
            {
                continue;
            }

            yield return Problem(
                "DOTNET_ROOT_MISSING_LAYOUT",
                Ports.Severity.Warning,
                root,
                $"dotnet-root has none of sdk/, host/ or host/fxr/. It is not a usable .NET install; builds pointed at it will fail.",
                "dotnet-root");
        }
    }

    // ---- 2. DOTNET_ROOT pointing somewhere that is not a valid install ----

    /// <summary>
    /// <c>DOTNET_ROOT</c> is authoritative, so a bad value breaks every build that honours it. The
    /// check is an Error, not a Warning: this one does not merely waste space, it fails work.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckDotnetRootEnvironment()
    {
        string? dotnetRoot = Env.GetVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            yield break;
        }

        bool matchedAKnownRoot = ResolvedPathsFor("dotnet-root")
            .Contains(dotnetRoot, StringComparer.OrdinalIgnoreCase);

        if (matchedAKnownRoot)
        {
            yield break;
        }

        yield return Problem(
            "DOTNET_ROOT_INVALID",
            Ports.Severity.Error,
            dotnetRoot,
            $"DOTNET_ROOT points at '{dotnetRoot}', which is not a resolved .NET location. Builds that honour it will fail.",
            "dotnet-root");
    }

    // ---- 3. PATH entries that claim to be dotnet but hold no executable ----

    private IEnumerable<Ports.Problem> CheckPathEntries()
    {
        foreach (string entry in Env.PathEntries)
        {
            if (!entry.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Fs.FileExists(Join(entry, "dotnet.exe")) || Fs.FileExists(Join(entry, "dotnet")))
            {
                continue;
            }

            yield return Problem(
                "DOTNET_PATH_MISSING",
                Ports.Severity.Warning,
                entry,
                $"PATH entry '{entry}' names dotnet but contains no dotnet executable. Every shell start pays for it and tooling can pick the wrong one.",
                "dotnet-root");
        }
    }

    // ---- 4. SDK folders on disk that the CLI does not know about ----

    /// <summary>
    /// Compares <c>&lt;root&gt;/sdk/*</c> against <c>dotnet --list-sdks</c>. A folder the CLI cannot
    /// see is unreachable by every tool, so nothing can select it and nothing can keep it updated —
    /// it is pure residue. Only reported when the CLI answered, so an unavailable dotnet never
    /// produces a wall of false orphans.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckOrphanedSdks(IReadOnlyList<string> onDisk, IReadOnlySet<string> known)
    {
        if (known.Count == 0)
        {
            yield break;
        }

        foreach (string version in onDisk.Where(v => !known.Contains(v)))
        {
            yield return Problem(
                "SDK_ORPHAN",
                Ports.Severity.Warning,
                null,
                $"SDK {version} exists on disk but 'dotnet --list-sdks' does not report it. It is unreachable by any tool and no longer updated.",
                "dotnet-sdk",
                new Dictionary<string, string> { ["version"] = version });
        }
    }

    // ---- 5. An SDK installed where a tool belongs ----

    /// <summary>
    /// <c>dotnet-tools</c> holds tool shims. An <c>sdk/</c> directory there is a mis-install: it can
    /// never be executed as a tool, and its size is attributed to a location the user believes holds
    /// only shims.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckSdkInToolsLocation()
    {
        foreach (string root in ResolvedPathsFor("dotnet-tools"))
        {
            string sdkDir = Join(root, "sdk");
            if (!Fs.DirectoryExists(sdkDir))
            {
                continue;
            }

            foreach (FileEntry entry in Directories(sdkDir))
            {
                yield return Problem(
                    "SDK_IN_TOOLS_LOCATION",
                    Ports.Severity.Warning,
                    entry.Path,
                    $"'{entry.Name}' sits under the dotnet-tools location, which holds tool shims. An SDK here can never run and is not counted by 'dotnet --list-sdks'.",
                    "dotnet-tools",
                    new Dictionary<string, string> { ["name"] = entry.Name });
            }
        }
    }

    // ---- 6. The same root reached through two sources ----

    /// <summary>
    /// The same physical dotnet-root reached by two sources (say <c>DOTNET_ROOT</c> and the OS
    /// default) wastes nothing but confuses provenance reporting. Reported only when the two
    /// resolved paths differ textually yet point at the same real directory, which is what makes it
    /// a duplicate rather than a second install.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckDuplicateRoots()
    {
        foreach (string locationId in NetEcosystemLocations.All)
        {
            var byRealPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (Ports.ScanRoot root in RootsFor(locationId))
            {
                string real = Fs.ResolveLinkTarget(root.ResolvedPath);
                if (!byRealPath.TryGetValue(real, out List<string>? declared))
                {
                    declared = [];
                    byRealPath[real] = declared;
                }

                declared.Add(root.ResolvedPath);
            }

            foreach ((string real, List<string> declared) in byRealPath.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (declared.Count < 2)
                {
                    continue;
                }

                yield return Problem(
                    "ROOT_DUPLICATE",
                    Ports.Severity.Info,
                    real,
                    $"{locationId} resolved to {declared.Count} paths that are the same directory ({string.Join(", ", declared)}). Harmless, but the report will list the same space more than once.",
                    locationId,
                    new Dictionary<string, string> { ["paths"] = string.Join(";", declared) });
            }
        }
    }

    // ---- 7. A preview superseded by the GA release of the same feature band ----

    /// <summary>
    /// A preview is superseded when a GA release exists in the SAME feature band (8.0.4xx) and is
    /// newer. Band containment is what makes this safe to say: 9.0's GA does not supersede 8.0's
    /// preview, because the preview is what builds targeting net8.0-preview still need.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckPreviewSuperseded(IReadOnlyList<string> versions)
    {
        var byBand = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string version in versions)
        {
            string band = SdkVersion.BandOf(version);
            if (!byBand.TryGetValue(band, out List<string>? list))
            {
                list = [];
                byBand[band] = list;
            }

            list.Add(version);
        }

        foreach ((string band, List<string> inBand) in byBand.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? newestGa = inBand
                .Where(v => !SdkVersion.IsPreview(v))
                .OrderByDescending(v => v, Comparer<string>.Create(SdkVersion.Compare))
                .FirstOrDefault();

            if (newestGa is null)
            {
                continue;
            }

            foreach (string preview in inBand.Where(SdkVersion.IsPreview).Order(StringComparer.Ordinal))
            {
                yield return Problem(
                    "SDK_PREVIEW_SUPERSEDED",
                    Ports.Severity.Warning,
                    null,
                    $"Preview SDK {preview} is superseded by GA {newestGa} in the same feature band ({band}).",
                    "dotnet-sdk",
                    new Dictionary<string, string> { ["previewVersion"] = preview, ["gaVersion"] = newestGa, ["band"] = band });
            }
        }
    }

    // ---- 8. An older major SDK superseded by a newer one ----

    /// <summary>
    /// A Warning, not Info: an older major is the single largest reclaimable block on a .NET
    /// machine, and 06 says Unknown ≠ safe-to-delete — but "you may be able to remove this" is a
    /// genuinely useful thing to surface.
    /// </summary>
    private IEnumerable<Ports.Problem> CheckSupersededMajorSdks(IReadOnlyList<string> versions)
    {
        List<string> ordered = [.. versions.OrderByDescending(v => v, Comparer<string>.Create(SdkVersion.Compare))];
        if (ordered.Count < 2)
        {
            yield break;
        }

        string newest = ordered[0];

        foreach (string older in ordered.Skip(1))
        {
            if (!IsOlderMajor(older, newest))
            {
                continue;
            }

            yield return Problem(
                "SDK_SUPERSEDED_MAJOR",
                Ports.Severity.Warning,
                null,
                $"SDK {older} is superseded by {newest}. Removing it frees space but will break anything that pins or targets the older major.",
                "dotnet-sdk",
                new Dictionary<string, string> { ["oldVersion"] = older, ["newVersion"] = newest });
        }
    }

    private static bool IsOlderMajor(string candidate, string baseline) =>
        int.TryParse(SdkVersion.MajorOf(candidate), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int candidateMajor)
        && int.TryParse(SdkVersion.MajorOf(baseline), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int baselineMajor)
        && baselineMajor > candidateMajor;

    // ---- 9. A global.json pin no installed SDK satisfies ----

    /// <summary>
    /// A pin to a version that is not installed is a hard build failure, so this is an Error.
    /// <para>
    /// The pin protects the named version and its <c>rollForward</c> band only. A 10.x pin does not
    /// make a missing 9.x SDK acceptable, and does not make an installed 9.x SDK a problem either —
    /// which is why the test is "is the pinned version installed", not "is anything newer installed".
    /// </para>
    /// </summary>
    private IEnumerable<Ports.Problem> CheckPinnedSdkAvailable(IReadOnlyList<string> onDisk)
    {
        var installed = new HashSet<string>(onDisk, StringComparer.OrdinalIgnoreCase);

        foreach (string globalJson in CandidateGlobalJsons())
        {
            if (!Fs.FileExists(globalJson))
            {
                continue;
            }

            string? pinned = ReadPinnedSdkVersion(globalJson);
            if (pinned is null || installed.Contains(pinned))
            {
                continue;
            }

            yield return Problem(
                "SDK_PIN_UNAVAILABLE",
                Ports.Severity.Error,
                globalJson,
                $"global.json pins SDK {pinned}, which is not installed in any resolved dotnet-root. Builds in this directory will fail.",
                "dotnet-root",
                new Dictionary<string, string> { ["pinnedVersion"] = pinned, ["globalJson"] = globalJson });
        }
    }

    /// <summary>
    /// Where a pin can be. The scan has no project list here, so this looks at the home directory and
    /// the conventional project directory beside it — the places a pin is left behind when a
    /// repository is closed. A pin inside an unrelated repository is that repository's business.
    /// </summary>
    private IEnumerable<string> CandidateGlobalJsons()
    {
        string? home = Env.HomeDirectory;
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        yield return Join(home, "global.json");
        yield return Join(Join(home, "project"), "global.json");
    }

    /// <summary>Reads the sdk.version string out of a global.json without a JSON parser.</summary>
    /// <remarks>
    /// Hand-parsed because the plugin may not take a dependency (NFR-08), and because a malformed
    /// global.json must degrade to "no pin found" rather than throw and lose every other check.
    /// </remarks>
    private string? ReadPinnedSdkVersion(string path)
    {
        string content;
        try
        {
            content = Fs.ReadSmallText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int sdk = content.IndexOf("\"sdk\"", StringComparison.Ordinal);
        if (sdk < 0)
        {
            return null;
        }

        int versionKey = content.IndexOf("\"version\"", sdk, StringComparison.Ordinal);
        if (versionKey < 0)
        {
            return null;
        }

        int colon = content.IndexOf(':', versionKey);
        if (colon < 0)
        {
            return null;
        }

        int open = content.IndexOf('"', colon);
        if (open < 0)
        {
            return null;
        }

        int close = content.IndexOf('"', open + 1);
        return close > open ? content[(open + 1)..close] : null;
    }

    // ---- 10. A tool directory holding something the CLI never registered ----

    /// <summary>
    /// Compares the tool directory against <c>dotnet tool list -g</c>. A directory in one and not
    /// the other is a shim left behind by an uninstall that did not complete, or by a hand-copy —
    /// either way it is dead weight that <c>dotnet tool uninstall</c> will refuse to remove.
    /// <para>
    /// Skipped entirely when the command fails: an unreachable CLI proves nothing about the
    /// directory, and reporting every tool as unregistered would be a wall of false alarms.
    /// </para>
    /// </summary>
    private async Task<List<Ports.Problem>> CheckUnregisteredToolShimsAsync(CancellationToken cancellationToken)
    {
        var problems = new List<Ports.Problem>();
        var toolPaths = ResolvedPathsFor("dotnet-tools").ToList();
        if (toolPaths.Count == 0)
        {
            return problems;
        }

        Ports.ProcessResult result = await _ctx.ProcessRunner.RunAsync(
            new Ports.ProcessRequest
            {
                FileName = "dotnet",
                Arguments = ["tool", "list", "-g"],
                WorkingDirectory = Env.HomeDirectory,
                Timeout = ToolTimeout,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            return problems;
        }

        HashSet<string> registered = RegisteredToolIds(result.StandardOutput);

        foreach (string path in toolPaths)
        {
            foreach (FileEntry entry in Directories(path).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                // '.store' is the NuGet package store the SDK creates *inside* the tool directory. It
                // is never a tool and never appears in `dotnet tool list -g`, so flagging it would
                // put a permanent false positive in every report. A leading dot is how NuGet marks
                // its own bookkeeping, so it is skipped here exactly as discovery skips it.
                if (entry.Name.StartsWith('.'))
                {
                    continue;
                }

                if (registered.Contains(entry.Name))
                {
                    continue;
                }

                problems.Add(Problem(
                    "DOTNET_TOOL_BROKEN_SHIM",
                    Ports.Severity.Warning,
                    entry.Path,
                    $"'{entry.Name}' is in the tool directory but 'dotnet tool list -g' does not list it. The shim is dead: it will not run and uninstall will not remove it.",
                    "dotnet-tools",
                    new Dictionary<string, string> { ["toolId"] = entry.Name }));
            }
        }

        return problems;
    }

    /// <summary>Parses the tool-id column of <c>dotnet tool list -g</c>, skipping the header.</summary>
    private static HashSet<string> RegisteredToolIds(string stdout)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0
                || trimmed.StartsWith("Package Id", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith('-'))
            {
                continue;
            }

            string[] columns = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > 0)
            {
                ids.Add(columns[0]);
            }
        }

        return ids;
    }

    // ---- shared reads ----

    /// <summary>SDK versions found under every resolved dotnet-root's <c>sdk/</c> directory.</summary>
    private IReadOnlyList<string> SdkVersionsOnDisk()
    {
        var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in ResolvedPathsFor("dotnet-root"))
        {
            foreach (FileEntry entry in Directories(Join(root, "sdk")))
            {
                versions.Add(entry.Name);
            }
        }

        return [.. versions];
    }

    /// <summary>Every SDK the CLI reports, as bare versions (the bracketed path is dropped).</summary>
    private async Task<HashSet<string>> KnownSdkVersionsAsync(CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!ResolvedPathsFor("dotnet-root").Any())
        {
            return known;
        }

        Ports.ProcessResult result = await _ctx.ProcessRunner.RunAsync(
            new Ports.ProcessRequest
            {
                FileName = "dotnet",
                Arguments = ["--list-sdks"],
                WorkingDirectory = Env.HomeDirectory,
                Timeout = ToolTimeout,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            return known;
        }

        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "8.0.400 [/usr/share/dotnet/sdk]" — the path is informational and machine-specific.
            int space = line.Trim().IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                known.Add(line.Trim()[..space]);
            }
        }

        return known;
    }

    private IEnumerable<FileEntry> Directories(string path) =>
        Fs.EnumerateEntries(path, new Ports.EnumerationRequest { MaxDepth = 1 })
            .Where(e => e.Kind == Ports.EntryKind.Directory);

    /// <summary>
    /// Facts values are primitives by contract (C-12), and a null dictionary is common enough on a
    /// problem with no extra context that it gets a helper rather than five inline null checks.
    /// </summary>
    private static Ports.Problem Problem(
        string code,
        Ports.Severity severity,
        string? path,
        string summary,
        string? locationId = null,
        IReadOnlyDictionary<string, string>? data = null) =>
        new(code, severity, path ?? string.Empty, summary)
        {
            LocationId = locationId,
            Data = data ?? new Dictionary<string, string>(),
        };
}

using Coppice.Adapters;
using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Plugins.Net;
using Coppice.Ports;
using CoreScanRoot = Coppice.Core.Scanning.ScanRoot;
using Net = Coppice.Plugins.Net;

namespace Coppice.Cli;

/// <summary>
/// The CLI entry point (07).
/// <para>
/// Argument parsing is hand-rolled and tiny on purpose: v0.1 needs a handful of flags, and a parser
/// dependency would be the largest thing in the tool. Every verb except <c>apply</c> dispatches to a
/// service that cannot mutate anything, so <c>doctor</c>, <c>roots</c> and <c>scan</c> are safe by
/// construction rather than by promise; <c>apply</c> mutates through the gateway alone, behind a
/// confirmation and a write-ahead journal (T-034).
/// </para>
/// <para>
/// Exit codes follow 07 exactly: 0 success, 1 runtime error, 2 usage error, 3 safety abort, 4 partial
/// apply.
/// </para>
/// </summary>
public static class CoppiceApp
{
    private const int ExitSuccess = ExitCodes.Success;
    private const int ExitRuntimeError = ExitCodes.RuntimeError;
    private const int ExitUsageError = ExitCodes.UsageError;
    private const int ExitSafetyAbort = ExitCodes.SafetyAbort;

    /// <summary>
    /// The command surface, as a table rather than a switch: the read-only audit reads these keys
    /// reflectively, so what the binary can be ASKED to do and what the audit pins are the same list.
    /// <c>apply</c> is the one mutating verb (S-1) and is here deliberately; everything else only reads.
    /// </summary>
    public static IReadOnlyDictionary<string, Func<string[], int>> Verbs { get; } =
        new Dictionary<string, Func<string[], int>>(StringComparer.Ordinal)
        {
            ["doctor"] = RunDoctor,
            ["roots"] = RunRoots,
            ["scan"] = RunScan,
            ["apply"] = RunApply,
            ["audit"] = RunAudit,
        };

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            string command = args.Length > 0 ? args[0] : "help";

            if (command is "version" or "--version")
            {
                return PrintVersion();
            }

            if (command is "help" or "--help" or "-h")
            {
                return PrintHelp();
            }

            if (Verbs.TryGetValue(command, out Func<string[], int>? verb))
            {
                return verb(args.AsSpan(1).ToArray());
            }

            return command is "plan" or "clean" or "report"
                ? NotYetAvailable(command)
                : Usage($"unknown command '{command}'");
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"coppice: {ex.Message}");
            return ExitRuntimeError;
        }
    }

    private static int RunApply(string[] args) =>
        new ApplyCommand(
            new PhysicalFileSystem(),
            new SystemProcessRunner(),
            new SystemEnvironment(),
            new FileSystemStateStore(new PhysicalFileSystem(), new SystemEnvironment()),
            new FileAppendLog(new SystemEnvironment()),
            new SystemApplyConsole())
            .Run(args);

    private static int RunAudit(string[] args) =>
        new AuditCommand(
            new PhysicalFileSystem(),
            new FileSystemStateStore(new PhysicalFileSystem(), new SystemEnvironment()),
            new FileAppendLog(new SystemEnvironment()),
            new SystemApplyConsole())
            .Run(args);

    private static int RunDoctor(string[] args) => RunDoctor(CommandFlags.Parse(args));

    private static int RunRoots(string[] args) => RunRoots(CommandFlags.Parse(args));

    private static int RunScan(string[] args) => RunScan(CommandFlags.Parse(args));

    private static int RunDoctor(CommandFlags flags)
    {
        var service = BuildService();
        DoctorReport report = service.Inspect();

        if (flags.Json)
        {
            Console.Out.WriteLine(DoctorService.ToJson(report));
        }
        else
        {
            Console.Out.Write(DoctorService.ToText(report, service.HomeDirectory));
        }

        // A location that resolved but cannot be cleaned is not a failure — it is a report. Only an
        // AMBIGUOUS root is a safety abort, because the tool genuinely does not know what to do.
        bool ambiguous = report.Roots.Any(r => r.Root.Validity == Core.Domain.RootValidity.Ambiguous);
        return ambiguous ? ExitSafetyAbort : ExitSuccess;
    }

    private static int RunRoots(CommandFlags flags)
    {
        var service = BuildService();
        DoctorReport report = service.Inspect();

        if (flags.Json)
        {
            Console.Out.WriteLine(DoctorService.ToJson(report));
            return ExitSuccess;
        }

        Console.Out.WriteLine("coppice roots");
        Console.Out.WriteLine();
        Console.Out.WriteLine(TextReport.RootHeader());
        Console.Out.WriteLine(new string('-', 100));

        foreach (RootReport root in report.Roots)
        {
            Console.Out.WriteLine(TextReport.RootRow(root.LocationId, root.Root, service.HomeDirectory));
            Console.Out.WriteLine(
                $"      via: {TextReport.Via(root.Root.Via)}"
                + $"{(root.Root.ViaDetail is null ? string.Empty : $" ({root.Root.ViaDetail})")}"
                + $", validity: {TextReport.Validity(root.Root.Validity)}"
                + $", role: {TextReport.Role(root.Root.Role)}");

            // Named explicitly here as well as marked in the table: `roots` is the command a user runs when
            // they want to know WHY a path was chosen, which is exactly the question a guessed path invites.
            if (TextReport.ConfidenceNote(root.Root) is { } note)
            {
                Console.Out.WriteLine($"      confidence: LOW — {note}");
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{report.Roots.Count} location(s). This command only reads; it changes nothing.");
        return ExitSuccess;
    }

    private static int RunScan(CommandFlags flags)
    {
        var service = BuildService();
        DoctorReport report = service.Inspect();

        // Only scan locations that are Valid and have Clean rights. Ambiguous/Missing/Denied are not scanned.
        var roots = report.Roots
            .Where(r => r.Root.Validity == Core.Domain.RootValidity.Ok
                     && r.Root.Role != Core.Domain.RootRole.Inactive)
            .Select(r => new Ports.ScanRoot(
                r.LocationId,
                r.Root.RealPath,
                NetProfile.Find(r.LocationId)?.Kind.ToString() ?? "unknown",
                ToPortRole(r.Root.Role),
                FingerprintSpecFor(r.LocationId),
                NetProfile.Find(r.LocationId)?.Owner.ToString() ?? "unknown",
                ToPortRisk(NetProfile.Find(r.LocationId)?.Tier ?? Core.Domain.Risk.Review)))
            .ToList();

        // Project roots come from config.toml [projects] or --projects flag. For v0.1 we accept none.
        var projectRoots = Array.Empty<string>();

        var scanService = new ScanService(service.Fs, new SystemProcessRunner(), service.Env, service.State);
        ScanReport scanReport = scanService.RunAsync(roots, projectRoots).GetAwaiter().GetResult();

        if (flags.Json)
        {
            Console.Out.WriteLine(ScanReportToJson(scanReport));
            return ExitSuccess;
        }

        Console.Out.WriteLine("coppice scan");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"snapshot: {scanReport.SnapshotId}");
        Console.Out.WriteLine($"os: {scanReport.OS}");
        Console.Out.WriteLine($"projects: {scanReport.ProjectCount}");
        Console.Out.WriteLine($"ecosystems: {string.Join(", ", scanReport.Ecosystems)}");
        Console.Out.WriteLine();
        Console.Out.WriteLine(TextReport.UsageHeader());
        Console.Out.WriteLine(new string('-', 60));
        Console.Out.WriteLine(TextReport.UsageRow(scanReport.Usage));
        Console.Out.WriteLine();
        Console.Out.WriteLine(TextReport.ItemHeader());
        Console.Out.WriteLine(new string('-', 120));

        foreach (Item item in scanReport.Items)
        {
            Console.Out.WriteLine(TextReport.ItemRow(item));
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{scanReport.Items.Count} item(s) in {scanReport.Ecosystems.Count} ecosystem(s).");

        foreach (string hint in scanReport.OnboardingHints)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"hint: {hint}");
        }

        if (scanReport.Problems.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("HEALTH PROBLEMS:");
            Console.Out.WriteLine(TextReport.ProblemHeader());
            Console.Out.WriteLine(new string('-', 120));
            foreach (Problem p in scanReport.Problems)
            {
                Console.Out.WriteLine(TextReport.ProblemRow(p));
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine("This command only reads and stores a snapshot; it changes nothing.");
        return ExitSuccess;
    }

    private static string ScanReportToJson(ScanReport r) =>
        System.Text.Json.JsonSerializer.Serialize(r, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Core owns the domain enums; Ports declares its own so the plugin boundary stays free of
    /// Core types (NFR-08). These two switches are the whole translation cost, kept in one place
    /// so adding a member to either enum fails the build here rather than silently in a plugin.
    /// </summary>
    private static Ports.RootRole ToPortRole(Core.Domain.RootRole role) => role switch
    {
        Core.Domain.RootRole.Active => Ports.RootRole.Active,
        Core.Domain.RootRole.Additional => Ports.RootRole.Additional,
        Core.Domain.RootRole.Inactive => Ports.RootRole.Inactive,
        _ => Ports.RootRole.Inactive,
    };

    private static Ports.Risk ToPortRisk(Core.Domain.Risk risk) => risk switch
    {
        Core.Domain.Risk.Safe => Ports.Risk.Safe,
        Core.Domain.Risk.Review => Ports.Risk.Review,
        Core.Domain.Risk.Manual => Ports.Risk.Manual,
        _ => Ports.Risk.Manual,
    };

    /// <summary>
    /// The fingerprint a location id declares in the 09 table, in the portable shape plugins see.
    /// Unknown ids get an empty spec, which means "no layout gate" — never a reject.
    /// </summary>
    private static Ports.FingerprintSpec FingerprintSpecFor(string locationId)
    {
        Net.FingerprintSpec? fingerprint = NetProfile.Find(locationId)?.Fingerprint;
        return fingerprint is null
            ? new Ports.FingerprintSpec()
            : new Ports.FingerprintSpec
            {
                RequiredPaths = fingerprint.RequiredPaths,
                EntryPatterns = fingerprint.EntryPatterns,
                LayoutRatio = fingerprint.LayoutRatio,
            };
    }

    private static DoctorService BuildService() =>
        new(new PhysicalFileSystem(), new SystemProcessRunner(), new SystemEnvironment());

    private static int PrintVersion()
    {
        Console.WriteLine($"coppice {typeof(CoppiceApp).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}");
        return ExitSuccess;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("coppice — a cross-platform, plugin-based toolchain & cache cleaner.");
        Console.WriteLine();
        Console.WriteLine("  Harvest what regrows. Never touch the root.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  doctor [--json]     resolve locations and report what was found");
        Console.WriteLine("  roots  [--json]     show each location, how it was resolved, and its validity");
        Console.WriteLine("  scan   [--json]     inventory the caches and store a snapshot");
        Console.WriteLine("  apply  [--plan <file>] [--yes] [--strict] [--resume] [--json]");
        Console.WriteLine("                      execute a plan: path steps MOVE to quarantine, native steps run a tool");
        Console.WriteLine("  audit  show|verify [--json]");
        Console.WriteLine("                      show the mutation log, or replay it against the quarantine index");
        Console.WriteLine("  version             print the build version");
        Console.WriteLine();
        Console.WriteLine("Flags: --json, --no-color");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok · 1 runtime · 2 usage · 3 safety abort · 4 partial apply.");
        Console.WriteLine();
        Console.WriteLine("apply is the only command that changes anything, and it asks first unless --yes is given.");
        return ExitSuccess;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"coppice: {message}");
        Console.Error.WriteLine("Run 'coppice help' for usage.");
        return ExitUsageError;
    }

    private static int NotYetAvailable(string command) =>
        Usage($"'{command}' is not available in this build");
}

/// <summary>The global flags the read-only verbs accept. Unknown flags are rejected rather than ignored (07).</summary>
public sealed record CommandFlags(bool Json)
{
    public static CommandFlags Parse(string[] args) => Parse(args.AsSpan());

    public static CommandFlags Parse(ReadOnlySpan<string> args)
    {
        bool json = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--no-color":
                    // Accepted and ignored: this renderer never emits color, which is the point.
                    break;
                default:
                    throw new ArgumentException($"unknown option '{arg}'");
            }
        }

        return new CommandFlags(json);
    }
}

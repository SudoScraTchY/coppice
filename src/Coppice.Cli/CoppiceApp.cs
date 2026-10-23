using Coppice.Adapters;
using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>
/// The CLI entry point (07).
/// <para>
/// Argument parsing is hand-rolled and tiny on purpose: v0.1 needs a handful of flags, and a parser
/// dependency would be the largest thing in the tool. <c>doctor</c>, <c>roots</c>, <c>scan</c>, <c>plan</c>
/// and <c>audit</c> cannot mutate a scanned location; <c>apply</c> mutates through the gateway behind a
/// confirmation and a write-ahead journal (T-034), and <c>quarantine restore|prune</c> mutates through
/// <see cref="Core.Quarantine.QuarantineRestore"/> behind its own confirmation (T-044).
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
    /// <c>apply</c>, <c>quarantine</c> and <c>tui</c> are the mutating verbs (S-1): the TUI's apply goes
    /// through the same gateway, journal and audit as <c>apply</c>, and its restore through the same
    /// <c>QuarantineRestore</c> as the quarantine verb. <c>plan</c> only reads a snapshot and stores a plan.
    /// </summary>
    public static IReadOnlyDictionary<string, Func<string[], int>> Verbs { get; } =
        new Dictionary<string, Func<string[], int>>(StringComparer.Ordinal)
        {
            ["doctor"] = RunDoctor,
            ["roots"] = RunRoots,
            ["scan"] = RunScan,
            ["plan"] = RunPlan,
            ["apply"] = RunApply,
            ["quarantine"] = RunQuarantine,
            ["audit"] = RunAudit,
            ["tui"] = RunTui,
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

            return command is "clean" or "report"
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

    private static int RunPlan(string[] args) =>
        new PlanCommand(
            new FileSystemStateStore(new PhysicalFileSystem(), new SystemEnvironment()),
            new SystemPlanFileWriter(),
            new SystemApplyConsole())
            .Run(args);

    private static int RunQuarantine(string[] args) =>
        new QuarantineCommand(
            new PhysicalFileSystem(),
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

    private static int RunTui(string[] args) => Tui.TuiCommand.RunSystem(args);

    private static int RunDoctor(string[] args) => RunDoctor(CommandFlags.Parse(args));

    private static int RunRoots(string[] args) => RunRoots(CommandFlags.Parse(args));

    private static int RunScan(string[] args) => RunScan(ScanFlags.Parse(args));

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
            Console.Out.Write(DoctorText.ToText(report, service.HomeDirectory));
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

    private static int RunScan(ScanFlags flags)
    {
        var service = BuildService();
        DoctorReport report = service.Inspect();

        // Only scan locations that are Valid and have Clean rights. Ambiguous/Missing/Denied are not scanned.
        // The mapping lives in ScanWiring so the TUI scans exactly the same set.
        var roots = ScanWiring.FromDoctor(report);

        // Reference resolution needs project roots: without them every item is Unknown and a plan can
        // propose nothing. They are explicit (repeated --projects), never inferred from the cwd.
        var scanService = new ScanService(service.Fs, new SystemProcessRunner(), service.Env, service.State);
        ScanReport scanReport = scanService.RunAsync(roots, flags.ProjectRoots).GetAwaiter().GetResult();

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

    private static DoctorService BuildService() =>
        new(new PhysicalFileSystem(), new SystemProcessRunner(), new SystemEnvironment());

    private static int PrintVersion()
    {
        Console.WriteLine($"coppice {typeof(CoppiceApp).Assembly.GetName().Version?.ToString(3) ?? "unknown"}");
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
        Console.WriteLine("  scan   [--projects <path>]... [--json]");
        Console.WriteLine("                      inventory the caches and store a snapshot; project roots make usage provable");
        Console.WriteLine("  plan   [--policy <preset>] [--keep-latest <n>] [--out <file>] [--json]");
        Console.WriteLine("                      build and store a plan from the latest snapshot (no changes)");
        Console.WriteLine("  apply  [--plan <file>] [--yes] [--strict] [--resume] [--json]");
        Console.WriteLine("                      execute a plan: path steps MOVE to quarantine, native steps run a tool");
        Console.WriteLine("  quarantine list|restore <id>|prune [--older-than <age>] [--yes] [--dry-run] [--json]");
        Console.WriteLine("                      inspect, restore or permanently discard quarantined items");
        Console.WriteLine("  audit  show|verify [--json]");
        Console.WriteLine("                      show the mutation log, or replay it against the quarantine index");
        Console.WriteLine("  tui    [--once] [--no-color] [--projects <path>]...");
        Console.WriteLine("                      keyboard-driven screens: dashboard, roots, items, problems, plan, apply, quarantine");
        Console.WriteLine("  version             print the build version");
        Console.WriteLine();
        Console.WriteLine("Flags: --json, --ci, --no-color");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok · 1 runtime · 2 usage · 3 safety abort · 4 partial apply.");
        Console.WriteLine();
        Console.WriteLine("--ci forces machine-readable output and never prompts; apply --ci needs --yes and --plan,");
        Console.WriteLine("and quarantine restore|prune --ci needs --yes.");
        Console.WriteLine();
        Console.WriteLine("apply, quarantine restore|prune, and tui change anything; all ask before acting.");
        Console.WriteLine("The tool is a native binary or dotnet tool — no container runtime is required.");
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

/// <summary>
/// The scan flags. `--projects` is repeatable; each path is a project tree whose manifests and lock/assets
/// files make usage provable. No roots means Unknown, not "unused".
/// </summary>
public sealed record ScanFlags(bool Json, IReadOnlyList<string> ProjectRoots)
{
    public static ScanFlags Parse(string[] args) => Parse(args.AsSpan());

    public static ScanFlags Parse(ReadOnlySpan<string> args)
    {
        bool json = false;
        var projectRoots = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--ci":
                    // FR-21: machine-readable.
                    json = true;
                    break;
                case "--no-color":
                    // Accepted and ignored: this renderer never emits color.
                    break;
                case "--projects":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        throw new ArgumentException("--projects needs a directory path");
                    }

                    projectRoots.Add(args[++i]);
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        return new ScanFlags(json, projectRoots);
    }
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
                case "--ci":
                    // FR-21: CI mode is machine-readable. Every read-only verb already renders JSON under
                    // --json, and the CLI never emits color or spinners, so forcing the machine form is the
                    // whole of "no spinners/color" here.
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

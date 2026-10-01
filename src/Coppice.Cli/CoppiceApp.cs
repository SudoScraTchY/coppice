using Coppice.Adapters;

namespace Coppice.Cli;

/// <summary>
/// The CLI entry point (07).
/// <para>
/// Argument parsing is hand-rolled and tiny on purpose: v0.1 needs a handful of flags, and a parser
/// dependency would be the largest thing in the tool. Commands dispatch to services that cannot
/// mutate anything, so <c>doctor</c> and <c>roots</c> are safe by construction rather than by
/// promise.
/// </para>
/// <para>
/// Exit codes follow 07 exactly: 0 success, 1 runtime error, 2 usage error, 3 safety abort.
/// </para>
/// </summary>
public static class CoppiceApp
{
    private const int ExitSuccess = 0;
    private const int ExitRuntimeError = 1;
    private const int ExitUsageError = 2;
    private const int ExitSafetyAbort = 3;

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            string command = args.Length > 0 ? args[0] : "help";
            var flags = CommandFlags.Parse(args.AsSpan(1));

            return command switch
            {
                "doctor" => RunDoctor(flags),
                "roots" => RunRoots(flags),
                "scan" or "plan" or "apply" or "clean" or "report" => NotYetAvailable(command),
                "version" or "--version" => PrintVersion(),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => Usage($"unknown command '{command}'"),
            };
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
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{report.Roots.Count} location(s). This command only reads; it changes nothing.");
        return ExitSuccess;
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
        Console.WriteLine("  version             print the build version");
        Console.WriteLine();
        Console.WriteLine("Flags: --json, --no-color");
        Console.WriteLine();
        Console.WriteLine("Nothing here deletes anything. plan and apply arrive in v0.2 and v0.3.");
        return ExitSuccess;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"coppice: {message}");
        Console.Error.WriteLine("Run 'coppice help' for usage.");
        return ExitUsageError;
    }

    private static int NotYetAvailable(string command) =>
        Usage($"'{command}' is not available yet; v0.1 is read-only");
}

/// <summary>The global flags v0.1 accepts. Unknown flags are rejected rather than ignored (07).</summary>
public sealed record CommandFlags(bool Json)
{
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

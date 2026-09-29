namespace Coppice.Cli;

/// <summary>
/// Entry point for the v0.1 read-only surface. Phase 0 delivers foundations only, so the honest
/// behaviour today is to report what exists and what does not — a menu that silently does nothing
/// would be worse than one that says so.
/// </summary>
public static class CoppiceApp
{
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string command = args.Length > 0 ? args[0] : "help";

        return command switch
        {
            "--version" or "version" => PrintVersion(),
            "help" or "--help" or "-h" => PrintHelp(),
            _ => PrintHelp(),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"coppice {typeof(CoppiceApp).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("coppice — a cross-platform, plugin-based toolchain & cache cleaner.");
        Console.WriteLine();
        Console.WriteLine("  Harvest what regrows. Never touch the root.");
        Console.WriteLine();
        Console.WriteLine("Status: foundations only (phase 0). No cleaning commands exist yet.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  doctor   explain what is on this machine        (coming in v0.1)");
        Console.WriteLine("  roots    list resolved cache locations          (coming in v0.1)");
        Console.WriteLine("  scan     inventory caches, read-only            (coming in v0.1)");
        Console.WriteLine("  plan     produce a reviewed removal plan        (coming in v0.2)");
        Console.WriteLine("  apply    execute a plan you have approved       (coming in v0.3)");
        Console.WriteLine();
        Console.WriteLine("Run 'coppice version' for the build version.");
        return 0;
    }
}

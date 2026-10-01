using System.Text;
using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>
/// Plain-text rendering (07). Deliberately dependency-free and ANSI-free: output must be identical
/// whether it goes to a terminal, a pipe, or a CI log, or the golden tests mean nothing (NFR-09).
/// </summary>
public static class TextReport
{
    /// <summary>Renders one resolved root as a fixed-width row, the way `roots` prints it.</summary>
    public static string RootRow(string locationId, ResolvedRoot root, string? home)
    {
        ArgumentNullException.ThrowIfNull(root);

        return string.Join(
            "  ",
            locationId.PadRight(20),
            Role(root.Role).PadRight(11),
            Validity(root.Validity).PadRight(17),
            Via(root.Via).PadRight(9),
            Shorten(DisplayPath(root.RealPath, home), 46));
    }

    /// <summary>
    /// A concrete next step for a root that will not be cleaned. NFR-10 requires a fix hint: a
    /// denylist hit with no explanation leaves the user thinking the tool is broken.
    /// </summary>
    public static string FixHint(ResolvedRoot root) => root.Validity switch
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

    /// <summary>Header for the `roots` table.</summary>
    public static string RootHeader() => string.Join(
        "  ",
        "LOCATION".PadRight(20),
        "ROLE".PadRight(11),
        "VALIDITY".PadRight(17),
        "VIA".PadRight(9),
        "PATH");

    /// <summary>
    /// Renders the machine's home as `~` so a path stays readable. Purely cosmetic, and only for
    /// display: the machine-readable output keeps the real path.
    /// </summary>
    public static string DisplayPath(string path, string? home)
    {
        if (string.IsNullOrEmpty(home) || !path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        string tail = path[home.Length..];
        return tail.Length == 0 ? "~" : "~" + tail;
    }

    public static string Shorten(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        // Keep the tail: the last path segment is the part a user recognizes.
        return "..." + value[^(max - 3)..];
    }

    public static string Role(RootRole role) => role switch
    {
        RootRole.Active => "active",
        RootRole.Additional => "additional",
        RootRole.Inactive => "inactive",
        _ => role.ToString(),
    };

    public static string Validity(RootValidity validity) => validity switch
    {
        RootValidity.Ok => "ok",
        RootValidity.NotFound => "not-found",
        RootValidity.FailsFingerprint => "bad-fingerprint",
        RootValidity.Denied => "denied",
        RootValidity.Ambiguous => "ambiguous",
        RootValidity.NeedsElevation => "needs-elevation",
        _ => validity.ToString(),
    };

    public static string Via(ResolvedVia via) => via switch
    {
        ResolvedVia.Pin => "pin",
        ResolvedVia.Tool => "tool",
        ResolvedVia.Env => "env",
        ResolvedVia.Config => "config",
        ResolvedVia.Registry => "registry",
        ResolvedVia.OsFile => "os-file",
        ResolvedVia.Default => "default",
        _ => via.ToString(),
    };

    /// <summary>Writes text to stdout only.</summary>
    public static void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Console.Out.WriteLine(text);
    }

    public static void Write(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Console.Out.Write(builder.ToString());
    }
}

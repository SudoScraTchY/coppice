using System.Collections.Immutable;
using Coppice.Ports;

namespace Coppice.Core.PathSafety;

/// <summary>
/// Per-OS denylist entries from 06-safety-model. A denylist hit is a hard floor: validity = Denied,
/// reported, never cleaned.
/// </summary>
public static class Denylist
{
    /// <summary>How deep a denylist rule reaches.</summary>
    public enum DenyScope
    {
        /// <summary>Only the path itself. Its children stay cleanable.</summary>
        Container,

        /// <summary>The path and everything beneath it.</summary>
        Tree,
    }

    /// <summary>One denylist rule and the operating systems it applies to.</summary>
    public sealed record DenyRule(string Entry, DenyScope Scope, OperatingSystemKind[] OperatingSystems)
    {
        public bool AppliesTo(OperatingSystemKind os) => Array.IndexOf(OperatingSystems, os) >= 0;
    }

    private static readonly OperatingSystemKind[] Win = [OperatingSystemKind.Windows];
    private static readonly OperatingSystemKind[] Posix = [OperatingSystemKind.Linux, OperatingSystemKind.MacOS];
    private static readonly OperatingSystemKind[] All = [OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS];

    /// <summary>
    /// The denylist (06-safety-model), with how far each rule reaches and which OSes it covers.
    /// <para>
    /// A <see cref="DenyScope.Container"/> rule protects ONLY the path itself. A user's home
    /// directory is listed because claiming <c>~</c> as a cache root would be catastrophic — but
    /// <c>~/.nuget/packages</c> and <c>~/go/pkg/mod</c> are exactly the caches this tool exists to
    /// clean, so its children must remain cleanable. The filesystem root behaves the same way.
    /// </para>
    /// <para>
    /// A <see cref="DenyScope.Tree"/> rule is installer-owned: the path and everything beneath it are
    /// refused outright. <c>/usr</c> is the clearest case — a toolchain under <c>/usr/local</c> is
    /// owned by the OS package manager, so the spec routes it to report-only, never to deletion.
    /// </para>
    /// <para>
    /// Rules are scoped per OS on purpose: <c>/opt</c> is protected on Linux, but on macOS
    /// <c>/opt/homebrew</c> is where Homebrew keeps user-managed toolchains.
    /// </para>
    /// <para>
    /// A <c>{0}</c> token stands for any drive letter, and <c>{home}</c> or <c>~</c> for the
    /// caller-supplied user profile. The denylist never reads the running machine, so it is
    /// reproducible and fully testable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DenyRule> Rules { get; } =
    [
        // ---- Container rules: the path itself only ----
        new("/", DenyScope.Container, Posix),
        new("{home}", DenyScope.Container, All),
        new("/home", DenyScope.Container, [OperatingSystemKind.Linux]),
        new("/Users", DenyScope.Container, [OperatingSystemKind.MacOS]),

        // ---- Windows trees ----
        new("{0}:\\windows", DenyScope.Tree, Win),
        new("{0}:\\program files", DenyScope.Tree, Win),
        new("{0}:\\program files (x86)", DenyScope.Tree, Win),
        new("{0}:\\programdata", DenyScope.Tree, Win),
        new("{home}\\desktop", DenyScope.Tree, Win),
        new("{home}\\documents", DenyScope.Tree, Win),
        new("{home}\\downloads", DenyScope.Tree, Win),
        new("{home}\\onedrive", DenyScope.Tree, Win),
        new("{home}\\one drive", DenyScope.Tree, Win),
        new("{0}:\\users\\public\\onedrive", DenyScope.Tree, Win),

        // ---- macOS trees ----
        new("~/Desktop", DenyScope.Tree, [OperatingSystemKind.MacOS]),
        new("~/Documents", DenyScope.Tree, [OperatingSystemKind.MacOS]),
        new("~/Downloads", DenyScope.Tree, [OperatingSystemKind.MacOS]),
        new("/System", DenyScope.Tree, [OperatingSystemKind.MacOS]),
        new("/Library", DenyScope.Tree, [OperatingSystemKind.MacOS]),

        // ---- Linux trees ----
        new("/usr", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/etc", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/var", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/boot", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/bin", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/sbin", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/lib", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/lib32", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/lib64", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/libx32", DenyScope.Tree, [OperatingSystemKind.Linux]),
        new("/opt", DenyScope.Tree, [OperatingSystemKind.Linux]),
    ];

    /// <summary>Every denylist entry that applies to an OS, for the report and for test coverage.</summary>
    public static IReadOnlyList<string> EntriesFor(OperatingSystemKind os) =>
        [.. Rules.Where(r => r.AppliesTo(os)).Select(r => r.Entry)];

    /// <summary>
    /// Checks whether <paramref name="path"/> (assumed canonical for the given OS) is denied.
    /// This two-argument form cannot evaluate the home-directory rules; prefer the overload that
    /// takes the home directory, which every real caller can supply.
    /// </summary>
    public static bool IsDenied(string path, OperatingSystemKind os) => IsDenied(path, os, homeDirectory: null);

    /// <summary>
    /// Denylist check with the caller's home directory. Returns true when the path matches any
    /// entry exactly, or is a child of a protected entry. A denylist hit is a hard floor: the root
    /// is reported as <c>Denied</c> and never cleaned (06-safety-model).
    /// </summary>
    public static bool IsDenied(string path, OperatingSystemKind os, string? homeDirectory)
    {
        string? matched = Match(path, os, homeDirectory, out _);
        return matched is not null;
    }

    /// <summary>Returns the (first matching) denylist entry that caused the denial, or null.</summary>
    public static string? WhichDenied(string path, OperatingSystemKind os) => WhichDenied(path, os, null);

    /// <summary>Returns the (first matching) denylist entry that caused the denial, or null.</summary>
    public static string? WhichDenied(string path, OperatingSystemKind os, string? homeDirectory) =>
        Match(path, os, homeDirectory, out _);

    private static string? Match(string path, OperatingSystemKind os, string? home, out string? expanded)
    {
        expanded = null;

        if (os is not (OperatingSystemKind.Windows or OperatingSystemKind.Linux or OperatingSystemKind.MacOS))
        {
            return null;
        }

        var homeLower = home?.ToLowerInvariant().TrimEnd('/', '\\');

        // "/" is the filesystem root: trimming it away would turn the single most important rule
        // into a no-op, so the root is recognized before any normalization happens.
        var rawCandidate = path.ToLowerInvariant();
        var candidate = rawCandidate.TrimEnd('/', '\\');

        // Tree rules first: installer-owned subtrees are the strictest.
        foreach (DenyRule rule in Rules.Where(r => r.Scope == DenyScope.Tree && r.AppliesTo(os)))
        {
            if (Matches(rule.Entry, os, homeLower, candidate, includeChildren: true))
            {
                expanded = rule.Entry;
                return rule.Entry;
            }
        }

        foreach (DenyRule rule in Rules.Where(r => r.Scope == DenyScope.Container && r.AppliesTo(os)))
        {
            // "/" is a CONTAINER rule: it protects the filesystem root itself and nothing
            // beneath it. Matching it as a prefix would deny the entire filesystem, including
            // every cache this tool exists to clean.
            if (rule.Entry == "/"
                ? candidate.Length == 0
                : Matches(rule.Entry, os, homeLower, candidate, includeChildren: false))
            {
                expanded = rule.Entry;
                return rule.Entry;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> equals an expansion of <paramref name="entry"/> and,
    /// for a tree rule, is also a child of it.
    /// </summary>
    private static bool Matches(string entry, OperatingSystemKind os, string? homeLower, string candidate, bool includeChildren)
    {
        foreach (string expanded in ExpandTokens(entry, os, homeLower))
        {
            if (string.IsNullOrEmpty(expanded))
            {
                continue;
            }

            var rule = expanded.ToLowerInvariant().TrimEnd('/', '\\');
            if (rule.Length == 0)
            {
                continue;
            }

            if (candidate == rule)
            {
                return true;
            }

            if (includeChildren
                && (candidate.StartsWith(rule + "/", StringComparison.Ordinal)
                    || candidate.StartsWith(rule + "\\", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Expands one denylist entry into every concrete path it denotes, or yields nothing when it
    /// cannot be resolved here (a <c>{home}</c> rule with no home supplied). A single entry can
    /// denote several paths, because <c>{0}</c> stands for any drive letter.
    /// </summary>
    private static IEnumerable<string> ExpandTokens(string entry, OperatingSystemKind os, string? homeLower)
    {
        if (entry.Contains('{') && string.IsNullOrEmpty(homeLower))
        {
            // {0} alone is fine; only the home tokens need a home.
            if (entry.Contains("{home}", StringComparison.Ordinal)
                || entry.StartsWith("~", StringComparison.Ordinal))
            {
                yield break;
            }
        }

        if (entry.Contains("{0}", StringComparison.Ordinal))
        {
            for (char drive = 'A'; drive <= 'Z'; drive++)
            {
                yield return Substitute(entry, char.ToLowerInvariant(drive).ToString(), homeLower);
            }

            yield break;
        }

        yield return Substitute(entry, string.Empty, homeLower);
    }

    private static string Substitute(string entry, string drive, string? homeLower)
    {
        var result = entry;

        if (result.Contains("{0}", StringComparison.Ordinal))
        {
            result = result.Replace("{0}", drive, StringComparison.Ordinal);
        }

        if (result.Contains("{home}", StringComparison.Ordinal))
        {
            result = result.Replace("{home}", homeLower ?? string.Empty, StringComparison.Ordinal);
        }

        if (result.StartsWith("~/", StringComparison.Ordinal))
        {
            result = (homeLower ?? string.Empty) + result[1..];
        }
        else if (result == "~")
        {
            result = homeLower ?? string.Empty;
        }

        return result;
    }
}

using System.Text.RegularExpressions;

namespace Coppice.Plugins.Net;

/// <summary>One SDK as <c>dotnet --list-sdks</c> reports it.</summary>
public sealed record SdkEntry(string Version, string InstallPath);

/// <summary>One runtime as <c>dotnet --list-runtimes</c> reports it.</summary>
public sealed record RuntimeEntry(string Name, string Version, string InstallPath);

/// <summary>
/// Parsers for recorded real tool output (11-testing: "Recorded real tool outputs … serve as
/// resolver golden fixtures").
/// <para>
/// PURE and total: every method returns null or an empty list rather than throwing. A single
/// malformed line in a vendor's output must not abort a scan, and an SDK we cannot parse must not
/// be silently treated as current — the health check is what reports it.
/// </para>
/// </summary>
public static partial class DotNetToolOutputParser
{
    /// <summary>
    /// <c>9.0.300 [C:\Program Files\dotnet\sdk]</c> — version, space, bracketed path.
    /// The bracket is part of the format on every OS, which is why this one parser covers all three.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<version>\S+)\s+\[(?<path>[^\]]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkLine();

    /// <summary><c>Microsoft.NETCore.App 8.0.17 [C:\…\shared\Microsoft.NETCore.App]</c></summary>
    [GeneratedRegex(@"^\s*(?<name>\S+)\s+(?<version>\S+)\s+\[(?<path>[^\]]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeLine();

    /// <summary><c>global-packages: C:\Users\dev\.nuget\packages</c></summary>
    [GeneratedRegex(@"^\s*(?<key>[a-z-]+)\s*:\s*(?<value>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalsLine();

    public static IReadOnlyList<SdkEntry> ParseSdks(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var entries = new List<SdkEntry>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = SdkLine().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                entries.Add(new SdkEntry(match.Groups["version"].Value, match.Groups["path"].Value));
            }
        }

        return entries;
    }

    public static IReadOnlyList<RuntimeEntry> ParseRuntimes(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var entries = new List<RuntimeEntry>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = RuntimeLine().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                entries.Add(new RuntimeEntry(
                    match.Groups["name"].Value,
                    match.Groups["version"].Value,
                    match.Groups["path"].Value));
            }
        }

        return entries;
    }

    /// <summary>
    /// Parses <c>dotnet nuget locals --list</c> into its label-to-path pairs. The value may be
    /// quoted on some SDK builds, so quotes are stripped rather than becoming part of the path.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseNuGetLocals(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = LocalsLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            string value = match.Groups["value"].Value.Trim().Trim('"');
            if (value.Length > 0)
            {
                result[match.Groups["key"].Value] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// The architecture encoded in an SDK install path. The .NET layout puts the arch in the path
    /// rather than in the version, so x86/x64 duplicate installs (a 09-ecosystems health check) are
    /// only detectable this way.
    /// </summary>
    public static string? ArchitectureOf(string installPath)
    {
        if (string.IsNullOrEmpty(installPath))
        {
            return null;
        }

        string[] segments = installPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment.Equals("x64", StringComparison.OrdinalIgnoreCase))
            {
                return "x64";
            }

            if (segment.Equals("x86", StringComparison.OrdinalIgnoreCase))
            {
                return "x86";
            }

            if (segment.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            {
                return "arm64";
            }
        }

        // The default layout has no arch segment; a 64-bit host is the overwhelming case, but this
        // returns "unknown" rather than guessing, because an x86 install would be missed.
        return null;
    }
}

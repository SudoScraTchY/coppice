using System.Text.RegularExpressions;

namespace Coppice.Plugins.Net;

/// <summary>
/// The parsers named in 10-formats' closed set: <c>label-value</c> and <c>sdk-bracket-paths</c>.
/// <para>
/// A parser's whole job is to survive a real tool's real output, which is messier than its
/// documentation suggests. The captured Windows output is <c>global-packages: C:\...\packages\</c>
/// with a trailing separator, real output sometimes carries an <c>info :</c> prefix, and an empty
/// response is a legitimate answer meaning "no SDKs installed". Each of those is a case below.
/// </para>
/// <para>
/// Every parser is pure: text in, value out, never touching the filesystem or the process.
/// </para>
/// </summary>
public static partial class NetOutputParsers
{
    /// <summary>
    /// <c>label-value</c>: <c>global-packages: /home/dev/.nuget/packages/</c>.
    /// Returns the value for the requested label, or null when the label is absent.
    /// </summary>
    public static string? ParseLabelValue(string output, string label)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        foreach (string line in SplitLines(output))
        {
            // Strip a diagnostic prefix. Real output carries "info :" / "warning :" on some
            // invocations, and a parser that does not tolerate it reports "no cache" on a machine
            // that has one — the worst possible failure for a cleanup tool.
            string working = StripDiagnosticPrefix(line);

            int colon = working.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string found = working[..colon].Trim();
            if (!found.Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = working[(colon + 1)..].Trim();

            // Quoted values appear when a path contains a space and the shell quoted it.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value.Trim();
        }

        return null;
    }

    /// <summary>Parses every <c>label: value</c> pair in the output.</summary>
    public static IReadOnlyDictionary<string, string> ParseLabelValueAll(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in SplitLines(output))
        {
            string working = StripDiagnosticPrefix(line);
            int colon = working.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string label = working[..colon].Trim();
            string value = working[(colon + 1)..].Trim().Trim('"');
            if (label.Length > 0 && value.Length > 0)
            {
                result[label] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// <c>sdk-bracket-paths</c>: one <c>VERSION [PATH]</c> per line, as <c>dotnet --list-sdks</c>
    /// and <c>dotnet --list-runtimes</c> emit.
    /// </summary>
    public static IReadOnlyList<(string Name, string Version, string Path)> ParseBracketPaths(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var results = new List<(string, string, string)>();
        foreach (string line in SplitLines(output))
        {
            string working = StripDiagnosticPrefix(line);
            if (working.Length == 0)
            {
                continue;
            }

            Match match = BracketPathPattern().Match(working);
            if (!match.Success)
            {
                continue;
            }

            string head = match.Groups["head"].Value.Trim();

            // "<name> <version> [path]" for runtimes; just "<version> [path]" for SDKs.
            int space = head.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                results.Add((head[..space].Trim(), head[(space + 1)..].Trim(), match.Groups["path"].Value.Trim()));
            }
            else
            {
                results.Add((string.Empty, head, match.Groups["path"].Value.Trim()));
            }
        }

        return results;
    }

    private static string StripDiagnosticPrefix(string line)
    {
        string trimmed = line.Trim();

        // "info : ", "warning : ", "error : " — MSBuild-style prefixes on dotnet output.
        foreach (string level in (string[])["info", "warning", "error", "debug"])
        {
            string prefix = level + " :";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].TrimStart();
            }
        }

        return trimmed;
    }

    private static IEnumerable<string> SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^(?<head>.+?)\s*\[(?<path>[^\]]+)\]\s*$")]
    private static partial Regex BracketPathPattern();
}

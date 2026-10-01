namespace Coppice.Plugins.Net;

/// <summary>A path reported by a tool query, with the label it was filed under.</summary>
public sealed record LabeledPath(string Label, string Path);

/// <summary>A global tool as reported by <c>dotnet tool list -g</c>.</summary>
public sealed record GlobalTool(string PackageId, string Version, string Command);

/// <summary>
/// Parsers for the recorded <c>dotnet</c> output formats (10-formats' built-in parser set).
/// <para>
/// Each parser is total: malformed lines are skipped, never thrown. Tool output is untrusted input
/// (the same reason a poisoned <c>go</c> on PATH is survivable), and a single stray line must not
/// abort a scan of a machine that happens to have an unexpected format.
/// </para>
/// </summary>
public static class DotnetOutputParser
{
    /// <summary>
    /// Parses <c>dotnet nuget locals --list</c>: <c>{label}: {path}</c> per line.
    /// <para>
    /// The path is trimmed of trailing separators. Real NuGet prints <c>global-packages: …\packages\</c>
    /// with a trailing backslash, and keeping it would make the value fail equality against a path
    /// from any other source — producing a phantom second root for the same directory.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LabeledPath> ParseLabelValuePaths(string? output)
    {
        var results = new List<LabeledPath>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == line.Length - 1)
            {
                continue;
            }

            string label = line[..colon].Trim();
            string path = line[(colon + 1)..].Trim();

            if (label.Length == 0 || path.Length == 0)
            {
                continue;
            }

            results.Add(new LabeledPath(label, TrimTrailingSeparators(path)));
        }

        return results;
    }

    /// <summary>
    /// Parses <c>dotnet --list-sdks</c>: <c>{version} [{path}]</c> per line.
    /// <para>
    /// This is the spec's <c>sdk-bracket-paths</c> parser. A pre-release version contains a '-',
    /// so the version is taken as everything before the LAST space-then-bracket, not by splitting
    /// on the bracket alone.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SdkInfo> ParseSdks(string? output)
    {
        var results = new List<SdkInfo>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            int open = line.LastIndexOf('[');
            int close = line.LastIndexOf(']');
            if (open <= 0 || close <= open)
            {
                continue;
            }

            string version = line[..open].Trim();
            string path = TrimTrailingSeparators(line[(open + 1)..close].Trim());

            if (version.Length == 0 || path.Length == 0)
            {
                continue;
            }

            results.Add(new SdkInfo(version, path));
        }

        return results;
    }

    /// <summary>
    /// Parses <c>dotnet --list-runtimes</c>: <c>{name} {version} [{path}]</c> per line. The name
    /// itself contains dots but no spaces, so the last space-separated field before the bracket is
    /// the version and the one before that is the name.
    /// </summary>
    public static IReadOnlyList<RuntimeInfo> ParseRuntimes(string? output)
    {
        var results = new List<RuntimeInfo>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            int open = line.LastIndexOf('[');
            int close = line.LastIndexOf(']');
            if (open <= 0 || close <= open)
            {
                continue;
            }

            string path = TrimTrailingSeparators(line[(open + 1)..close].Trim());
            string head = line[..open].Trim();

            int lastSpace = head.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                continue;
            }

            string name = head[..lastSpace].Trim();
            string version = head[(lastSpace + 1)..].Trim();

            if (name.Length == 0 || version.Length == 0 || path.Length == 0)
            {
                continue;
            }

            results.Add(new RuntimeInfo(name, version, path));
        }

        return results;
    }

    /// <summary>
    /// Parses <c>dotnet tool list -g</c>, a fixed-width table preceded by a header and a dashed
    /// rule. Returns empty for the "No global tools installed." prose form rather than reporting a
    /// tool named "No".
    /// </summary>
    public static IReadOnlyList<GlobalTool> ParseGlobalTools(string? output)
    {
        var results = new List<GlobalTool>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            // Header row, and the "no tools" prose, both fail the version-shape check.
            if (!char.IsAsciiDigit(fields[1][0]))
            {
                continue;
            }

            results.Add(new GlobalTool(fields[0], fields[1], fields.Length > 2 ? fields[2] : string.Empty));
        }

        return results;
    }

    /// <summary>
    /// Trims trailing path separators without ever destroying a root. "/" stays "/" and "C:\"
    /// stays "C:\" — trimming the latter to "C:" would turn a drive root into a drive-RELATIVE
    /// path, which resolves against the current directory on that drive and could name an entirely
    /// different directory than the one the tool queried.
    /// </summary>
    public static string TrimTrailingSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            return path[..1];
        }

        // A drive root is a letter followed by ':' with nothing after it.
        bool isDriveRoot = trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]);
        return isDriveRoot ? trimmed + path[^1] : trimmed;
    }
}

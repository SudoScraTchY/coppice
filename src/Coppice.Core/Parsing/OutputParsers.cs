using System.Text.Json;

namespace Coppice.Parsing;

/// <summary>How a parser read the output, and what it could not. Never an exception (FR-02).</summary>
/// <param name="Values">What the parser understood, in the order it found them.</param>
/// <param name="Issues">Everything it could not read. Non-empty means lower confidence, never a guess.</param>
public sealed record ParseResult(IReadOnlyList<string> Values, IReadOnlyList<ParseIssue> Issues)
{
    public static ParseResult Empty { get; } = new([], []);

    public bool IsConfident => Issues.Count == 0;

    /// <summary>The single value, when the output held exactly one. Null for zero or several.</summary>
    public string? Single
    {
        get
        {
            IReadOnlyList<string> absolute = AbsoluteValues;
            return absolute.Count == 1 ? absolute[0] : null;
        }
    }

    /// <summary>
    /// The values that look like absolute paths. A caller resolving a location wants a path, and a
    /// single-value query that returned prose ("command not found") yields nothing here rather than
    /// the prose itself.
    /// </summary>
    public IReadOnlyList<string> AbsoluteValues =>
        [.. Values.Where(IsAbsolutePath)];

    private static bool IsAbsolutePath(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        // Unix root, Windows drive, or UNC. A bare relative path is NOT accepted: a tool query that
        // answers with "packages" has not told us where anything is.
        if (value[0] is '/' or '\\')
        {
            return true;
        }

        return value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] is '/' or '\\');
    }
}

/// <summary>One thing a parser could not read, with enough context to diagnose it.</summary>
/// <param name="Line">The offending line, truncated. Null when the problem is the output as a whole.</param>
/// <param name="Reason">Why it was not understood.</param>
public sealed record ParseIssue(string? Line, string Reason)
{
    public override string ToString() =>
        Line is null ? Reason : $"'{Shorten(Line)}': {Reason}";

    private static string Shorten(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "...");
}

/// <summary>
/// The closed set of built-in output parsers (T-021, 10-formats).
/// <para>
/// Each parser is TOTAL: nothing here throws. Tool output is untrusted input — a poisoned
/// <c>go</c> on PATH can print anything, and a machine with a newer tool version can print a shape
/// nobody anticipated. Neither may abort a scan, and neither may be silently interpreted (FR-02).
/// </para>
/// <para>
/// So the contract is: read what you recognise, report what you do not, and let the caller lower its
/// confidence. A parser that guesses is worse than one that returns nothing, because the guess becomes
/// a candidate path and candidates are fed to the denylist.
/// </para>
/// </summary>
public static class OutputParsers
{
    /// <summary>
    /// <c>label-value</c>: <c>KEY: value</c> or <c>KEY = value</c> per line, optionally quoted.
    /// <para>
    /// Serves <c>dotnet nuget locals --list</c> (<c>global-packages: C:\…</c>) and
    /// <c>go env GOMODCACHE</c> (<c>GOMODCACHE='/home/u/go/pkg/mod'</c>). The single quotes matter:
    /// Go quotes its values, so a parser that does not strip them yields a path starting with a
    /// quote character, which matches nothing and produces a phantom unresolved location.
    /// </para>
    /// </summary>
    public static ParseResult ParseLabelValue(string? output)
    {
        var values = new List<string>();
        var issues = new List<ParseIssue>();

        foreach (string line in SplitLines(output))
        {
            int separator = FindLabelSeparator(line);
            if (separator < 0)
            {
                issues.Add(new ParseIssue(line, "no ':' or '=' separator"));
                continue;
            }

            string label = line[..separator].Trim();
            string value = Unquote(line[(separator + 1)..].Trim());

            if (label.Length == 0)
            {
                issues.Add(new ParseIssue(line, "empty label"));
                continue;
            }

            if (value.Length == 0)
            {
                issues.Add(new ParseIssue(line, $"label '{label}' has no value"));
                continue;
            }

            // A value that is not a path is prose, and prose must not become a candidate root.
            // Without this, `bash: go: command not found` parses as label "bash" with value
            // "go: command not found" — a plausible-looking resolution for a tool that is not installed.
            if (!LooksAbsolute(value))
            {
                issues.Add(new ParseIssue(line, $"'{label}' value is not an absolute path"));
                continue;
            }

            values.Add(TrimTrailingSeparators(value));
        }

        return new ParseResult(values, issues);
    }

    /// <summary>
    /// <c>label-value</c>, but only the value for one named label. Used when a query reports several
    /// variables and the caller wants exactly one (<c>go env GOMODCACHE</c> among many).
    /// </summary>
    public static ParseResult ParseLabelValueFor(string? output, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        foreach (string line in SplitLines(output))
        {
            int separator = FindLabelSeparator(line);
            if (separator < 0)
            {
                continue;
            }

            if (!string.Equals(line[..separator].Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = Unquote(line[(separator + 1)..].Trim());

            // Order matters: an EMPTY value (Go's GOEXE='') is "the tool told me it is unset", which
            // is a different and more useful answer than "the tool said something that is not a path".
            if (value.Length == 0)
            {
                return new ParseResult([], [new ParseIssue(line, $"'{label}' has no value")]);
            }

            if (!LooksAbsolute(value))
            {
                return new ParseResult([], [new ParseIssue(line, $"'{label}' value '{value}' is not an absolute path")]);
            }

            return new ParseResult([TrimTrailingSeparators(value)], []);
        }

        return new ParseResult([], [new ParseIssue(null, $"no line labelled '{label}'")]);
    }

    /// <summary>
    /// <c>line</c>: one bare value per line. Serves <c>dotnet --list-sdks</c> without the bracket
    /// suffix, and any query that prints one path per line.
    /// <para>
    /// Lines that are not absolute paths are issues, not values. That is what makes
    /// <c>go: command not found</c> produce an empty result with an explanation rather than a
    /// candidate root.
    /// </para>
    /// </summary>
    public static ParseResult ParseLines(string? output)
    {
        var values = new List<string>();
        var issues = new List<ParseIssue>();

        foreach (string line in SplitLines(output))
        {
            string value = Unquote(line);

            if (LooksAbsolute(value))
            {
                values.Add(TrimTrailingSeparators(value));
            }
            else
            {
                issues.Add(new ParseIssue(line, "not an absolute path"));
            }
        }

        return new ParseResult(values, issues);
    }

    /// <summary>
    /// <c>sdk-bracket-paths</c>: <c>{version} [{path}]</c> per line. Serves <c>dotnet --list-sdks</c>.
    /// <para>
    /// The bracket is taken from the LAST '[' and the LAST ']', because a pre-release version contains
    /// a '-' and a path can contain a '[' — so the first bracket is not reliably the delimiter.
    /// </para>
    /// </summary>
    public static ParseResult ParseBracketPaths(string? output)
    {
        var values = new List<string>();
        var issues = new List<ParseIssue>();

        foreach (string line in SplitLines(output))
        {
            int open = line.LastIndexOf('[');
            int close = line.LastIndexOf(']');

            if (open <= 0 || close <= open)
            {
                issues.Add(new ParseIssue(line, "no '[path]' suffix"));
                continue;
            }

            string path = TrimTrailingSeparators(Unquote(line[(open + 1)..close].Trim()));

            if (!LooksAbsolute(path))
            {
                issues.Add(new ParseIssue(line, $"bracketed value '{path}' is not an absolute path"));
                continue;
            }

            values.Add(path);
        }

        return new ParseResult(values, issues);
    }

    /// <summary>
    /// <c>json-path</c>: a dotted path into a JSON document, e.g. <c>cache</c> into
    /// <c>{ "cache": "C:\\…", "registry": "…" }</c>. Serves <c>npm config --json</c>.
    /// <para>
    /// JSON output is NOT the same as the default <c>npm config get cache</c>, which prints a bare
    /// path with no JSON around it. Both are supported because <c>--json</c> is the only way to get
    /// several values unambiguously, and a tool that offers it should be read with it.
    /// </para>
    /// </summary>
    public static ParseResult ParseJsonPath(string? output, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (string.IsNullOrWhiteSpace(output))
        {
            return new ParseResult([], [new ParseIssue(null, "no output")]);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(output, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            JsonElement current = document.RootElement;

            foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out JsonElement next))
                {
                    return new ParseResult([], [new ParseIssue(null, $"no property '{path}' (missing '{segment}')")]);
                }

                current = next;
            }

            string? value = current.ValueKind == JsonValueKind.String ? current.GetString() : null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return new ParseResult([], [new ParseIssue(null, $"'{path}' is {Describe(current.ValueKind)}")]);
            }

            string trimmed = TrimTrailingSeparators(value);
            return new ParseResult([trimmed], LooksAbsolute(trimmed) ? [] : [new ParseIssue(null, $"'{path}' is not an absolute path")]);
        }
        catch (JsonException ex)
        {
            return new ParseResult([], [new ParseIssue(null, $"invalid JSON: {ex.Message.Split(',')[0]}")]);
        }
    }

    /// <summary>
    /// <c>regex</c>: the spec names it, and it is refused in manifests (10-formats flags it) because a
    /// pattern is code coppice cannot verify at load time. It exists here for code plugins that accept
    /// the risk explicitly, with a match timeout so a pathological pattern cannot hang a scan.
    /// </summary>
    public static ParseResult ParseRegex(string? output, string pattern, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        if (string.IsNullOrWhiteSpace(output))
        {
            return new ParseResult([], [new ParseIssue(null, "no output")]);
        }

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                timeout ?? TimeSpan.FromMilliseconds(500));
        }
        catch (ArgumentException ex)
        {
            return new ParseResult([], [new ParseIssue(null, $"invalid pattern: {ex.Message.Split('(')[0].Trim()}")]);
        }

        var values = new List<string>();
        var issues = new List<ParseIssue>();

        foreach (string line in SplitLines(output))
        {
            try
            {
                System.Text.RegularExpressions.Match match = regex.Match(line);
                if (match.Success)
                {
                    values.Add(TrimTrailingSeparators(match.Groups.Count > 1 && match.Groups[1].Success
                        ? match.Groups[1].Value
                        : match.Value));
                }
                else
                {
                    issues.Add(new ParseIssue(line, "no regex match"));
                }
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                issues.Add(new ParseIssue(line, "regex timed out"));
            }
        }

        return new ParseResult(values, issues);
    }

    /// <summary>
    /// Splits output into trimmed, non-empty lines. Handles LF, CRLF and a missing trailing newline.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// Strips matching surrounding quotes. Go prints <c>GOMODCACHE='/home/u/go/pkg/mod'</c> and a
    /// leading apostrophe would otherwise become part of the path.
    /// </summary>
    public static string Unquote(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        char first = value[0];
        char last = value[^1];

        bool quoted = (first == '"' && last == '"') || (first == '\'' && last == '\'');
        return quoted ? value[1..^1] : value;
    }

    /// <summary>
    /// Trims trailing separators without destroying a root. <c>/</c> stays <c>/</c> and <c>C:\</c> stays
    /// <c>C:\</c> — trimming the latter to <c>C:</c> produces a drive-RELATIVE path that resolves
    /// against the current directory of that drive, naming a different directory than the tool queried.
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

        bool isDriveRoot = trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]);
        return isDriveRoot ? trimmed + path[^1] : trimmed;
    }

    /// <summary>True for a Unix root, a Windows drive path, or a UNC share. A bare word is not a path.</summary>
    public static bool LooksAbsolute(string value) =>
        value.Length > 0
        && (value[0] is '/' or '\\'
            || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\'));

    private static int FindLabelSeparator(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);

        // "=" only counts when there is no colon, so a Windows path's drive colon does not win over the
        // label separator in "PATH=C:\tools".
        int equals = line.IndexOf('=', StringComparison.Ordinal);

        if (colon >= 0 && equals >= 0)
        {
            return colon < equals ? colon : equals;
        }

        return colon >= 0 ? colon : equals;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object, not a string",
        JsonValueKind.Array => "an array, not a string",
        JsonValueKind.Number => "a number, not a string",
        JsonValueKind.True or JsonValueKind.False => "a boolean, not a string",
        JsonValueKind.Null => "null",
        _ => "not a string",
    };
}

using System.Globalization;

namespace Coppice.Core.Config;

/// <summary>
/// A minimal TOML reader scoped to what a config file uses, with LINE NUMBERS retained (T-029, NFR-10).
/// <para>
/// Deliberately not the manifest reader. <c>Coppice.Manifests</c> has a full TOML reader, but Core must
/// not depend on it (NFR-08), and this reader needs something the manifest one does not do: report the
/// line each key was READ FROM, so an invalid config can say "policy.keep_latest on line 7" instead of
/// "invalid config". Two small readers beat a dependency cycle.
/// </para>
/// <para>
/// Supports exactly: comments, <c>version = 1</c> scalars, <c>[table]</c> headers, bare and quoted keys,
/// basic strings with the usual escapes, integer and boolean values, and arrays of strings (possibly
/// multi-line). Anything else is refused by name rather than skipped — a config file is small and
/// hand-edited, so a construct this cannot read is a typo the user needs told about.
/// </para>
/// </summary>
public sealed class ConfigDocument
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private ConfigDocument()
    {
    }

    /// <summary>One key's value and where it came from.</summary>
    public sealed record Entry(object? Value, int Line, string Raw);

    /// <summary>All keys, dotted: "policy.keep_latest". Used to detect typos.</summary>
    public IReadOnlyCollection<string> Keys => _entries.Keys;

    public bool Contains(string dottedKey) => _entries.ContainsKey(dottedKey);

    public int LineOf(string dottedKey) => _entries.TryGetValue(dottedKey, out Entry? e) ? e.Line : 0;

    public object? Raw(string dottedKey) => _entries.TryGetValue(dottedKey, out Entry? e) ? e.Value : null;

    public string? String(string dottedKey) => _entries.TryGetValue(dottedKey, out Entry? e) && e.Value is string s ? s : null;

    public int? Int(string dottedKey) => _entries.TryGetValue(dottedKey, out Entry? e) && e.Value is int i ? i : null;

    public bool? Bool(string dottedKey) => _entries.TryGetValue(dottedKey, out Entry? e) && e.Value is bool b ? b : null;

    public IReadOnlyList<string>? StringArray(string dottedKey) =>
        _entries.TryGetValue(dottedKey, out Entry? e) && e.Value is List<string> list ? list : null;

    /// <summary>
    /// The literal text of the line a key was on, for a hint that quotes what the user actually wrote.
    /// </summary>
    public string SourceLine(string dottedKey, string wholeDocument)
    {
        int line = LineOf(dottedKey);

        if (line <= 0 || wholeDocument.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = wholeDocument.Split('\n');

        return line <= lines.Length ? lines[line - 1].TrimEnd('\r') : string.Empty;
    }

    /// <summary>
    /// Parses <paramref name="text"/>.
    /// <para>
    /// Throws <see cref="ConfigParseException"/> rather than returning a result: a syntactically broken
    /// file has no partial document to return, and a half-loaded config that silently dropped the tables it
    /// could not read is precisely the failure mode to avoid on a file that steers deletions.
    /// </para>
    /// </summary>
    public static ConfigDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var doc = new ConfigDocument();
        string[] lines = text.Split('\n');
        string table = string.Empty;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i].TrimEnd('\r').Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                // Strip a trailing comment BEFORE looking for the closing bracket. Without this,
                // "[policy]   # the policy" fails as an unclosed header — and a commented table header is
                // the most natural line a user adds to explain what a section is for.
                string header = StripComment(line, lineNumber).TrimEnd();

                if (!header.EndsWith(']'))
                {
                    throw new ConfigParseException(lineNumber, $"Table header '{header}' is not closed with ']'.");
                }

                table = header[1..^1].Trim();

                if (table.Length == 0)
                {
                    throw new ConfigParseException(lineNumber, "Table header has an empty name.");
                }

                continue;
            }

            int eq = FindAssignment(line);
            if (eq < 0)
            {
                throw new ConfigParseException(lineNumber, $"Expected 'key = value' but found '{line}'.");
            }

            string key = line[..eq].Trim().Trim('"', '\'');
            string valueText = line[(eq + 1)..].Trim();

            if (key.Length == 0)
            {
                throw new ConfigParseException(lineNumber, "Key is empty.");
            }

            string dotted = table.Length == 0 ? key : $"{table}.{key}";

            // Strip a trailing comment, but only outside a string: a Windows path may contain '#'.
            valueText = StripComment(valueText, lineNumber);

            object? value = valueText.StartsWith('[')
                ? ReadStringArray(valueText, lineNumber, lines, ref i)
                : ReadScalar(valueText, lineNumber);

            if (doc._entries.ContainsKey(dotted))
            {
                throw new ConfigParseException(lineNumber, $"Key '{dotted}' is set twice.");
            }

            doc._entries[dotted] = new Entry(value, lineNumber, valueText);
        }

        return doc;
    }

    /// <summary>
    /// The index of the '=' that separates key from value.
    /// <para>
    /// Not <c>IndexOf('=')</c>: a quoted key is legal in this format (<c>"nuget-packages"</c> is a pinned
    /// location id, and those routinely contain no '=' but the code must not depend on that), and a value
    /// may contain '=' too. Scanning outside quotes is the only correct version.
    /// </para>
    /// </summary>
    private static int FindAssignment(string line)
    {
        bool inQuotes = false;
        char quote = '"';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    inQuotes = false;
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quote = c;
            }
            else if (c == '=')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Removes a trailing <c># comment</c>, leaving any '#' inside a string alone.</summary>
    private static string StripComment(string value, int lineNumber)
    {
        bool inQuotes = false;
        char quote = '"';

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (inQuotes)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    inQuotes = false;
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quote = c;
            }
            else if (c == '#')
            {
                return value[..i].TrimEnd();
            }
        }

        _ = lineNumber;
        return value;
    }

    private static object? ReadScalar(string text, int lineNumber)
    {
        if (text.Length == 0)
        {
            throw new ConfigParseException(lineNumber, "Value is empty.");
        }

        if (text[0] is '"' or '\'')
        {
            return ReadString(text, lineNumber);
        }

        if (bool.TryParse(text, out bool flag))
        {
            return flag;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return number;
        }

        if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Named through to the message: "expected a whole number, got 'two'" beats a type name.
        throw new ConfigParseException(
            lineNumber,
            $"'{text}' is not a quoted string, a whole number, or true/false.");
    }

    private static string ReadString(string text, int lineNumber)
    {
        char quote = text[0];

        if (text.Length < 2 || text[^1] != quote)
        {
            throw new ConfigParseException(lineNumber, $"String value is not closed with {quote}.");
        }

        string body = text[1..^1];

        // A single-quoted string is literal in TOML, which is exactly what a Windows path wants.
        if (quote == '\'')
        {
            return body;
        }

        var result = new System.Text.StringBuilder(body.Length);

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (c != '\\')
            {
                result.Append(c);
                continue;
            }

            if (i + 1 >= body.Length)
            {
                throw new ConfigParseException(lineNumber, "String ends with a lone backslash.");
            }

            char escape = body[++i];
            result.Append(escape switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => throw new ConfigParseException(
                    lineNumber,
                    $"'\\{escape}' is not a recognised escape. Use '\\\\' for a literal backslash."),
            });
        }

        return result.ToString();
    }

    private static List<string> ReadStringArray(string text, int lineNumber, string[] lines, ref int index)
    {
        if (!text.EndsWith(']'))
        {
            // Multi-line array. Consume following lines until the bracket closes, so the array's own
            // reported line stays the line it STARTED on rather than where it happened to end.
            var joined = new System.Text.StringBuilder(text);
            int start = index;

            while (index + 1 < lines.Length && !joined.ToString().Contains(']', StringComparison.Ordinal))
            {
                index++;
                joined.Append(' ').Append(lines[index].TrimEnd('\r').Trim());
            }

            text = joined.ToString().Trim();

            if (!text.EndsWith(']'))
            {
                throw new ConfigParseException(start + 1, "Array is not closed with ']'.");
            }
        }

        string inner = text[1..^1].Trim();

        if (inner.Length == 0)
        {
            return [];
        }

        var values = new List<string>();
        int cursor = 0;

        while (cursor < inner.Length)
        {
            while (cursor < inner.Length && (inner[cursor] == ',' || char.IsWhiteSpace(inner[cursor])))
            {
                cursor++;
            }

            if (cursor >= inner.Length)
            {
                break;
            }

            if (inner[cursor] is not ('"' or '\''))
            {
                throw new ConfigParseException(
                    lineNumber,
                    "Array entries must be quoted strings; this reader does not infer types inside arrays.");
            }

            char quote = inner[cursor];
            int start = ++cursor;

            while (cursor < inner.Length && inner[cursor] != quote)
            {
                // Skip an escaped quote so "a\"b" does not terminate the string early. Skipping the
                // backslash here matters: the span below is handed to ReadString, which decodes escapes,
                // so it must still SEE them.
                if (inner[cursor] == '\\' && quote == '"' && cursor + 1 < inner.Length)
                {
                    cursor++;
                }

                cursor++;
            }

            if (cursor >= inner.Length)
            {
                throw new ConfigParseException(lineNumber, $"String in array is not closed with {quote}.");
            }

            // Decode through the SAME path a scalar string takes. Skipping this would leave
            // roots = ["D:\\src"] as a literal double backslash — a path that does not exist, on the one
            // platform where the user's config is most likely to name a real directory.
            string raw = inner[start..cursor];
            values.Add(quote == '"' ? ReadString($"\"{raw}\"", lineNumber) : raw);

            cursor++;
        }

        return values;
    }
}

/// <summary>A TOML syntax error in a config file, carrying the line.</summary>
public sealed class ConfigParseException(int lineNumber, string message)
    : Exception($"line {lineNumber}: {message}")
{
    public int LineNumber { get; } = lineNumber;

    public string Detail { get; } = message;
}

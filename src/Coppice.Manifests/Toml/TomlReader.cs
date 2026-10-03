using System.Globalization;
using System.Text;

namespace Coppice.Manifests.Toml;

/// <summary>
/// A minimal TOML reader covering exactly the subset ecosystem manifests use.
/// <para>
/// This is NOT a general TOML implementation and must not become one. The manifest schema
/// (10-formats) needs: comments, one level of table headers, array-of-table headers, dotted keys,
/// strings, integers, floats, booleans, flat arrays, and inline tables. Anything outside that is a
/// parse error naming the line.
/// </para>
/// <para>
/// The subset is the security posture, not a shortcut. A manifest is user-supplied input that names
/// filesystem paths and shell commands, so the reader refuses to interpret rather than guessing:
/// a multi-line string, a date, or an unterminated array is an error, never a silent default. That
/// keeps "we understood your manifest" a property coppice can actually check.
/// </para>
/// </summary>
public static class TomlReader
{
    /// <summary>Parses TOML text into tables, arrays and scalars. Throws <see cref="TomlException"/> on any error.</summary>
    public static TomlTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new TomlTable();
        TomlTable current = root;
        var lines = SplitLines(text);

        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i];
            int lineNumber = i + 1;
            string line = StripComment(raw).Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                current = OpenHeader(root, line, lineNumber);
                continue;
            }

            int equals = FindAssignment(line);
            if (equals < 0)
            {
                throw new TomlException(lineNumber, $"expected 'key = value', found '{Truncate(line)}'");
            }

            string key = line[..equals].Trim();
            string valueText = line[(equals + 1)..].Trim();

            if (key.Length == 0)
            {
                throw new TomlException(lineNumber, "assignment has an empty key");
            }

            // 10-formats' own example writes `sources = [` across several lines. A reader that
            // demanded one line per value would refuse the manifest the spec shows, so an
            // unterminated array or inline table pulls in following lines until it balances.
            if (NeedsContinuation(valueText))
            {
                var joined = new StringBuilder(valueText);
                int firstLine = lineNumber;

                while (!IsBalanced(joined.ToString()) && i + 1 < lines.Count)
                {
                    i++;
                    joined.Append(' ').Append(StripComment(lines[i]).Trim());

                    if (i - firstLine > MaxContinuationLines)
                    {
                        throw new TomlException(
                            firstLine,
                            $"value spans more than {MaxContinuationLines} lines; manifest values must be simple");
                    }
                }

                current.Set(SplitKey(key, lineNumber), ParseValue(joined.ToString(), current, lineNumber));
                continue;
            }

            current.Set(SplitKey(key, lineNumber), ParseValue(valueText, current, lineNumber));
        }

        return root;
    }

    private static TomlTable OpenHeader(TomlTable root, string line, int lineNumber)
    {
        bool array = line.StartsWith("[[", StringComparison.Ordinal);
        string inner = array ? line[2..] : line[1..];
        inner = inner.TrimEnd(']', ' ').Trim();

        if (inner.Length == 0)
        {
            throw new TomlException(lineNumber, "table header has no name");
        }

        TomlTable target = root;

        foreach (string part in SplitKey(inner, lineNumber))
        {
            if (array && ReferenceEquals(target, root) == false && target.IsLastArrayHeader)
            {
                throw new TomlException(lineNumber, $"cannot nest array-of-table '{inner}' inside another array-of-table");
            }

            // `[[location]]` means "append a NEW element to the location array", every time it appears.
            // An earlier version consulted Contains() first, so the SECOND [[location]] found the key
            // already present and was handed the FIRST location's table — silently discarding every
            // location after the first, with no error anywhere. A manifest declaring three locations
            // reported one, and looked like it had been read correctly.
            bool isFinalSegment = part == SplitKey(inner, lineNumber)[^1];

            if (array && isFinalSegment)
            {
                target = target.AddArrayOfTables(part);
                target.IsLastArrayHeader = true;
                continue;
            }

            target = target.GetOrAddTable(part);
        }

        return target;
    }

    /// <summary>
    /// Finds the '=' that separates key from value, skipping any inside a quoted key.
    /// A plain <c>IndexOf('=')</c> would split <c>name = "a=b"</c> correctly but also mis-handle a
    /// quoted key containing '=', so the scan is explicit rather than clever.
    /// </summary>
    private static int FindAssignment(string line)
    {
        bool inBasic = false;
        bool inLiteral = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            switch (c)
            {
                case '\\' when inBasic:
                    i++;
                    break;
                case '"' when !inLiteral:
                    inBasic = !inBasic;
                    break;
                case '\'' when !inBasic:
                    inLiteral = !inLiteral;
                    break;
                case '=' when !inBasic && !inLiteral:
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitKey(string key, int lineNumber)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool inBasic = false;
        bool inLiteral = false;
        bool quoted = false;

        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            switch (c)
            {
                case '\\' when inBasic && i + 1 < key.Length:
                    current.Append(Unescape(key[i + 1].ToString(), lineNumber));
                    i++;
                    quoted = true;
                    break;
                case '"' when !inLiteral:
                    inBasic = !inBasic;
                    quoted = true;
                    break;
                case '\'' when !inBasic:
                    inLiteral = !inLiteral;
                    quoted = true;
                    break;
                case '.' when !inBasic && !inLiteral:
                    if (current.Length == 0)
                    {
                        throw new TomlException(lineNumber, $"key '{key}' has an empty segment");
                    }

                    parts.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length == 0)
        {
            throw new TomlException(lineNumber, $"key '{key}' has an empty segment");
        }

        parts.Add(current.ToString());
        return [.. parts.Select(p => NormalizeKey(p, quoted, lineNumber))];
    }

    private static string NormalizeKey(string raw, bool wasQuoted, int lineNumber)
    {
        if (wasQuoted)
        {
            return raw;
        }

        // Bare TOML keys are restricted; accepting anything here would let a manifest smuggle in a
        // key that a reader and a human would disagree about.
        foreach (char c in raw)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
            if (!ok)
            {
                throw new TomlException(
                    lineNumber,
                    $"bare key '{raw}' contains '{c}'; quote the key if that is intended");
            }
        }

        return raw;
    }

    private static object ParseValue(string text, TomlTable owner, int lineNumber)
    {
        if (text.Length == 0)
        {
            throw new TomlException(lineNumber, "assignment has no value");
        }

        if (text[0] == '[')
        {
            return ParseArray(text, owner, lineNumber);
        }

        if (text[0] == '{')
        {
            return ParseInlineTable(text, owner, lineNumber);
        }

        if (text.StartsWith("\"\"\"", StringComparison.Ordinal) || text.StartsWith("'''", StringComparison.Ordinal))
        {
            throw new TomlException(lineNumber, "multi-line strings are not supported in manifests");
        }

        if (text is "true" or "false")
        {
            return text == "true";
        }

        if (text.StartsWith('"') || text.StartsWith('\''))
        {
            return ParseString(text, lineNumber);
        }

        // Numbers. Reject anything with a date/time shape rather than silently parsing the head of it.
        if (LooksLikeDate(text))
        {
            throw new TomlException(lineNumber, $"dates and times are not supported: '{text}'");
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return number;
        }

        throw new TomlException(lineNumber, $"cannot parse value '{Truncate(text)}'");
    }

    private static bool LooksLikeDate(string text) =>
        text.Contains('-', StringComparison.Ordinal) && text.Count(c => c == '-') >= 2
        || text.Contains(':', StringComparison.Ordinal);

    private static string ParseString(string text, int lineNumber)
    {
        char quote = text[0];
        if (text.Length < 2 || text[^1] != quote)
        {
            throw new TomlException(lineNumber, "unterminated string");
        }

        string inner = text[1..^1];
        return quote == '\'' ? inner : Unescape(inner, lineNumber);
    }

    private static string Unescape(string text, int lineNumber = 0)
    {
        var builder = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (i + 1 >= text.Length)
            {
                throw new TomlException(lineNumber, "string ends with a dangling backslash");
            }

            char next = text[++i];
            switch (next)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u':
                    if (i + 4 >= text.Length || !ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                    {
                        throw new TomlException(lineNumber, "invalid \\u escape");
                    }

                    builder.Append((char)code);
                    i += 4;
                    break;
                default:
                    throw new TomlException(lineNumber, $"unsupported escape '\\{next}'");
            }
        }

        return builder.ToString();
    }

    private static List<object> ParseArray(string text, TomlTable owner, int lineNumber)
    {
        if (!text.EndsWith(']'))
        {
            throw new TomlException(lineNumber, "unterminated '['; the array is never closed");
        }

        string inner = text[1..^1].Trim();
        var items = new List<object>();

        if (inner.Length == 0)
        {
            return items;
        }

        foreach (string element in SplitTopLevel(inner, lineNumber))
        {
            items.Add(ParseValue(element.Trim(), owner, lineNumber));
        }

        return items;
    }

    private static TomlTable ParseInlineTable(string text, TomlTable owner, int lineNumber)
    {
        if (!text.EndsWith('}'))
        {
            throw new TomlException(lineNumber, "unterminated '{'; the inline table is never closed");
        }

        var table = new TomlTable();
        string inner = text[1..^1].Trim();

        if (inner.Length == 0)
        {
            return table;
        }

        foreach (string pair in SplitTopLevel(inner, lineNumber))
        {
            int equals = FindAssignment(pair.Trim());
            if (equals < 0)
            {
                throw new TomlException(lineNumber, $"inline table entry '{Truncate(pair)}' is not 'key = value'");
            }

            table.Set(SplitKey(pair.Trim()[..equals].Trim(), lineNumber), ParseValue(pair.Trim()[(equals + 1)..].Trim(), owner, lineNumber));
        }

        return table;
    }

    /// <summary>
    /// Splits on commas that are not inside quotes, brackets or braces. This is what lets
    /// <c>sources = [{ via = "tool", run = "a,b" }]</c> parse, where a naive Split(',') produces
    /// garbage that then fails with a confusing error further along.
    /// </summary>
    private static IReadOnlyList<string> SplitTopLevel(string text, int lineNumber)
    {
        // A multi-line value arrives with embedded newlines. TOML permits that whitespace inside
        // arrays and inline tables, so it is collapsed to single spaces here rather than rejected.
        text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();

        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool inBasic = false;
        bool inLiteral = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\' when inBasic:
                    current.Append(c);
                    if (i + 1 < text.Length)
                    {
                        current.Append(text[++i]);
                    }

                    break;
                case '"' when !inLiteral:
                    inBasic = !inBasic;
                    current.Append(c);
                    break;
                case '\'' when !inBasic:
                    inLiteral = !inLiteral;
                    current.Append(c);
                    break;
                case '[' or '{' when !inBasic && !inLiteral:
                    depth++;
                    current.Append(c);
                    break;
                case ']' or '}' when !inBasic && !inLiteral:
                    depth--;
                    if (depth < 0)
                    {
                        throw new TomlException(lineNumber, "unbalanced ']' or '}'");
                    }

                    current.Append(c);
                    break;
                case ',' when depth == 0 && !inBasic && !inLiteral:
                    parts.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (depth != 0)
        {
            throw new TomlException(lineNumber, "unbalanced '[' or '{'");
        }

        if (inBasic || inLiteral)
        {
            throw new TomlException(lineNumber, "unterminated string");
        }

        string tail = current.ToString();
        if (tail.Trim().Length > 0 || parts.Count == 0)
        {
            parts.Add(tail);
        }

        // A trailing comma is valid TOML and 10-formats' own example ends its `sources` array with
        // one. Without this, the empty segment after that comma is parsed as a value and the whole
        // manifest is rejected for a trailing comma — which is the spec's own example being refused.
        return parts;
    }

    /// <summary>A generous ceiling on how far a single value may span. Manifests are small.</summary>
    private const int MaxContinuationLines = 64;

    /// <summary>
    /// True when the value opens a bracket or brace it has not yet closed.
    /// <para>
    /// This must tolerate an empty rest-of-line after the opening bracket, which is the shape
    /// 10-formats itself uses: <c>sources = [</c> followed by one entry per line.
    /// </para>
    /// </summary>
    private static bool NeedsContinuation(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        return (value[0] == '[' || value[0] == '{') && !IsBalanced(value);
    }

    /// <summary>
    /// True when every bracket opened outside a string is closed. Counts only what is OUTSIDE quotes,
    /// so a path like <c>"C:\\{x}"</c> cannot fake a brace, and a comma inside a string cannot
    /// pretend an array continues.
    /// </summary>
    private static bool IsBalanced(string text)
    {
        int depth = 0;
        bool inBasic = false;
        bool inLiteral = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\' when inBasic:
                    i++;
                    break;
                case '"' when !inLiteral:
                    inBasic = !inBasic;
                    break;
                case '\'' when !inBasic:
                    inLiteral = !inLiteral;
                    break;
                case '[' or '{' when !inBasic && !inLiteral:
                    depth++;
                    break;
                case ']' or '}' when !inBasic && !inLiteral:
                    depth--;
                    break;
                default:
                    break;
            }
        }

        return depth <= 0 && !inBasic && !inLiteral;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                lines.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>Strips a trailing comment, respecting quotes so <c>path = "a # b"</c> survives.</summary>
    private static string StripComment(string line)
    {
        bool inBasic = false;
        bool inLiteral = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            switch (c)
            {
                case '\\' when inBasic:
                    i++;
                    break;
                case '"' when !inLiteral:
                    inBasic = !inBasic;
                    break;
                case '\'' when !inBasic:
                    inLiteral = !inLiteral;
                    break;
                case '#' when !inBasic && !inLiteral:
                    return line[..i];
                default:
                    break;
            }
        }

        return line;
    }

    private static string Truncate(string text) =>
        text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 37), "...");
}

using System.Globalization;

namespace Coppice.Manifests.Toml;

/// <summary>A TOML parse failure, carrying the line so the message can point at it (NFR-10).</summary>
public sealed class TomlException : Exception
{
    public TomlException(int lineNumber, string message)
        : base($"line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
        Detail = message;
    }

    public int LineNumber { get; }

    public string Detail { get; }
}

/// <summary>
/// A TOML table. Values are <see cref="string"/>, <see cref="double"/>, <see cref="bool"/>,
/// <see cref="List{T}"/> of those, or a nested <see cref="TomlTable"/>.
/// <para>
/// Lookup returns null for anything absent or wrongly typed rather than throwing. A validator that
/// has to distinguish "key missing" from "key present but null" will do that itself with
/// <see cref="TryGet"/>, which reports which of the two happened.
/// </para>
/// </summary>
public sealed class TomlTable
{
    private readonly Dictionary<string, object?> _values = [];
    private readonly Dictionary<string, TomlTable> _tables = [];
    private readonly Dictionary<string, List<TomlTable>> _tableArrays = [];

    /// <summary>True when this table was opened by a <c>[[header]]</c>. Blocks illegal nesting.</summary>
    internal bool IsLastArrayHeader { get; set; }

    public bool Contains(string key) => _values.ContainsKey(key) || _tables.ContainsKey(key) || _tableArrays.ContainsKey(key);

    /// <summary>All dotted keys present in this table, for reporting unknown keys.</summary>
    public IReadOnlyCollection<string> Keys => [.. _values.Keys, .. _tables.Keys, .. _tableArrays.Keys];

    internal void Set(IReadOnlyList<string> path, object? value)
    {
        TomlTable table = this;
        for (int i = 0; i < path.Count - 1; i++)
        {
            table = table.GetOrAddTable(path[i]);
        }

        table._values[path[^1]] = value;
    }

    internal TomlTable GetOrAddTable(string key)
    {
        if (_tables.TryGetValue(key, out TomlTable? existing))
        {
            return existing;
        }

        var created = new TomlTable();
        _tables[key] = created;
        return created;
    }

    internal TomlTable AddArrayOfTables(string key)
    {
        var created = new TomlTable();
        if (!_tableArrays.TryGetValue(key, out List<TomlTable>? list))
        {
            list = [];
            _tableArrays[key] = list;
        }

        list.Add(created);
        return created;
    }

    /// <summary>
    /// Returns the array-of-table entries for <paramref name="key"/>, creating nothing. Empty when the
    /// key is absent or is a plain table — the validator reports that as "no locations declared", which
    /// is a real problem, rather than as a missing key.
    /// </summary>
    /// <summary>
    /// Returns table entries for <paramref name="key"/> from either spelling TOML allows:
    /// <c>[[key]]</c> headers (stored as an array-of-table) and <c>key = [{...}]</c> inline arrays
    /// (stored as a plain array whose elements happen to be tables).
    /// <para>
    /// Both are needed because the two forms are not interchangeable to a reader: an inline array has
    /// no header, so a version that only read array-of-tables reported "declares no sources" for a
    /// manifest that plainly listed them.
    /// </para>
    /// </summary>
    public IReadOnlyList<TomlTable> GetTableArray(string key)
    {
        if (_tableArrays.TryGetValue(key, out List<TomlTable>? list))
        {
            return list;
        }

        if (_values.TryGetValue(key, out object? raw) && raw is List<object> inline)
        {
            var result = new List<TomlTable>(inline.Count);
            foreach (object? item in inline)
            {
                if (item is TomlTable table)
                {
                    result.Add(table);
                }
            }

            return result;
        }

        return [];
    }

    /// <summary>
    /// Returns a nested table. Looks in BOTH maps, because an inline table written as
    /// <c>fingerprint = { ... }</c> is stored as a VALUE while <c>[fingerprint]</c> is stored as a TABLE.
    /// An earlier version read only <c>_tables</c>, so every inline fingerprint block silently came back
    /// absent — no ratio check ran, and the location was treated as having no fingerprint at all.
    /// </summary>
    public TomlTable? GetTable(string key)
    {
        if (_tables.TryGetValue(key, out TomlTable? table))
        {
            return table;
        }

        return _values.TryGetValue(key, out object? raw) ? raw as TomlTable : null;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        if (!_values.TryGetValue(key, out object? raw) || raw is not T typed)
        {
            return false;
        }

        value = typed;
        return true;
    }

    /// <summary>True when the key exists at all, even if its value is null.</summary>
    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary>The raw value, whatever its type. Used by the validator to describe a mismatch precisely.</summary>
    public object? Raw(string key) => _values.GetValueOrDefault(key);

    /// <summary>The value as a string. Numbers and booleans are NOT coerced: a manifest that writes
    /// <c>id = 3</c> for a string field is an error the author needs to see, not something to accept quietly.</summary>
    public string? GetString(string key) => _values.TryGetValue(key, out object? raw) && raw is string s ? s : null;

    public double? GetNumber(string key) =>
        _values.TryGetValue(key, out object? raw) && raw is double d ? d : null;

    public bool? GetBool(string key) =>
        _values.TryGetValue(key, out object? raw) && raw is bool b ? b : null;

    /// <summary>Array elements as strings. Returns null if the value is not an array of strings.</summary>
    public IReadOnlyList<string>? GetStringArray(string key)
    {
        if (!_values.TryGetValue(key, out object? raw) || raw is not List<object> list)
        {
            return null;
        }

        var result = new List<string>(list.Count);
        foreach (object? item in list)
        {
            if (item is not string s)
            {
                return null;
            }

            result.Add(s);
        }

        return result;
    }

    /// <summary>
    /// Describes a value for an error message: quoted for strings, invariant for numbers, and a type
    /// name otherwise. Error text that says "expected number, got text" saves a round trip that
    /// "invalid value" does not.
    /// </summary>
    public string Describe(string key) => _values.TryGetValue(key, out object? raw)
        ? raw switch
        {
            null => "null",
            string s => $"string \"{(s.Length <= 30 ? s : s[..27] + "...")}\"",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "boolean true" : "boolean false",
            List<object> => "array",
            _ => raw.GetType().Name,
        }
        : "(missing)";
}

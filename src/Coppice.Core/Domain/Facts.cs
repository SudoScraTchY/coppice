namespace Coppice.Core.Domain;

/// <summary>
/// The opaque, plugin-populated fact bag. Values are primitives only (04-plugin-contract rule 6)
/// so a plugin can add data without the core knowing what it means, and so plans stay serializable.
/// A record, so an <see cref="Item"/> carrying a Facts round-trips with structural equality —
/// the value bag is a value, not an identity.
/// </summary>
public sealed record Facts : IReadOnlyDictionary<string, string>
{
    public static Facts Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Facts()
    {
    }

    public Facts(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var pair in values)
        {
            Add(pair.Key, pair.Value);
        }
    }

    public static Facts From(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new Facts(values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public string this[string key] => _values[key];

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(Facts? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._values.Count != _values.Count)
        {
            return false;
        }

        foreach (var pair in _values)
        {
            if (!other._values.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in _values)
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private void Add(string key, string value)
    {
        ValidateKey(key);
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), $"Fact '{key}' has a null value.");
        }

        _values[key] = value;
    }

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 0 || key.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Fact keys must be non-empty and contain no whitespace.", nameof(key));
        }

        // Identifier-like throughout, not just the first character: a ':' or '/' anywhere would
        // corrupt the colon-delimited reason/detail columns of the CSV and text reports.
        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    $"Fact key '{key}' must contain only ASCII letters, digits or underscores.", nameof(key));
            }
        }
    }
}

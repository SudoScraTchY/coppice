using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppice.Core.Domain;

/// <summary>
/// <see cref="Facts"/> is both a record (for value equality in a plan) and an
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>. System.Text.Json would otherwise pick the
/// collection converter and fail to bind it, so the shape is pinned here: a plain JSON object,
/// written in the same ordinal key order the constructor sorts to.
/// </summary>
public sealed class FactsJsonConverter : JsonConverter<Facts>
{
    public override Facts Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Facts must be a JSON object.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return Facts.From(values);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Malformed Facts object.");
            }

            var key = reader.GetString()!;
            reader.Read();
            values[key] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString()!,
                JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => string.Empty,
                _ => throw new JsonException($"Facts values must be primitives; got {reader.TokenType}."),
            };
        }

        throw new JsonException("Unterminated Facts object.");
    }

    public override void Write(Utf8JsonWriter writer, Facts value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        foreach (var pair in value)
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    public override Facts ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Facts value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

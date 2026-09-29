using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppice.Core.Domain;

/// <summary>
/// Single source of truth for how domain objects hit JSON. Determinism is a hard requirement
/// (NFR-06): sorted keys, no floating-point formatting drift, and a checksum that is a function of
/// the canonical bytes alone so a tampered plan cannot be applied (FR-11).
/// </summary>
public static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    public static JsonSerializerOptions PrettyOptions { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.General)
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new FactsJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? PrettyOptions : Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// SHA-256 over the canonical (non-indented) serialization, so the checksum is stable across
    /// formatting but changes if any field changes.
    /// </summary>
    public static string ComputeChecksum<T>(T value)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

/// <summary>
/// Deterministic ordering. Reports and plans are diffed by users and compared across runs, so
/// ties must never resolve by hash-bucket order.
/// </summary>
public static class Deterministic
{
    public static IReadOnlyList<Item> OrderItems(IEnumerable<Item> items) =>
        [.. items
            .OrderBy(i => i.Ecosystem, StringComparer.Ordinal)
            .ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Version, StringComparer.Ordinal)
            .ThenBy(i => i.LocationId, StringComparer.Ordinal)
            .ThenBy(i => i.Path, StringComparer.Ordinal)];

    public static IReadOnlyList<PlanStep> OrderSteps(IEnumerable<PlanStep> steps) =>
        [.. steps
            .OrderBy(s => s.Risk)
            .ThenBy(s => s.ItemId, StringComparer.Ordinal)];

    public static IReadOnlyList<ResolvedRoot> OrderRoots(IEnumerable<ResolvedRoot> roots) =>
        [.. roots
            .OrderBy(r => r.Role)
            .ThenBy(r => r.RealPath, StringComparer.Ordinal)
            .ThenBy(r => r.DeclaredPath, StringComparer.Ordinal)];

    public static IReadOnlyList<Problem> OrderProblems(IEnumerable<Problem> problems) =>
        [.. problems
            .OrderBy(p => p.Ecosystem, StringComparer.Ordinal)
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .ThenBy(p => p.Path, StringComparer.Ordinal)
            .ThenBy(p => p.Summary, StringComparer.Ordinal)];

    public static IReadOnlyList<ScanIssue> OrderIssues(IEnumerable<ScanIssue> issues) =>
        [.. issues
            .OrderBy(i => i.LocationId, StringComparer.Ordinal)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ThenBy(i => i.Path, StringComparer.Ordinal)];
}

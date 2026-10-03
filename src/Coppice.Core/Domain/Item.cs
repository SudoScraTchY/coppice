namespace Coppice.Core.Domain;

/// <summary>
/// One inventory entry. <see cref="Ecosystem"/> and <see cref="Kind"/> are open string ids on purpose
/// (OCP, 03-architecture notes): the core must never need an edit to learn a new language.
/// </summary>
public sealed record Item(
    string Ecosystem,
    string Kind,
    string Name,
    string Version,
    string LocationId,
    string Path,
    ulong Size,
    Risk Risk,
    Facts Facts)
{
    /// <summary>Stable identity used as the plan step key; deterministic across runs (NFR-06).</summary>
    public string ItemId => DeterministicKey($"{Ecosystem}\u001f{Kind}\u001f{Name}\u001f{Version}\u001f{LocationId}\u001f{Path}");

    private static string DeterministicKey(string raw)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw), hash);
        return System.Convert.ToHexStringLower(hash[..8]);
    }
}

public sealed record ResolvedRoot(
    string DeclaredPath,
    string RealPath,
    RootRole Role,
    ResolvedVia Via,
    string? ViaDetail,
    RootValidity Validity,
    string? Owner,
    RootConfidence Confidence = RootConfidence.Full,
    string? ConfidenceDetail = null)
{
    public bool IsCleanable => Validity == RootValidity.Ok;

    /// <summary>True when this root was located without asking a tool, and the report should say so.</summary>
    public bool IsLowConfidence => Confidence == RootConfidence.Lowered;

    public static ResolvedRoot Reject(string declaredPath, RootValidity validity, string? detail = null) =>
        new(declaredPath, declaredPath, RootRole.Inactive, ResolvedVia.Default, detail, validity, null);
}

/// <summary>
/// How much the report should trust WHERE a root came from (09-ecosystems, rust section).
/// <para>
/// This is not a risk tier and it does not gate cleaning. It answers a narrower question: did we ask a
/// tool, or did we guess from an environment variable and a convention? Cargo has no query command at
/// all, so its registry is located by convention alone — a machine with an unusual <c>CARGO_HOME</c>
/// silently defaults somewhere harmless but wrong. The report has to be able to say that, because the
/// alternative is a user who believes a location was found when it was guessed.
/// </para>
/// </summary>
public enum RootConfidence
{
    /// <summary>A tool or a pin confirmed the path.</summary>
    Full = 0,

    /// <summary>Resolved from convention, environment or config with no confirming tool.</summary>
    Lowered = 1,
}

public sealed record ScanIssue(string LocationId, string Code, string Summary, string? Path);

public sealed record LocationScan(
    string LocationId,
    IReadOnlyList<ResolvedRoot> Roots,
    IReadOnlyList<Item> Items,
    IReadOnlyList<ScanIssue> Issues);

/// <summary>
/// Bytes per risk tier, for report headers and trend history. Named <c>UsageBreakdown</c> because
/// <c>Usage</c> is the three-state reference enum fixed by 03-architecture.
/// </summary>
public sealed record UsageBreakdown(ulong SafeBytes, ulong ReviewBytes, ulong ManualBytes)
{
    public static UsageBreakdown Zero { get; } = new(0, 0, 0);

    public ulong Total => SafeBytes + ReviewBytes + ManualBytes;

    public ulong this[Risk risk] => risk switch
    {
        Risk.Safe => SafeBytes,
        Risk.Review => ReviewBytes,
        Risk.Manual => ManualBytes,
        _ => 0,
    };

    public static UsageBreakdown FromItems(IEnumerable<Item> items)
    {
        ulong @safe = 0, review = 0, manual = 0;
        foreach (var item in items)
        {
            switch (item.Risk)
            {
                case Risk.Safe: @safe += item.Size; break;
                case Risk.Review: review += item.Size; break;
                default: manual += item.Size; break;
            }
        }

        return new UsageBreakdown(@safe, review, manual);
    }
}

namespace Coppice.Plugins.Net;

/// <summary>
/// A parsed .NET version: the parts the tool actually reasons about.
/// <para>
/// The feature band (<see cref="Major"/>.<see cref="Minor"/>) is what decides whether an SDK can
/// satisfy a <c>global.json</c> pin (S-4), and whether a preview has been superseded by a GA
/// release of the same band. Both are safety- or correctness-relevant, so they are parsed properly
/// rather than pattern-matched at each call site.
/// </para>
/// </summary>
public sealed record DotNetVersion : IComparable<DotNetVersion>
{
    public required int Major { get; init; }

    public required int Minor { get; init; }

    public required int Patch { get; init; }

    /// <summary>The fourth component, when present (<c>10.0.1.2</c>).</summary>
    public int Revision { get; init; }

    /// <summary>True for any preview, RC, or pre-release build.</summary>
    public required bool IsPreview { get; init; }

    /// <summary>The pre-release label without the leading dash, e.g. <c>preview.1.25100.1</c>.</summary>
    public string? PreRelease { get; init; }

    /// <summary>The <c>Major.Minor</c> feature band, e.g. <c>10.0</c>.</summary>
    public string Band => $"{Major}.{Minor}";

    public override string ToString() =>
        Revision > 0
            ? $"{Major}.{Minor}.{Patch}.{Revision}{Suffix()}"
            : $"{Major}.{Minor}.{Patch}{Suffix()}";

    private string Suffix() => string.IsNullOrEmpty(PreRelease) ? string.Empty : "-" + PreRelease;

    /// <summary>
    /// Parses a version as printed by <c>dotnet --list-sdks</c>. Returns null rather than throwing:
    /// a single unparseable line must not abort a scan, and an SDK we cannot understand must not be
    /// silently treated as current.
    /// </summary>
    public static DotNetVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();

        // Split off the pre-release label at the first '-'.
        string core = trimmed;
        string? preRelease = null;
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            core = trimmed[..dash];
            preRelease = trimmed[(dash + 1)..];
        }

        // Strip any build metadata (+sha).
        int plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        string[] parts = core.Split('.', StringSplitOptions.None);
        if (parts.Length is < 2 or > 4)
        {
            return null;
        }

        Span<int> numbers = stackalloc int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
            {
                return null;
            }
        }

        return new DotNetVersion
        {
            Major = numbers[0],
            Minor = numbers[1],
            Patch = numbers[2],
            Revision = parts.Length > 3 ? numbers[3] : 0,
            // "rc" and "preview" are both pre-release; "rtm" and a GA build are not.
            IsPreview = !string.IsNullOrEmpty(preRelease),
            PreRelease = string.IsNullOrEmpty(preRelease) ? null : preRelease,
        };
    }

    /// <summary>
    /// Total order for release comparison. GA sorts above any preview of the same version, and
    /// pre-releases sort BELOW the GA they lead to — which is what makes "preview superseded by GA"
    /// computable (04-plugin-contract rule 5 requires a total order).
    /// </summary>
    public int CompareTo(DotNetVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        result = Revision.CompareTo(other.Revision);
        if (result != 0)
        {
            return result;
        }

        // Both previews or both GA: compare the labels so ordering is total and stable.
        if (IsPreview && other.IsPreview)
        {
            return string.CompareOrdinal(PreRelease ?? string.Empty, other.PreRelease ?? string.Empty);
        }

        // GA outranks any preview of the same numeric version.
        return IsPreview ? -1 : other.IsPreview ? 1 : 0;
    }

    public static bool operator <(DotNetVersion a, DotNetVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(DotNetVersion a, DotNetVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(DotNetVersion a, DotNetVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(DotNetVersion a, DotNetVersion b) => a.CompareTo(b) >= 0;

    /// <summary>True when <paramref name="minimum"/> satisfies a <c>global.json</c> rollForward pin.</summary>
    public bool Satisfies(DotNetVersion minimum, string rollForward)
    {
        ArgumentNullException.ThrowIfNull(minimum);

        if (this < minimum)
        {
            return false;
        }

        if (string.IsNullOrEmpty(rollForward) || rollForward.Equals("latestPatch", StringComparison.OrdinalIgnoreCase))
        {
            return Band == minimum.Band;
        }

        if (rollForward.Equals("latestFeature", StringComparison.OrdinalIgnoreCase))
        {
            return Major == minimum.Major;
        }

        if (rollForward.Equals("major", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "disable" and anything unrecognised: an SDK that is not the pinned one cannot satisfy it.
        return this.Major == minimum.Major
            && this.Minor == minimum.Minor
            && this.Patch == minimum.Patch
            && this.IsPreview == minimum.IsPreview;
    }
}

using System.Globalization;

namespace Coppice.Plugins.Net;

/// <summary>An SDK as reported by <c>dotnet --list-sdks</c>.</summary>
public sealed record SdkInfo(string Version, string InstallPath)
{
    /// <summary>Feature band: the middle component, <c>9.0.300</c> → <c>0.300</c>. This is what
    /// "latest patch in band" retention keys on, and what the preview-superseded health check uses.</summary>
    public string Band => SdkVersion.BandOf(Version);

    /// <summary>Major version, <c>9.0.300</c> → <c>9</c>.</summary>
    public string Major => SdkVersion.MajorOf(Version);

    /// <summary>
    /// True for a pre-release (<c>7.0.100-preview.7</c>). 09's health check flags "preview SDK
    /// superseded by GA of the same feature band", which is meaningless without this flag.
    /// </summary>
    public bool IsPreview => SdkVersion.IsPreview(Version);

    /// <summary>Architecture inferred from the install path; <c>Unknown</c> when not determinable.</summary>
    public string Architecture => SdkVersion.ArchitectureOf(InstallPath);
}

/// <summary>A runtime as reported by <c>dotnet --list-runtimes</c>.</summary>
public sealed record RuntimeInfo(string Name, string Version, string InstallPath)
{
    public string Major => SdkVersion.MajorOf(Version);
}

/// <summary>
/// The version arithmetic the SDK health checks and retention policy depend on.
/// <para>
/// These are NOT SemVer, and treating them as such is the bug this type exists to prevent. A .NET
/// version is <c>major.minor.patch</c> plus an optional <c>-prerelease.build</c> tail, and
/// <c>10.0.100-preview.1</c> sorts BEFORE <c>10.0.100</c> — the opposite of a naive string
/// compare, which is how a tool ends up "updating" to a version older than the one installed.
/// </para>
/// </summary>
public static class SdkVersion
{
    /// <summary>Feature band, e.g. <c>9.0.300</c> → <c>0.300</c>.</summary>
    public static string BandOf(string version)
    {
        Parsed parsed = Parse(version);
        return parsed.Major is null ? string.Empty : $"{parsed.Minor}.{parsed.Patch}";
    }

    /// <summary>Major component, e.g. <c>9.0.300</c> → <c>9</c>.</summary>
    public static string MajorOf(string version) => Parse(version).Major ?? string.Empty;

    /// <summary>True when the version carries a <c>-</c> tail.</summary>
    public static bool IsPreview(string version)
    {
        Parsed parsed = Parse(version);
        return parsed.Preview is not null;
    }

    /// <summary>
    /// Total order over .NET versions. A release outranks a preview of the same numeric version, and
    /// a longer preview build number outranks a shorter one.
    /// </summary>
    public static int Compare(string a, string b)
    {
        Parsed left = Parse(a);
        Parsed right = Parse(b);

        int result = Numeric(left.Major).CompareTo(Numeric(right.Major));
        if (result != 0)
        {
            return result;
        }

        result = Numeric(left.Minor).CompareTo(Numeric(right.Minor));
        if (result != 0)
        {
            return result;
        }

        result = Numeric(left.Patch).CompareTo(Numeric(right.Patch));
        if (result != 0)
        {
            return result;
        }

        bool leftPreview = left.Preview is not null;
        bool rightPreview = right.Preview is not null;

        // A release beats a preview at the same numeric version: 10.0.100 > 10.0.100-preview.1
        if (leftPreview != rightPreview)
        {
            return leftPreview ? -1 : 1;
        }

        if (!leftPreview)
        {
            return 0;
        }

        return ComparePreRelease(left.Preview!, right.Preview!);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="baseline"/>.</summary>
    public static bool IsNewerThan(string candidate, string baseline) => Compare(candidate, baseline) > 0;

    /// <summary>Try to parse a version string into components; returns null if unrecognized.</summary>
    public static bool TryParse(string version, out Parsed parsed)
    {
        try
        {
            parsed = Parse(version);
            return parsed.Major != null && parsed.Minor != null && parsed.Patch != null;
        }
        catch
        {
            parsed = new Parsed(null, null, null, null);
            return false;
        }
    }

    /// <summary>
    /// Architecture from an install path. Recognizes the folder names a .NET installer actually
    /// uses; returns <c>unknown</c> rather than guessing when the path says nothing.
    /// </summary>
    public static string ArchitectureOf(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return "unknown";
        }

        if (installPath.Contains("arm64", StringComparison.OrdinalIgnoreCase)
            || installPath.Contains("aarch64", StringComparison.OrdinalIgnoreCase))
        {
            return "arm64";
        }

        if (installPath.Contains("x86", StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }

        if (installPath.Contains("x64", StringComparison.OrdinalIgnoreCase)
            || installPath.Contains("amd64", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }

        // An unqualified path on Windows is x64 for .NET 5+; elsewhere it is the host arch. Saying
        // "unknown" is the honest answer when the path does not say.
        return "unknown";
    }

    private static int ComparePreRelease(string a, string b)
    {
        // Numeric parts compare numerically, text parts lexically, and a longer tail wins, which
        // matches NuGet's own ordering: preview.2 > preview.1, preview.1.1 > preview.1.
        string[] left = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] right = b.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length)
            {
                return -1;
            }

            if (i >= right.Length)
            {
                return 1;
            }

            bool leftNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out int leftValue);
            bool rightNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out int rightValue);

            int result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftValue.CompareTo(rightValue),
                (true, false) => 1,
                (false, true) => -1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static int Numeric(string? part) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;

    public readonly record struct Parsed(string? Major, string? Minor, string? Patch, string? Preview);

    /// <summary>
    /// Splits a .NET version into its numeric core and its pre-release tail, discarding build
    /// metadata after '+' (SemVer treats 9.0.100+sha.abc and 9.0.100 as the same version, and so
    /// does NuGet).
    /// </summary>
    private static Parsed Parse(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new Parsed(null, null, null, null);
        }

        string core = version.Trim();
        string? preview = null;

        int dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preview = core[(dash + 1)..];
            core = core[..dash];
        }

        core = core.Split('+', 2)[0];
        preview = preview?.Split('+', 2)[0];

        string[] parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);

        return new Parsed(
            parts.Length > 0 ? parts[0] : null,
            parts.Length > 1 ? parts[1] : null,
            parts.Length > 2 ? parts[2] : null,
            preview is { Length: > 0 } ? preview : null);
    }
}

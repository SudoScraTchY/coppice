using Coppice.Core.PathSafety;
using Coppice.Ports;
using FsCheck.Fluent;
using Xunit;

namespace Coppice.Tests.Safety;

/// <summary>
/// The escape suite for PATH SAFETY (T-028, 11-testing E-3…E-6, E-8, E-9).
/// <para>
/// The invariant, stated once: <b>no candidate ever resolves outside its root</b>. Not "the known attacks
/// are detected" — every generated path, however bizarre, either stays inside the root or is refused
/// outright. A check that rejects only a fixed list of spellings is a lookup table, and this suite is what
/// distinguishes a check from a lookup table.
/// </para>
/// <para>
/// The suite must also not OVER-refuse. Several tests assert that legitimate names survive, because a
/// canonicalizer that refuses "..hidden", or a colon on Linux, is just as broken as one that lets "../.."
/// through: it silently skips real cache directories and reports an empty machine.
/// </para>
/// </summary>
public sealed class PathSafetyEscapeTests
{
    private static OperatingSystemKind Os(string name) =>
        Enum.Parse<OperatingSystemKind>(name, ignoreCase: true);

    private static string Root(OperatingSystemKind os) =>
        os == OperatingSystemKind.Windows ? @"C:\cache" : "/cache";

    /// <summary>
    /// Strict containment by SEGMENT. A raw prefix test would call "/cacheevil" contained in "/cache" —
    /// the classic sibling-directory escape, and the reason this helper never compares raw prefixes.
    /// </summary>
    private static bool IsInside(string candidate, string root, OperatingSystemKind os)
    {
        if (!PathCanonicalizer.IsRooted(candidate, os))
        {
            return false;
        }

        if (PathCanonicalizer.PathsEqual(candidate, root, os))
        {
            return false;
        }

        char sep = PathCanonicalizer.SeparatorFor(os);
        string prefix = root.EndsWith(sep) ? root : root + sep;

        return candidate.StartsWith(prefix, PathCanonicalizer.ComparisonFor(os));
    }

    // ---- E-3: parent traversal ----------------------------------------------------------------------

    [Fact]
    public void E3_A_traversal_segment_never_produces_a_path_outside_the_root()
    {
        PropertyRunner.Run(
            nameof(E3_A_traversal_segment_never_produces_a_path_outside_the_root),
            PropertyRunner.OperatingSystemName(),
            HostileNames.TraversalSegments(),
            (osName, traversal) =>
            {
                OperatingSystemKind os = Os(osName);
                string root = Root(os);
                char sep = PathCanonicalizer.SeparatorFor(os);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(root, traversal + sep + "innocent", os),
                    os);

                // Either canonicalization refuses, or what comes back is still inside the root. There is
                // no third option where it "succeeds" somewhere else.
                return !result.Ok || IsInside(result.Path, root, os);
            });
    }

    [Fact]
    public void E3_Traversal_past_the_filesystem_root_is_refused_or_stays_contained()
    {
        PropertyRunner.Run(
            nameof(E3_Traversal_past_the_filesystem_root_is_refused_or_stays_contained),
            PropertyRunner.OperatingSystemName(),
            HostileNames.TraversalSegments(),
            (osName, traversal) =>
            {
                OperatingSystemKind os = Os(osName);

                // Built from the filesystem root, so the ".." run reaches past it — which is where a
                // hand-rolled collapse can silently produce something that still parses.
                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    traversal + "/" + traversal + "/" + traversal,
                    os);

                return !result.Ok || IsInside(result.Path, Root(os), os);
            });
    }

    [Fact]
    public void E3_A_benign_dot_segment_survives_canonicalization()
    {
        PropertyRunner.Run(
            nameof(E3_A_benign_dot_segment_survives_canonicalization),
            PropertyRunner.OperatingSystemName(),
            HostileNames.BenignDotSegments(),
            (osName, segment) =>
            {
                OperatingSystemKind os = Os(osName);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(Root(os), segment, os),
                    os);

                // "..hidden" and "..." are ordinary names. Refusing them as suspicious would mean refusing
                // to scan real caches.
                return result.Ok && IsInside(result.Path, Root(os), os);
            });
    }

    // ---- E-4: case collision ------------------------------------------------------------------------

    [Fact]
    public void E4_Case_comparison_matches_what_the_filesystem_would_do()
    {
        PropertyRunner.Run(
            nameof(E4_Case_comparison_matches_what_the_filesystem_would_do),
            PropertyRunner.OperatingSystemName(),
            HostileNames.CaseVariants(),
            (osName, name) =>
            {
                OperatingSystemKind os = Os(osName);

                CanonicalResult lower = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(Root(os), name.ToLowerInvariant(), os), os);
                CanonicalResult upper = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(Root(os), name.ToUpperInvariant(), os), os);

                if (!lower.Ok || !upper.Ok)
                {
                    return true;
                }

                bool equal = PathCanonicalizer.PathsEqual(lower.Path, upper.Path, os);

                // On a case-insensitive FS the two MUST compare equal: otherwise a check could approve the
                // safe spelling and the OS would act on the other one. On a case-sensitive FS they must
                // not, or two genuinely different directories become one.
                return os == OperatingSystemKind.Linux
                    ? !string.Equals(lower.Path, upper.Path, StringComparison.OrdinalIgnoreCase)
                    : equal;
            });
    }

    [Fact]
    public void E4_Case_folding_never_makes_a_path_leave_the_root()
    {
        PropertyRunner.Run(
            nameof(E4_Case_folding_never_makes_a_path_leave_the_root),
            PropertyRunner.OperatingSystemName(),
            HostileNames.CaseVariants(),
            (osName, name) =>
            {
                OperatingSystemKind os = Os(osName);

                string folded = PathCanonicalizer.FoldCase(PathCanonicalizer.Combine(Root(os), name, os), os);
                CanonicalResult result = PathCanonicalizer.Canonicalize(folded, os);

                return !result.Ok || IsInside(result.Path, Root(os), os);
            });
    }

    // ---- E-5: trailing dots and spaces ---------------------------------------------------------------

    [Fact]
    public void E5_Trailing_dots_and_spaces_are_refused_on_windows()
    {
        // Deterministic rather than a property: the invariant is about ONE OS, and enumerating the
        // fragment space directly says so. The Windows kernel strips trailing dots and spaces, so "pkg"
        // and "pkg." are the SAME directory there and two different names everywhere else — exactly the
        // gap a string comparison leaves open.
        foreach (string stem in new[] { "pkg", "cache", "packages", "1.0.0" })
        {
            foreach (int dots in new[] { 1, 2, 3 })
            {
                foreach (int spaces in new[] { 0, 1, 2 })
                {
                    string hostile = stem + new string('.', dots) + new string(' ', spaces);

                    CanonicalResult result = PathCanonicalizer.Canonicalize(
                        PathCanonicalizer.Combine(@"C:\cache", hostile, OperatingSystemKind.Windows),
                        OperatingSystemKind.Windows);

                    Assert.False(
                        result.Ok && result.Reason != CanonicalFailure.TrailingDotOrSpace,
                        $"'{hostile}' should be refused as trailing dot/space but came back as {result.Reason}");
                }
            }
        }
    }

    [Fact]
    public void E5_Trailing_dots_and_spaces_are_legal_names_on_unix()
    {
        // The other half. Refusing them on Linux or macOS would mean silently skipping real cache
        // directories on those platforms and reporting an empty machine.
        PropertyRunner.Run(
            nameof(E5_Trailing_dots_and_spaces_are_legal_names_on_unix),
            PropertyRunner.UnixName(),
            HostileNames.TrailingDotOrSpace(),
            (osName, hostile) =>
            {
                OperatingSystemKind os = Os(osName);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(Root(os), hostile, os), os);

                return result.Ok && IsInside(result.Path, Root(os), os);
            });
    }

    // ---- E-6: alternate data streams ----------------------------------------------------------------

    [Fact]
    public void E6_An_ads_style_name_is_refused_on_windows()
    {
        PropertyRunner.Run(
            nameof(E6_An_ads_style_name_is_refused_on_windows),
            Gen.Elements("Windows"),
            HostileNames.AdsNames(),
            (_, ads) =>
            {
                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(@"C:\cache", ads, OperatingSystemKind.Windows),
                    OperatingSystemKind.Windows);

                return !result.Ok && result.Reason == CanonicalFailure.AdsNotation;
            });
    }

    [Fact]
    public void E6_A_colon_in_the_drive_letter_is_not_an_ads()
    {
        // The drive letter is the one legitimate colon. Refusing it would refuse every Windows path.
        CanonicalResult result = PathCanonicalizer.Canonicalize(@"C:\cache\pkg", OperatingSystemKind.Windows);

        Assert.True(result.Ok, result.Message);
        Assert.NotEqual(CanonicalFailure.AdsNotation, result.Reason);
    }

    [Fact]
    public void E6_A_colon_is_not_refused_on_unix()
    {
        // Colons are ordinary filename characters on Linux and macOS. Treating them as ADS there would
        // refuse real package directories.
        PropertyRunner.Run(
            nameof(E6_A_colon_is_not_refused_on_unix),
            PropertyRunner.UnixName(),
            HostileNames.AdsNames(),
            (osName, ads) =>
            {
                OperatingSystemKind os = Os(osName);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(Root(os), ads, os), os);

                return result.Ok && IsInside(result.Path, Root(os), os);
            });
    }

    // ---- E-8: long paths ------------------------------------------------------------------------------

    [Fact]
    public void E8_A_long_path_is_canonicalized_intact_or_refused()
    {
        PropertyRunner.Run(
            nameof(E8_A_long_path_is_canonicalized_intact_or_refused),
            PropertyRunner.OperatingSystemName(),
            HostileNames.LongSegments(),
            (osName, segment) =>
            {
                OperatingSystemKind os = Os(osName);

                string longPath = PathCanonicalizer.Combine(Root(os), segment, os);
                CanonicalResult result = PathCanonicalizer.Canonicalize(longPath, os);

                if (!result.Ok)
                {
                    // Refusal is acceptable, but it must be a STATED refusal, not a truncated string.
                    return result.Reason != CanonicalFailure.None && result.Message.Length > 0;
                }

                // If it succeeded the path must be INTACT. A canonicalizer that silently trimmed would
                // hand back a path naming a different directory than the one it checked.
                return result.Path.Contains(segment, StringComparison.Ordinal)
                    && IsInside(result.Path, Root(os), os);
            });
    }

    [Fact]
    public void E8_The_long_path_form_and_the_plain_form_name_the_same_directory()
    {
        // Two spellings of one directory must not become two roots, or a de-duplication check comparing
        // them would miss and plan a step twice against the same directory.
        PropertyRunner.Run(
            nameof(E8_The_long_path_form_and_the_plain_form_name_the_same_directory),
            Gen.Elements("Windows"),
            HostileNames.LongSegments(),
            (_, segment) =>
            {
                string plain = PathCanonicalizer.Combine(@"C:\cache", segment, OperatingSystemKind.Windows);
                string longForm = PathCanonicalizer.ToLongPathForm(plain, OperatingSystemKind.Windows);

                if (!PathCanonicalizer.HasLongPathPrefix(longForm))
                {
                    return false;
                }

                CanonicalResult a = PathCanonicalizer.Canonicalize(plain, OperatingSystemKind.Windows);
                CanonicalResult b = PathCanonicalizer.Canonicalize(longForm, OperatingSystemKind.Windows);

                return a.Ok == b.Ok
                    && (!a.Ok || PathCanonicalizer.PathsEqual(a.Path, b.Path, OperatingSystemKind.Windows));
            });
    }

    // ---- E-9: Unicode normalization -------------------------------------------------------------------

    [Fact]
    public void E9_An_nfc_nfd_pair_is_recognised_as_a_collision()
    {
        // Written with EXPLICIT unicode escapes. Two literals that merely LOOK different in an editor are
        // byte-identical once the source file is normalized — which would make this test assert a
        // collision between a string and itself, i.e. assert nothing. The escapes pin the exact code
        // points, so the test means what it says on every machine and every editor.
        const string Precomposed = "caf\u00E9";      // U+00E9 LATIN SMALL LETTER E WITH ACUTE
        const string Decomposed = "cafe\u0301";      // "cafe" + U+0301 COMBINING ACUTE ACCENT

        Assert.NotEqual(Precomposed, Decomposed);
        Assert.Equal(Precomposed.Length + 1, Decomposed.Length);

        foreach (OperatingSystemKind os in Enum.GetValues<OperatingSystemKind>())
        {
            bool collision = PathCanonicalizer.HasNfcNfdCollision(Precomposed, [Decomposed], os);

            // Folding follows the FILESYSTEM, not the OS name: Linux ext4 and APFS both normalize, NTFS
            // does not. Windows must report no collision — a false positive there would make a scan
            // refuse two genuinely different Windows paths. Asserted per OS rather than assumed, because
            // "it returns something truthy" is not the property being defended.
            bool shouldCollide = os is OperatingSystemKind.Linux or OperatingSystemKind.MacOS;

            Assert.Equal(shouldCollide, collision);
        }
    }

    [Fact]
    public void E9_A_unicode_name_never_escapes_the_root_by_normalization()
    {
        PropertyRunner.Run(
            nameof(E9_A_unicode_name_never_escapes_the_root_by_normalization),
            PropertyRunner.OperatingSystemName(),
            HostileNames.UnicodeVariants(),
            (osName, name) =>
            {
                OperatingSystemKind os = Os(osName);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(Root(os), name, os), os);

                return !result.Ok || IsInside(result.Path, Root(os), os);
            });
    }
}

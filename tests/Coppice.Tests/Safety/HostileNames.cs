using FsCheck;
using FsCheck.Fluent;

namespace Coppice.Tests.Safety;

/// <summary>
/// Generators for the escape suite (T-028, FR-13, NFR-01, 11-testing E-1…E-9 + E-11).
/// <para>
/// The generators live in their own file and return STRINGS, not filesystem graphs. Each produces one
/// hostile path fragment, and the tests compose them into a fake VFS. Keeping them separate means the
/// same fragment is available to a shrinking reproducer without dragging the harness along, and it is
/// the fragment — a small string — that FsCheck can shrink to something a human can read.
/// </para>
/// <para>
/// Nothing here produces an "interesting" path by chance. Every generator draws from a closed set of
/// fragments seen in real attacks, so a failure names a specific technique rather than "some string".
/// </para>
/// </summary>
public static class HostileNames
{
    // ---- E-3: parent traversal ----------------------------------------------------------------------

    /// <summary>
    /// Traversal spellings. Every one of these must either normalize to a contained path or be refused —
    /// the point of E-3 is that ".." is spelled more than one way and a filter that handles only the
    /// obvious one is not a filter.
    /// </summary>
    public static Gen<string> TraversalSegments() =>
        Gen.Elements("..", "../", "..\\", "..%2f", "..%5c", "%2e%2e", "%2e%2e%2f", "...", "./..", "a/../..");

    /// <summary>Dot segments that are NOT traversal and must survive intact.</summary>
    public static Gen<string> BenignDotSegments() =>
        Gen.Elements(".", "..hidden", "...", "a.b", "..a", "a..", "...");

    // ---- E-4: case collision ------------------------------------------------------------------------

    /// <summary>Case variants of the same name. On a case-insensitive FS these are one directory.</summary>
    public static Gen<string> CaseVariants() =>
        from bool upper in Gen.Elements(true, false)
        from string stem in Gen.Elements("cache", "Packages", "NUGET", "node_modules", "ModCache")
        select upper ? stem.ToUpperInvariant() : stem.ToLowerInvariant();

    // ---- E-5: trailing dots and spaces --------------------------------------------------------------

    /// <summary>
    /// Trailing dots and spaces. The Windows kernel strips them, so "pkg" and "pkg." are the SAME
    /// directory to the OS while being different strings to any check that compares them.
    /// </summary>
    public static Gen<string> TrailingDotOrSpace() =>
        from int dots in Gen.Elements(1, 2, 3)
        from int spaces in Gen.Elements(0, 1, 2)
        from string stem in Gen.Elements("pkg", "cache", "packages", "1.0.0")
        select stem + new string('.', dots) + new string(' ', spaces);

    // ---- E-6: alternate data streams ----------------------------------------------------------------

    /// <summary>ADS-style names: a colon that is not a drive letter.</summary>
    public static Gen<string> AdsNames() =>
        from string stem in Gen.Elements("pkg", "cache", "1.0.0", "data")
        from string stream in Gen.Elements("hidden", "$DATA", "zone.Identifier", "a:b")
        select $"{stem}:{stream}";

    // ---- E-8: long paths ------------------------------------------------------------------------------

    /// <summary>Path components long enough to cross the 260-character legacy Windows limit.</summary>
    public static Gen<string> LongSegments() =>
        Gen.Elements(200, 240, 255, 260, 300, 400)
            .Select(n => new string('a', n));

    // ---- E-9: Unicode normalization -------------------------------------------------------------------

    /// <summary>
    /// NFC and NFD spellings of the same visual name. macOS's HFS+ treats them as distinct directory
    /// entries, so "café" written two ways is two directories a user cannot tell apart.
    /// <para>
    /// Written with explicit escapes for the same reason as the E-9 test: source normalization would
    /// silently collapse both literals into one, and the generator would then produce a set with no
    /// collision in it at all — passing without ever testing anything.
    /// </para>
    /// </summary>
    public static Gen<string> UnicodeVariants() =>
        from string form in Gen.Elements(
            "caf\u00E9", "cafe\u0301",         // NFC / NFD café
            "\u00C5", "A\u030A",               // NFC / NFD Å
            "ma\u00F1ana", "man\u0303ana")     // NFC / NFD mañana
        from string shape in Gen.Elements("-1.0", "-pkg", "x", "")
        select form + shape;

    // ---- E-1/E-2: link shapes --------------------------------------------------------------------------

    /// <summary>Link names, including ones that look like directories but are not.</summary>
    public static Gen<string> LinkNames() =>
        Gen.Elements("link", "escape", "up", "self", "cycle", "junction", "far", "deep", ".", "..");

    /// <summary>Link targets: absolute escapes, relative escapes, and innocent same-root links.</summary>
    public static Gen<string> LinkTargets(string root, string outside) =>
        from int pick in Gen.Elements(0, 1, 2, 3, 4)
        select pick switch
        {
            0 => "/etc",
            1 => root + "/../../.." + outside,
            2 => "../../../../etc",
            3 => root + "/inner",
            _ => outside,
        };

    /// <summary>
    /// What a poisoned tool might answer when asked where its cache is.
    /// <para>
    /// The filesystem root and the home directory are the two answers that matter most, because they are
    /// the ones a denylist must refuse: both are real directories that a naive resolver would accept as a
    /// cache root, and both would turn "clean my Go module cache" into "clean the filesystem".
    /// </para>
    /// </summary>
    public static Gen<string> PoisonedToolAnswers() =>
        Gen.Elements(
            "/",
            "/usr",
            "/usr/bin",
            "/usr/share",
            "/etc",
            "/var",
            "/bin",
            "/home/dev",
            "/root",
            "/opt",
            // Junk that a tool might emit when it is confused or hostile. Not a path at all — which must
            // be refused rather than canonicalized into something that happens to exist.
            "not-a-path",
            "",
            "   ",
            "C:\\",
            "\\\\server\\share",
            ".");

    // ---- separator and whitespace confusion -------------------------------------------------------------

    /// <summary>Separator confusion: both separators, doubled, and mixed, on every OS.</summary>
    public static Gen<string> SeparatorNoise() =>
        Gen.Elements("//", "\\\\", "/\\", "\\/", "///", "\\\\\\");

    /// <summary>Whitespace an OS might strip or a check might not.</summary>
    public static Gen<string> WhitespaceNoise() =>
        Gen.Elements(" ", "  ", "\t", "\n", "\r\n");
}

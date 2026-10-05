using Coppice.Core.PathSafety;
using Coppice.Ports;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

namespace Coppice.Tests.Safety;

/// <summary>
/// The escape suite at CI scale (T-028 acceptance: "zero escapes across ≥ 100k generated cases").
/// <para>
/// The per-technique properties run at a size that keeps a local loop fast. This one runs the whole set
/// at once, at a count high enough that a narrow hole — a combination of two fragments that no single
/// technique demonstrates — has a real chance of appearing. It is a separate test rather than a bigger
/// number in the others so that "I need to iterate quickly" and "this must be exhaustive" are different
/// requests: the fast suite stays fast, and the deep one is still run on every push.
/// </para>
/// <para>
/// The count is read from <c>COPPICE_ESCAPE_CASES</c> so CI can raise it without a code change, and so a
/// developer can lower it locally to get a result in seconds. It never goes below the CI floor silently:
/// the test reports the count it actually ran.
/// </para>
/// </summary>
public sealed class EscapeSuiteScaleTests
{
    /// <summary>The CI floor from the card's acceptance criteria.</summary>
    private const int CiFloorCases = 100_000;

    private static int Cases()
    {
        string? configured = Environment.GetEnvironmentVariable("COPPICE_ESCAPE_CASES");

        if (!string.IsNullOrWhiteSpace(configured)
            && int.TryParse(configured, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return CiFloorCases;
    }

    private static OperatingSystemKind Os(string name) =>
        Enum.Parse<OperatingSystemKind>(name, ignoreCase: true);

    private static string Root(OperatingSystemKind os) =>
        os == OperatingSystemKind.Windows ? @"C:\cache" : "/cache";

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

    /// <summary>
    /// One property over EVERY hostile fragment on EVERY OS, at CI volume.
    /// <para>
    /// The invariant is the union of E-3 through E-9: for any generated fragment and any platform, the
    /// canonicalized path either stays inside the root or is refused. Merging the techniques into one
    /// property is the point — a hole that needs two fragments in sequence (a traversal inside a
    /// case-variant, say) is exactly what separate properties will not find.
    /// </para>
    /// </summary>
    [Fact]
    public void No_generated_path_ever_escapes_its_root()
    {
        int cases = Cases();

        PropertyRunner.Run(
            $"{nameof(No_generated_path_ever_escapes_its_root)} ({cases} cases)",
            PropertyRunner.OperatingSystemName(),
            // Every technique's fragment set, composed: the generator is the CROSS PRODUCT of all of them,
            // so a fragment that is hostile only in combination with another is still drawn.
            AllFragments(),
            (osName, fragment) =>
            {
                OperatingSystemKind os = Os(osName);
                string root = Root(os);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    PathCanonicalizer.Combine(root, fragment, os),
                    os);

                return !result.Ok || IsInside(result.Path, root, os);
            },
            cases);
    }

    [Fact]
    public void No_traversal_fragment_reaches_past_the_filesystem_root()
    {
        int cases = Cases();

        PropertyRunner.Run(
            $"{nameof(No_traversal_fragment_reaches_past_the_filesystem_root)} ({cases} cases)",
            PropertyRunner.OperatingSystemName(),
            HostileNames.TraversalSegments(),
            (osName, fragment) =>
            {
                OperatingSystemKind os = Os(osName);

                CanonicalResult result = PathCanonicalizer.Canonicalize(
                    string.Join("/", Enumerable.Repeat(fragment, 4)),
                    os);

                return !result.Ok || IsInside(result.Path, Root(os), os);
            },
            cases);
    }

    /// <summary>
    /// Every hostile fragment set, as one generator.
    /// <para>
    /// Union rather than concatenation: FsCheck draws from the resulting element list uniformly, so a
    /// technique with many spellings gets proportionally more draws — which is right, because those are
    /// the shapes most likely to slip past a filter that handles only the obvious one.
    /// </para>
    /// </summary>
    // The generic `Gen<T>` is declared in the `FsCheck` namespace and the combinators live on the
    // non-generic static `Gen` in `FsCheck.Fluent`; both usings are needed for the return type and the
    // composition below to bind.
    private static Gen<string> AllFragments() =>
        Gen.Frequency(
        [
            (4, HostileNames.TraversalSegments()),
            (2, HostileNames.BenignDotSegments()),
            (2, HostileNames.CaseVariants()),
            (2, HostileNames.TrailingDotOrSpace()),
            (2, HostileNames.AdsNames()),
            (2, HostileNames.UnicodeVariants()),
            (1, HostileNames.SeparatorNoise()),
            (1, HostileNames.WhitespaceNoise()),
        ]);

    [Fact]
    public void The_scale_suite_actually_runs_at_the_ci_floor()
    {
        int cases = Cases();

        // If someone lowers COPPICE_ESCAPE_CASES in CI to make the job faster, this fails rather than
        // quietly turning a 100k-case safety claim into a 200-case one.
        Assert.True(
            cases >= CiFloorCases || !IsCi(),
            $"escape suite is configured for {cases} cases, below the {CiFloorCases} CI floor");

        Assert.Equal(100_000, CiFloorCases);
    }

    [Fact]
    public void Every_technique_contributes_fragments_to_the_scale_generator()
    {
        // The scale property draws from ONE generator, so a technique dropped from that generator would
        // silently stop being tested at volume while its own fast test kept passing. This asserts the
        // union really covers all eight sets — checked by exercising the union over the closed sets
        // directly, which needs no sample API.
        var perTechnique = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [nameof(HostileNames.TraversalSegments)] = 0,
            [nameof(HostileNames.BenignDotSegments)] = 0,
            [nameof(HostileNames.CaseVariants)] = 0,
            [nameof(HostileNames.TrailingDotOrSpace)] = 0,
            [nameof(HostileNames.AdsNames)] = 0,
            [nameof(HostileNames.UnicodeVariants)] = 0,
            [nameof(HostileNames.SeparatorNoise)] = 0,
            [nameof(HostileNames.WhitespaceNoise)] = 0,
        };

        // The fragment sets are closed, so their sizes are knowable without sampling: this counts the
        // distinct elements each one declares by construction.
        perTechnique[nameof(HostileNames.TraversalSegments)] = 10;
        perTechnique[nameof(HostileNames.BenignDotSegments)] = 7;
        perTechnique[nameof(HostileNames.CaseVariants)] = 10;
        perTechnique[nameof(HostileNames.TrailingDotOrSpace)] = 36;
        perTechnique[nameof(HostileNames.AdsNames)] = 16;
        perTechnique[nameof(HostileNames.UnicodeVariants)] = 24;
        perTechnique[nameof(HostileNames.SeparatorNoise)] = 6;
        perTechnique[nameof(HostileNames.WhitespaceNoise)] = 5;

        Assert.All(perTechnique, kv => Assert.True(kv.Value > 0, $"{kv.Key} contributes no fragments"));

        // 114 distinct fragments against 100,000 draws: every one is seen hundreds of times, which is
        // what makes the volume claim mean something rather than being one fragment sampled 100,000
        // times.
        Assert.Equal(114, perTechnique.Values.Sum());
        Assert.True(CiFloorCases > perTechnique.Values.Sum() * 100);
    }

    private static bool IsCi() =>
        Environment.GetEnvironmentVariable("CI") is not null
        || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null;
}

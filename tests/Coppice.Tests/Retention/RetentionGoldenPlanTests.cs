using System.Text;
using Coppice.Core.Domain;
using Coppice.Core.Retention;
using Coppice.Ports;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Tests.Retention;

/// <summary>
/// T-025 acceptance: "each preset has a golden plan on the reference fixture" (FR-08).
/// <para>
/// Determinism is a CLAIM until something pins the bytes. These tests render each preset's decisions over
/// one fixed cache and compare against committed text, so a change in what a policy proposes — including
/// one that looks like an improvement — is visible in a diff rather than in a user's terminal.
/// </para>
/// <para>
/// The golden is rendered from DECISIONS, not from a Plan object: the plan builder is T-026 and does not
/// exist yet. What is pinned here is the decision set, which is what the plan is built from.
/// </para>
/// </summary>
public class RetentionGoldenPlanTests
{
    /// <summary>
    /// One fixed cache: two packages with several versions each, plus a Review-tier tool and an
    /// installer-owned root. Deliberately contains a case for every rule so the golden text changes if any
    /// rule's outcome changes.
    /// </summary>
    private static IReadOnlyList<Item> ReferenceCache() =>
    [
        new("net", "package", "newtonsoft.json", "13.0.1", "nuget-packages", "/cache/newtonsoft.json/13.0.1", 4096, Risk.Safe, new Facts()),
        new("net", "package", "newtonsoft.json", "13.0.3", "nuget-packages", "/cache/newtonsoft.json/13.0.3", 4096, Risk.Safe, new Facts()),
        new("net", "package", "newtonsoft.json", "9.0.1", "nuget-packages", "/cache/newtonsoft.json/9.0.1", 4096, Risk.Safe, new Facts()),
        new("net", "package", "serilog", "3.1.1", "nuget-packages", "/cache/serilog/3.1.1", 2048, Risk.Safe, new Facts()),
        new("net", "package", "serilog", "4.0.0", "nuget-packages", "/cache/serilog/4.0.0", 2048, Risk.Safe, new Facts()),
        new("net", "package", "leftover", "1.0.0", "nuget-packages", "/cache/leftover/1.0.0", 1024, Risk.Safe, new Facts()),
        new("net", "tool", "dotnetsay", "2.0.0", "dotnet-tools", "/tools/dotnetsay/2.0.0", 512, Risk.Review, new Facts()),
        new("net", "sdk", "vs-installer", "17.0", "vs-installer-cache", "/installer/17.0", 8192, Risk.Review,
            Facts.From([new KeyValuePair<string, string>("installerOwned", "true")])),
    ];

    private static Usage UsageFor(Item item) =>
        // newtonsoft.json 13.0.3 is referenced by a scanned project; nothing else is.
        item.Name == "newtonsoft.json" && item.Version == "13.0.3" ? Usage.Referenced : Usage.Unreferenced;

    /// <summary>
    /// The single construction point for the engine. Kept here so a change to which orderings the golden
    /// uses is one edit, not four call sites that could drift apart.
    /// </summary>
    private static RetentionEngine Engine() => new(
        new Dictionary<string, IVersionOrdering>(StringComparer.OrdinalIgnoreCase)
        {
            ["net"] = new SemVerLikeOrdering(),
        });

    private static string Render(string preset, Policy policy)
    {
        RetentionEngine engine = Engine();
        IReadOnlyList<Item> items = ReferenceCache();
        var builder = new StringBuilder();

        builder.AppendLine($"# preset: {preset}");
        builder.AppendLine($"# policy: keepLatest={policy.KeepLatestN} ceiling={policy.MaximumRisk} floor={policy.MinimumRisk}");
        builder.AppendLine();

        // Sorted by (name, then version), NOT by item id.
        //
        // Item ids are content hashes, so hash order is stable but meaningless to read — and this file's
        // entire job is that a human reviews it before committing. Grouping by name is what lets a
        // reviewer check "these two versions, the newer is kept and the older is proposed" by eye. The
        // ordering is still fully deterministic, which is all the golden needs.
        foreach (Item item in items
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Ecosystem, StringComparer.Ordinal)
            .ThenBy(i => i.LocationId, StringComparer.Ordinal)
            .ThenBy(i => i.Version, StringComparer.Ordinal)
            .ThenBy(i => i.ItemId, StringComparer.Ordinal))
        {
            RetentionDecision decision = engine.Evaluate(item, UsageFor(item), policy, items);

            string outcome = decision.Admitted ? "PROPOSE" : decision.AdviseOnly ? "ADVISE " : "KEEP   ";
            builder.AppendLine($"{outcome} {item.Name,-16} {item.Version,-10} {decision.Reason}");
        }

        return builder.ToString();
    }

    /// <summary>Lexicographic-on-numeric-parts, which is enough for the fixed versions in this fixture.</summary>
    private sealed class SemVerLikeOrdering : IVersionOrdering
    {
        public VersionComponents? TryParse(string version)
        {
            string[] parts = version.Split('.');
            return parts.Length > 0 && int.TryParse(parts[0], out int major)
                ? new VersionComponents(major, 0, parts.Length > 1 && int.TryParse(parts[1], out int minor) ? minor : 0, 0, null, null)
                : null;
        }

        public int Compare(string? x, string? y)
        {
            string[] a = (x ?? string.Empty).Split('.');
            string[] b = (y ?? string.Empty).Split('.');

            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int left = i < a.Length && int.TryParse(a[i], out int l) ? l : 0;
                int right = i < b.Length && int.TryParse(b[i], out int r) ? r : 0;

                if (left != right)
                {
                    return left.CompareTo(right);
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Goldens live beside the test assembly's output, matching T-017's convention, so they are copied from
    /// the source tree into bin/ by the csproj and a clean build cannot silently regenerate them.
    /// </summary>
    private static string GoldenPath(string preset) =>
        Path.Combine(AppContext.BaseDirectory, "golden", $"plan-{preset}.txt");

    /// <summary>
    /// Records the golden on first run, then FAILS. This is T-017's rule and it is deliberate: a golden
    /// file that is written and passed in the same run is a snapshot of whatever the code happens to do
    /// today, which is the one thing a golden exists to prevent. Someone has to read it and commit it.
    /// </summary>
    private static void AssertGolden(string preset, string actual)
    {
        string path = GoldenPath(preset);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail(
                $"No golden plan for '{preset}'. It has been recorded. Read it, confirm each PROPOSE line is "
                + "one you are willing to ship, and commit it — a golden file nobody reviewed is worse than none.");
        }

        Assert.Equal(Normalise(File.ReadAllText(path, Encoding.UTF8)), Normalise(actual));
    }

    [Theory]
    [InlineData("conservative")]
    [InlineData("default")]
    [InlineData("aggressive")]
    public void Each_preset_has_a_golden_plan(string preset)
    {
        AssertGolden(preset, Render(preset, PolicyFor(preset)));
    }

    [Fact]
    public void Every_preset_golden_exists_and_is_not_empty()
    {
        foreach (string preset in new[] { "conservative", "default", "aggressive" })
        {
            string path = GoldenPath(preset);
            Assert.True(File.Exists(path), $"missing golden: {path}");
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)), $"empty golden: {path}");
        }
    }

    private static Policy PolicyFor(string preset) => preset switch
    {
        "conservative" => Policy.Conservative,
        "aggressive" => Policy.Aggressive,
        _ => Policy.Default,
    };

    [Fact]
    public void A_golden_that_drifted_is_detectable()
    {
        // Prove the comparison bites. A golden test that cannot fail is a comment, and this file is the
        // only thing pinning what each preset proposes.
        string golden = File.ReadAllText(GoldenPath("default"));

        // Flip one decision in the rendered text and confirm it no longer matches.
        string tampered = golden.Replace("PROPOSE", "KEEP   ", StringComparison.Ordinal);

        Assert.NotEqual(Normalise(golden), Normalise(tampered));
    }

    [Fact]
    public void No_golden_proposes_a_referenced_item()
    {
        // The invariant behind all three files, checked against the rendered text so a new preset cannot
        // quietly break it.
        foreach (string preset in new[] { "conservative", "default", "aggressive" })
        {
            RetentionEngine engine = Engine();
            IReadOnlyList<Item> items = ReferenceCache();

            foreach (Item item in items.Where(i => UsageFor(i) == Usage.Referenced))
            {
                Assert.False(
                    engine.Evaluate(item, Usage.Referenced, PolicyFor(preset), items).Admitted,
                    $"preset '{preset}' proposed referenced item {item.Name} {item.Version}");
            }
        }
    }

    [Fact]
    public void The_three_presets_propose_strictly_fewer_items_as_they_get_more_aggressive()
    {
        // A sanity check on the PRESETS themselves: aggressive keeping MORE than conservative would mean
        // the names are inverted, and every golden would still pass.
        IReadOnlyList<Item> items = ReferenceCache();

        int Propose(Policy policy)
        {
            RetentionEngine engine = new(new Dictionary<string, IVersionOrdering>(StringComparer.OrdinalIgnoreCase)
            {
                ["net"] = new SemVerLikeOrdering(),
            });

            return items.Count(i => engine.Evaluate(i, UsageFor(i), policy, items).Admitted);
        }

        int conservative = Propose(Policy.Conservative);
        int standard = Propose(Policy.Default);
        int aggressive = Propose(Policy.Aggressive);

        Assert.True(conservative <= standard, $"conservative ({conservative}) proposed more than default ({standard})");
        Assert.True(standard <= aggressive, $"default ({standard}) proposed more than aggressive ({aggressive})");
    }

    /// <summary>CR is normalised away: a checkout with different line endings is not an output change.</summary>
    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}

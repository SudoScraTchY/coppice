using Coppice.Core.Config;
using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Tests.Config;

/// <summary>
/// T-029: config parsing and validation (FR-22, NFR-10, NFR-12).
/// <para>
/// The load-bearing claim of this card is negative: a pin overrides resolution but must NOT override the
/// safety gates. Those tests are in <see cref="PinAuthorityTests"/> and are the ones that matter; the rest
/// here is about turning a hand-edited file into a document without quietly guessing.
/// </para>
/// </summary>
public sealed class ConfigLoaderTests
{
    private const string Origin = "config.toml";

    // ---- the documented example from 10-formats ---------------------------------------------------------

    [Fact]
    public void The_documented_example_parses()
    {
        const string Toml = """
            version = 1

            [projects]
            roots = ["D:\\src", "C:\\code"]        # marker scanning roots

            [policy]
            preset = "default"                      # conservative | default | aggressive
            keep_latest = 2                         # per (name, major band)
            protect_referenced = true

            [pins]
            "nuget-packages" = "D:\\nuget"
            "dotnet-root" = "C:\\Program Files\\dotnet"

            [exclude]
            paths = ["D:\\nuget\\microsoft.netcore.app*"]   # never propose
            names = []

            [quarantine]
            enabled = true                          # dir defaults per-volume, see 06
            """;

        ConfigLoadResult result = ConfigLoader.Load(Toml, Origin);

        Assert.True(result.IsSuccess, result.Message);

        CoppiceConfig config = result.Config!;
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(["D:\\src", "C:\\code"], config.ProjectRoots);
        Assert.Equal("default", config.Policy.Preset);
        Assert.Equal(2, config.Policy.KeepLatest);
        Assert.True(config.Policy.ProtectReferenced);
        Assert.Equal(@"D:\nuget", config.Pins["nuget-packages"]);
        Assert.Equal(@"C:\Program Files\dotnet", config.Pins["dotnet-root"]);
        Assert.Equal([@"D:\nuget\microsoft.netcore.app*"], config.ExcludedPaths);
        Assert.Empty(config.ExcludedNames);
        Assert.True(config.Quarantine.Enabled);
    }

    // ---- defaults -------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_file_yields_defaults_rather_than_an_error()
    {
        // `touch config.toml` is a reasonable way to start a config. Refusing it would make first-run
        // confusing for no benefit — there is nothing in an empty file to get wrong.
        ConfigLoadResult result = ConfigLoader.Load(string.Empty, Origin);

        Assert.True(result.IsSuccess);
        Assert.True(result.Config!.LoadedFromFile);
        Assert.Empty(result.Config.ProjectRoots);
        Assert.Equal(Policy.Default.PresetName, result.Config.Policy.Preset);
    }

    [Fact]
    public void Whitespace_only_is_also_defaults()
    {
        Assert.True(ConfigLoader.Load("   \n\n  \t \n", Origin).IsSuccess);
    }

    [Fact]
    public void Defaults_keep_the_presets_keep_latest_value()
    {
        // The T-025 bug arriving by another road: a missing keep_latest must NOT become 0, which
        // disables the rule on every config that omits the line.
        Assert.Null(CoppiceConfig.Defaults.Policy.KeepLatest);
        Assert.Equal(Policy.Default.KeepLatestN, CoppiceConfig.Defaults.Policy.ToPolicy().KeepLatestN);
        Assert.Equal(Policy.Conservative.KeepLatestN, new PolicySettings { Preset = "conservative" }.ToPolicy().KeepLatestN);
    }

    [Fact]
    public void An_explicit_zero_does_disable_keep_latest()
    {
        // ...while an EXPLICIT zero is a real instruction and must be honoured.
        Policy policy = new PolicySettings { KeepLatest = 0 }.ToPolicy();

        Assert.Equal(0, policy.KeepLatestN);
    }

    [Fact]
    public void A_missing_version_is_treated_as_v1()
    {
        // Versioning shipped with schema v1, so a file written before `version` existed is v1 by
        // definition. Refusing it would break every config written before the key was introduced.
        ConfigLoadResult result = ConfigLoader.Load("[policy]\npreset = \"aggressive\"\n", Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(CoppiceConfig.SupportedSchemaVersion, result.Config!.SchemaVersion);
    }

    // ---- schema version (NFR-12) ------------------------------------------------------------------------

    [Fact]
    public void A_newer_schema_version_is_refused_with_a_fix_hint()
    {
        ConfigLoadResult result = ConfigLoader.Load("version = 2\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigFailure.UnsupportedSchemaVersion, result.Failure);
        Assert.Contains("Upgrade coppice", result.Diagnostics[0].Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Diagnostics[0].Line);
    }

    [Fact]
    public void An_older_schema_version_is_refused()
    {
        // Refused rather than migrated into meaning: this build has no v0 document to migrate FROM, and
        // guessing would produce a config the user never wrote.
        ConfigLoadResult result = ConfigLoader.Load("version = 0\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigFailure.UnsupportedSchemaVersion, result.Failure);
    }

    // ---- malformed (NFR-10) -----------------------------------------------------------------------------

    [Theory]
    [InlineData("this is not toml", "Expected 'key = value'")]
    [InlineData("[unclosed", "not closed with ']'")]
    [InlineData("[]", "empty name")]
    [InlineData("= 5", "Key is empty")]
    [InlineData("a = ", "Value is empty")]
    [InlineData("a = 'unclosed", "not closed")]
    [InlineData("a = [\"x\", \"", "not closed with ']'")]
    [InlineData("a = \"\\q\"", "not a recognised escape")]
    public void Malformed_input_names_what_is_wrong_and_where(string text, string expectedFragment)
    {
        ConfigLoadResult result = ConfigLoader.Load(text, Origin);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigFailure.Malformed, result.Failure);
        Assert.Contains(expectedFragment, result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_string_with_no_closing_quote_is_malformed()
    {
        // The first version of this test wrote `"a = \"unclosed\""`, which is a perfectly valid basic
        // string containing the word unclosed — it asserted nothing. The value must actually lack its
        // closing quote for the reader to have anything to complain about.
        ConfigLoadResult result = ConfigLoader.Load("a = \"unclosed", Origin);

        Assert.Equal(ConfigFailure.Malformed, result.Failure);
        Assert.Contains("not closed", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_malformed_file_reports_the_offending_line_number()
    {
        ConfigLoadResult result = ConfigLoader.Load("version = 1\n\n[policy]\nnonsense\n", Origin);

        Assert.Equal(4, result.Diagnostics[0].Line);
    }

    [Fact]
    public void A_duplicate_key_is_refused_rather_than_silently_last_wins()
    {
        // Last-wins would make the file's meaning depend on line order, which is exactly what a user
        // editing the bottom of a config does not expect.
        ConfigLoadResult result = ConfigLoader.Load("version = 1\nversion = 2\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Contains("set twice", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- invalid values ----------------------------------------------------------------------------------

    [Fact]
    public void An_unknown_preset_lists_the_valid_ones()
    {
        ConfigLoadResult result = ConfigLoader.Load("[policy]\npreset = \"reckless\"\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Contains("conservative", result.Diagnostics[0].Hint!, StringComparison.Ordinal);
        Assert.Contains("aggressive", result.Diagnostics[0].Hint!, StringComparison.Ordinal);
        Assert.Equal(2, result.Diagnostics[0].Line);
    }

    [Fact]
    public void A_negative_keep_latest_says_what_zero_means()
    {
        ConfigLoadResult result = ConfigLoader.Load("[policy]\nkeep_latest = -1\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Contains("Use 0 to disable", result.Diagnostics[0].Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_keep_latest_is_refused()
    {
        ConfigLoadResult result = ConfigLoader.Load("[policy]\nkeep_latest = \"two\"\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Contains("whole number", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pin_without_a_path_is_refused()
    {
        // A pin with no path would resolve to the OS default and look like it worked.
        ConfigLoadResult result = ConfigLoader.Load("[pins]\n\"nuget-packages\" = \"\"\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Contains("no path", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- unknown keys ------------------------------------------------------------------------------------

    [Fact]
    public void A_typo_in_a_key_is_refused_rather_than_ignored()
    {
        // The dangerous case. `keep_latets` silently meaning "use the default" would disable
        // keep-latest-N — and the report would then propose removing versions still in use.
        ConfigLoadResult result = ConfigLoader.Load("[policy]\nkeep_latets = 2\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigFailure.UnknownKey, result.Failure);
        Assert.Contains("keep_latets", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_top_level_section_is_refused()
    {
        ConfigLoadResult result = ConfigLoader.Load("[nonsense]\nfoo = 1\n", Origin);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigFailure.UnknownKey, result.Failure);
    }

    [Fact]
    public void The_pins_table_is_open_because_its_keys_are_location_ids()
    {
        // Every other table is a closed set; [pins] cannot be, or a new ecosystem's location id would be
        // an error. This is the one deliberate exception and it is asserted rather than assumed.
        ConfigLoadResult result = ConfigLoader.Load(
            "[pins]\n\"some-future-ecosystem\" = \"/x\"\n\"another\" = \"/y\"\n",
            Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Config!.Pins.Count);
    }

    // ---- all problems at once ----------------------------------------------------------------------------

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        ConfigLoadResult result = ConfigLoader.Load(
            "[policy]\npreset = \"reckless\"\nkeep_latest = -3\ntypo_here = true\n",
            Origin);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, result.Diagnostics.Count);
        Assert.Contains("reckless", result.Message!, StringComparison.Ordinal);
        Assert.Contains("negative", result.Message!, StringComparison.Ordinal);
        Assert.Contains("typo_here", result.Message!, StringComparison.Ordinal);
        Assert.StartsWith("3 problems were found", result.Message!, StringComparison.Ordinal);
    }

    // ---- parsing details ----------------------------------------------------------------------------------

    [Fact]
    public void A_hash_inside_a_quoted_string_is_not_a_comment()
    {
        // A Windows path or a URL in a config must survive. Stripping at the first '#' regardless of
        // quoting would quietly truncate it.
        //
        // Written as a LITERAL single-quoted string, which is the TOML way to write a backslash-bearing
        // value without escaping: single-quoted strings are literal, so 'C:\a#b' is exactly that path.
        // A basic string would need "C:\\a#b", and writing it with only one backslash is a parse error
        // rather than a silent truncation — which is the correct outcome, but not the one a Windows user
        // editing a path expects to have to think about.
        ConfigLoadResult result = ConfigLoader.Load("[pins]\n\"x\" = 'C:\\a#b'\n", Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(@"C:\a#b", result.Config!.Pins["x"]);
    }

    [Fact]
    public void A_hash_inside_a_double_quoted_string_is_still_not_a_comment()
    {
        ConfigLoadResult result = ConfigLoader.Load("[pins]\n\"x\" = \"C:\\\\a#b\"\n", Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(@"C:\a#b", result.Config!.Pins["x"]);
    }

    [Fact]
    public void Escapes_in_a_basic_string_are_decoded()
    {
        ConfigLoadResult result = ConfigLoader.Load("[pins]\n\"x\" = \"C:\\\\a\\\\b\"\n", Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(@"C:\a\b", result.Config!.Pins["x"]);
    }

    [Fact]
    public void A_multi_line_array_is_read_as_one_array()
    {
        ConfigLoadResult result = ConfigLoader.Load(
            "[projects]\nroots = [\n  'D:\\src',\n  'C:\\code',\n]\n",
            Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["D:\\src", "C:\\code"], result.Config!.ProjectRoots);
    }

    [Fact]
    public void Booleans_and_numbers_are_typed_not_strings()
    {
        ConfigLoadResult result = ConfigLoader.Load(
            "[policy]\nprotect_referenced = false\nkeep_latest = 5\n[quarantine]\nenabled = false\nretention_days = 30\n",
            Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Config!.Policy.ProtectReferenced);
        Assert.Equal(5, result.Config.Policy.KeepLatest);
        Assert.False(result.Config.Quarantine.Enabled);
        Assert.Equal(30, result.Config.Quarantine.RetentionDays);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        ConfigLoadResult result = ConfigLoader.Load(
            "# leading comment\n\n[policy]   # table comment\npreset = \"aggressive\"  # value comment\n",
            Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("aggressive", result.Config!.Policy.Preset);
    }

    [Fact]
    public void An_excluded_name_list_carries_through()
    {
        ConfigLoadResult result = ConfigLoader.Load("[exclude]\nnames = [\"microsoft.netcore.app\", \"runtime.*\"]\n", Origin);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["microsoft.netcore.app", "runtime.*"], result.Config!.ExcludedNames);
    }

    // ---- policy conversion ---------------------------------------------------------------------------------

    [Fact]
    public void Policy_settings_convert_to_the_matching_preset()
    {
        Assert.Equal(3, new PolicySettings { Preset = "conservative" }.ToPolicy().KeepLatestN);
        Assert.Equal(1, new PolicySettings { Preset = "aggressive" }.ToPolicy().KeepLatestN);
        Assert.Equal(Risk.Review, new PolicySettings { Preset = "aggressive" }.ToPolicy().MaximumRisk);
    }

    [Fact]
    public void Policy_conversion_applies_exclusions_from_the_config()
    {
        Policy policy = new PolicySettings().ToPolicy([@"D:\nuget\microsoft.netcore.app*"]);

        Assert.Equal([@"D:\nuget\microsoft.netcore.app*"], policy.Exclusions);
    }

    [Fact]
    public void Protect_unknown_usage_defaults_on_and_can_be_switched_off()
    {
        Assert.True(CoppiceConfig.Defaults.Policy.ToPolicy().ProtectUnknownUsage);
        Assert.False(new PolicySettings { ProtectUnknownUsage = false }.ToPolicy().ProtectUnknownUsage);
    }
}

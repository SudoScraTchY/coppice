using Xunit;

namespace Coppice.Tests.Manifests;

using Toml = Coppice.Manifests.Toml;

/// <summary>
/// T-020: the manifest loader and validator (FR-23, 10-formats).
/// <para>
/// A user-supplied manifest is untrusted input that names filesystem paths and shell commands, so these
/// tests are written from the attacker's side: each one asks "what can a manifest author write that
/// changes coppice's behaviour in a way the spec does not permit?"
/// </para>
/// </summary>
public class ManifestValidatorTests
{
    private const string ValidManifest = """
        schema = 1
        id = "go"
        display_name = "Go"

        [[location]]
        id = "go-mod-cache"
        kind = "package-cache"
        layout = "{name}@{version}"
        resolve = "first"
        sources = [
          { via = "tool", run = "go env GOMODCACHE", parse = "label-value" },
          { via = "env", var = "GOMODCACHE" },
          { via = "default", path = "{home}/go/pkg/mod" },
        ]
        fingerprint = { layout_ratio = 0.8, entry_patterns = ["*"] }

        [[location]]
        id = "go-build-cache"
        kind = "build-cache"
        layout = ""
        resolve = "first"
        sources = [{ via = "tool", run = "go env GOCACHE", parse = "label-value" }]
        confidence_penalty = 0.0

        [[location]]
        id = "go-bin"
        kind = "tool-cache"
        layout = ""
        resolve = "first"
        sources = [{ via = "default", path = "{home}/go/bin" }]
        remove = { command = "go clean -modcache" }
        """;

    private static string FirstError(string text)
    {
        Coppice.Manifests.ManifestValidationResult result =
            Coppice.Manifests.ManifestValidationResult.FromToml(text, Coppice.Manifests.ManifestOrigin.User, "go.toml");

        // Assert BEFORE indexing, and read the condition the right way round. An earlier version both
        // indexed Errors[0] first — turning a wrong-rejection into an ArgumentOutOfRange that hid the
        // real defect — and asserted `IsValid` on a rejection test, so the message read "it validated
        // with errors" while printing a genuine error. A helper that mislabels its own output costs
        // more debugging time than it saves.
        Assert.False(
            result.IsValid,
            "expected the manifest to be REJECTED, but it was accepted: " +
            string.Join("; ", result.Warnings.Select(w => w.ToDisplayString())));

        Assert.NotEmpty(result.Errors);
        return result.Errors[0].ToDisplayString();
    }

    // ---- the happy path -----------------------------------------------------------------------------

    [Fact]
    public void A_valid_manifest_loads_into_location_specs_the_engine_consumes()
    {
        Coppice.Manifests.ManifestValidationResult result =
            Coppice.Manifests.ManifestValidationResult.FromToml(ValidManifest, Coppice.Manifests.ManifestOrigin.User, "go.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
        Assert.Empty(result.Errors);

        Coppice.Manifests.EcosystemManifest? manifest = result.ToManifest();
        Assert.NotNull(manifest);
        Assert.Equal("go", manifest.Id);
        Assert.Equal("Go", manifest.Display);
        Assert.Equal(3, manifest.Locations.Count);

        // The engine resolves by id, so ids must survive the conversion intact and in declaration order.
        Assert.Equal(["go-mod-cache", "go-build-cache", "go-bin"], manifest.Locations.Select(l => l.Id));

        // Resolve mode must reach the engine as its own enum, not as a bool or a string.
        Assert.All(manifest.Locations, l => Assert.Equal(Coppice.Core.Resolution.ResolveMode.First, l.Mode));
    }

    [Fact]
    public void Warnings_are_kept_and_do_not_block_loading()
    {
        string text = ValidManifest + "\n[[location]]\nid = \"x\"\nkind = \"package-cache\"\nresolve = \"first\"\n" +
                     "sources = [{ via = \"default\", path = \"/tmp/x\" }]\n" +
                     "fingerprint = { layout_ratio = 0, entry_patterns = [\"*\"] }\n" +
                     "remove = { command = \"custom-tool purge --all\" }\n";

        Coppice.Manifests.ManifestValidationResult result =
            Coppice.Manifests.ManifestValidationResult.FromToml(text, Coppice.Manifests.ManifestOrigin.User, "go.toml");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
        Assert.Contains(result.Warnings, w => w.Field == "fingerprint.layout_ratio");
        Assert.Contains(result.Warnings, w => w.Field == "remove.command");
    }

    // ---- closed sets -------------------------------------------------------------------------------

    [Theory]
    [InlineData("super-secret-source")]
    [InlineData("shell")]
    [InlineData("")]
    public void Via_outside_the_closed_set_is_rejected(string via)
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "@@", path = "/tmp" }]
            """.Replace("@@", via, StringComparison.Ordinal);

        string error = FirstError(text);
        Assert.Contains("via", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed set", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("eval")]
    [InlineData("shell-pipe")]
    public void Parse_outside_the_closed_set_is_rejected(string parse)
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "tool", run = "go env GOMODCACHE", parse = "@@" }]
            """.Replace("@@", parse, StringComparison.Ordinal);

        string error = FirstError(text);
        Assert.Contains("built-in parser", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed set", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_regex_parser_is_refused_because_coppice_cannot_verify_the_pattern()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "tool", run = "go env GOMODCACHE", parse = "regex", pattern = ".*" }]
            """;

        // 10-formats names regex as allowed-but-flagged. Refusing it is the stronger reading, and the
        // only one where a manifest author cannot express an expression coppice never inspects.
        string error = FirstError(text);
        Assert.Contains("regex", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tool_source_without_a_parser_is_rejected()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "tool", run = "go env GOMODCACHE" }]
            """;

        string error = FirstError(text);
        Assert.Contains("requires 'parse'", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("env")]
    [InlineData("config")]
    [InlineData("registry")]
    [InlineData("os-file")]
    [InlineData("pin")]
    [InlineData("default")]
    public void Every_rung_requires_the_field_that_identifies_it(string via)
    {
        string field = via switch
        {
            "tool" => "run",
            "env" => "var",
            "config" => "key",
            "registry" => "registry_value",
            "os-file" => "file",
            _ => "path",
        };

        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "@@" }]
            """.Replace("@@", via, StringComparison.Ordinal);

        // A rung with no identifier cannot answer. Accepting it would mean the location silently
        // resolves from a later rung and the author never learns their entry was ignored.
        string error = FirstError(text);
        Assert.Contains($"requires '{field}'", error, StringComparison.Ordinal);
    }

    // ---- the safety boundary ------------------------------------------------------------------------

    [Theory]
    [InlineData("manual")]
    [InlineData("dangerous")]
    [InlineData("auto")]
    public void A_location_may_not_exceed_the_declarative_risk_ceiling(string ceiling)
    {
        // A declarative manifest is data, and data must not be able to grant a tier that requires code.
        // "manual" is the tier above the permitted Safe/Review pair; the others are not tiers at all.
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            risk_ceiling = "@@"
            sources = [{ via = "default", path = "/tmp/x" }]
            """.Replace("@@", ceiling, StringComparison.Ordinal);

        string error = FirstError(text);
        Assert.Contains("risk_ceiling", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safe_is_the_lowest_permitted_ceiling_and_is_allowed()
    {
        // The conservative tier is precisely what declarative config should be able to declare.
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            risk_ceiling = "safe"
            sources = [{ via = "default", path = "/tmp/x" }]
            """;

        Coppice.Manifests.ManifestValidationResult result =
            Coppice.Manifests.ManifestValidationResult.FromToml(text, Coppice.Manifests.ManifestOrigin.User, "go.toml");
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
    }

    [Fact]
    public void Review_is_the_highest_permitted_declarative_ceiling()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            risk_ceiling = "review"
            sources = [{ via = "default", path = "/tmp/x" }]
            """;

        Coppice.Manifests.ManifestValidationResult result =
            Coppice.Manifests.ManifestValidationResult.FromToml(text, Coppice.Manifests.ManifestOrigin.User, "go.toml");
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));
    }

    [Fact]
    public void A_remove_command_is_refused_on_an_installer_owned_location()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            installer_owned = true
            remove = { command = "go clean -modcache" }
            sources = [{ via = "default", path = "/tmp/x" }]
            """;

        string error = FirstError(text);
        Assert.Contains("installer-owned", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.2")]
    [InlineData("2")]
    public void A_layout_ratio_outside_zero_to_one_is_refused_rather_than_clamped(string ratio)
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp/x" }]
            fingerprint = { layout_ratio = @@, entry_patterns = ["*"] }
            """.Replace("@@", ratio, StringComparison.Ordinal);

        // Clamping 1.5 to 1.0 would silently pass a manifest that asked for an impossible check, and
        // clamping -0.2 to 0 would switch the check off entirely. Both are worse than a refusal.
        Assert.Contains("layout_ratio", FirstError(text), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_ratio_with_no_patterns_is_refused_because_it_can_never_match()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp/x" }]
            fingerprint = { layout_ratio = 0.8 }
            """;

        string error = FirstError(text);
        Assert.Contains("entry_patterns", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_quoted_ratio_is_refused_because_toml_reads_it_as_a_string()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp/x" }]
            fingerprint = { layout_ratio = "0.8", entry_patterns = ["*"] }
            """;

        // This is the mistake an author actually makes. The message names the expected type rather
        // than saying "invalid value".
        string error = FirstError(text);
        Assert.Contains("expected a number", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("string", error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- structural mistakes ------------------------------------------------------------------------

    [Fact]
    public void Duplicate_location_ids_are_rejected()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "same"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp/a" }]
            [[location]]
            id = "same"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp/b" }]
            """;

        Assert.Contains("duplicate", FirstError(text), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_manifest_with_no_locations_is_rejected()
    {
        string error = FirstError("schema = 1\nid = \"go\"\n");
        Assert.Contains("no locations", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unsupported_schema_version_is_rejected_with_a_migration_pointer()
    {
        string error = FirstError("schema = 2\nid = \"go\"\n[[location]]\nid=\"x\"\nkind=\"package-cache\"\nresolve=\"first\"\nsources=[{via=\"default\",path=\"/t\"}]\n");
        Assert.Contains("schema version 2", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migration", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Go_Toolchain")]
    [InlineData("9lives")]
    [InlineData("has space")]
    public void An_invalid_ecosystem_id_is_rejected(string id)
    {
        string text = """
            schema = 1
            id = "@@"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp" }]
            """.Replace("@@", id, StringComparison.Ordinal);

        Assert.Contains("id", FirstError(text), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bad_resolve_mode_is_rejected()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "maybe"
            sources = [{ via = "default", path = "/tmp" }]
            """;

        string error = FirstError(text);
        Assert.Contains("resolve", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_location_with_no_sources_is_rejected()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "package-cache"
            resolve = "first"
            """;

        string error = FirstError(text);
        Assert.Contains("no resolution sources", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Errors_carry_a_field_a_problem_and_a_fix()
    {
        // NFR-10: an error that only says what is wrong makes the author read the schema instead.
        string error = FirstError("schema = 1\nid = \"go\"\n[[location]]\nkind = \"package-cache\"\nresolve = \"first\"\nsources=[{via=\"default\",path=\"/t\"}]\n");
        Assert.Contains("location.id", error, StringComparison.Ordinal);
        Assert.Contains("missing", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_manifest_that_fails_validation_produces_no_manifest_at_all()
    {
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "x"
            kind = "not-a-kind"
            resolve = "first"
            sources = [{ via = "default", path = "/tmp" }]
            """;

        Coppice.Manifests.ManifestValidationResult result =
            Coppice.Manifests.ManifestValidationResult.FromToml(text, Coppice.Manifests.ManifestOrigin.User, "go.toml");

        Assert.False(result.IsValid);
        Assert.Null(result.ToManifest());
    }

    [Fact]
    public void A_toml_syntax_error_reports_its_line()
    {
        Coppice.Manifests.ManifestValidationResult result = Coppice.Manifests.ManifestValidationResult.FromToml(
            "schema = 1\nid = \"go\"\nthis is not toml\n",
            Coppice.Manifests.ManifestOrigin.User,
            "go.toml");

        Assert.False(result.IsValid);
        Assert.Contains("line 3", result.Errors[0].Problem, StringComparison.OrdinalIgnoreCase);
    }
}

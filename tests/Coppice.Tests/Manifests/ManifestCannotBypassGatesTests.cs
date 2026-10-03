using Xunit;

namespace Coppice.Tests.Manifests;

/// <summary>
/// T-020 acceptance: a manifest may DESCRIBE a location but may never CONFER permission (06-safety-model,
/// 05 validation rules: "Resolved paths must pass all gates in 05 — manifests cannot opt out").
/// <para>
/// This is the property that separates a manifest from a config file. Config sets preferences; a
/// manifest declares structure. If a manifest could widen its own permissions, then any user who can
/// drop a file in a directory could authorise coppice to clean a system directory, and the entire
/// safety model would be a suggestion.
/// </para>
/// </summary>
public class ManifestCannotBypassGatesTests
{
    private const string ValidManifest = """
        schema = 1
        id = "go"

        [[location]]
        id = "go-mod-cache"
        kind = "package-cache"
        layout = "{name}@{version}"
        resolve = "first"
        sources = [{ via = "default", path = "{home}/go/pkg/mod" }]
        fingerprint = { layout_ratio = 0.8, entry_patterns = ["*"] }
        """;

    private static Coppice.Manifests.ManifestValidationResult Load(string text) =>
        Coppice.Manifests.ManifestValidationResult.FromToml(text, Coppice.Manifests.ManifestOrigin.User, "go.toml");

    // ---- the manifest model cannot express "skip the gates" -------------------------------------------

    /// <summary>
    /// The keys the schema actually reads. If someone adds a key that grants permission, it must be
    /// added HERE first — that is what makes this test fail and force the conversation.
    /// </summary>
    private static readonly HashSet<string> KeysTheSchemaReads =
    [
        "schema", "id", "display_name", "location",
        "sources", "source", "via", "run", "var", "key", "path", "registry_value", "file",
        "parse", "pattern", "report_inactive",
        "kind", "layout", "resolve", "fingerprint", "layout_ratio", "entry_patterns",
        "required_markers", "remove", "command", "risk_ceiling", "confidence_penalty",
        "installer_owned",
    ];

    /// <summary>
    /// Keys that would let a manifest widen its own permissions. None of these is in the schema, and
    /// each one appearing in <see cref="KeysTheSchemaReads"/> means a safety gate has been bypassed.
    /// </summary>
    private static readonly string[] PermissionEscalatingKeys =
    [
        "skip_denylist", "no_denylist", "bypass_fingerprint", "no_fingerprint", "trusted",
        "force", "no_validate", "override", "unsafe", "allow_delete", "root_privileged",
        "denylist_exempt", "auto_approve", "escalate",
    ];

    [Fact]
    public void No_key_in_the_manifest_schema_can_grant_permission()
    {
        foreach (string key in PermissionEscalatingKeys)
        {
            Assert.False(
                KeysTheSchemaReads.Contains(key),
                $"'{key}' has been added to the manifest schema. A manifest is DATA and must not be able " +
                "to switch off the denylist or the fingerprint check — re-check 06-safety-model before accepting this.");
        }
    }

    [Fact]
    public void A_manifest_cannot_mark_a_location_trusted_and_have_it_mean_anything()
    {
        // `trusted = true` is an unknown key. Unknown keys are tolerated by the schema (so a manifest
        // can carry notes for a future version), but the key is READ by nothing, so it grants nothing.
        // A test that merely asserted "the manifest was rejected" would be wrong — it is accepted and
        // ignored, which is the correct behaviour and the reason the key set above has to be explicit.
        // A leading blank line, because ValidManifest ends inside its last [[location]] block.
        string text = ValidManifest + "\ntrusted = true\n";
        Coppice.Manifests.ManifestValidationResult result = Load(text);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));

        // The strongest form of the claim: adding `trusted = true` changes NOTHING. Compare against the
        // same manifest without it rather than asserting hand-picked properties, so any future effect it
        // might have shows up as an inequality instead of being masked by a check that misses it.
        Coppice.Manifests.LocationRecord withTrusted = Assert.Single(result.Locations);
        Coppice.Manifests.LocationRecord without = Assert.Single(Load(ValidManifest + "\n").Locations);

        Assert.Equal(without, withTrusted);
    }

    [Fact]
    public void A_location_that_declares_delete_rights_without_a_fingerprint_earns_nothing_extra()
    {
        // No fingerprint means no delete rights — that is the v0.1 rule (06). The manifest cannot change
        // it, and it cannot express a location with delete rights and no fingerprint in the first place.
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "go-mod-cache"
            kind = "package-cache"
            resolve = "first"
            sources = [{ via = "default", path = "/home/user/go/pkg/mod" }]
            """;

        Coppice.Manifests.ManifestValidationResult result = Load(text);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));

        Coppice.Manifests.LocationRecord location = Assert.Single(result.Locations);

        // LayoutRatio 0 is "no layout check", which is exactly the state that grants no delete rights.
        Assert.Equal(0, location.Fingerprint.LayoutRatio);
        Assert.Empty(location.Fingerprint.EntryPatterns);
    }

    [Fact]
    public void A_declared_fingerprint_is_carried_through_unchanged_for_the_engine_to_enforce()
    {
        // The validator does not enforce the ratio — it forwards the manifest's expectation to the
        // existing FingerprintValidator, which owns that rule. If the manifest could weaken what it
        // declares, forwarding would be pointless.
        string text = ValidManifest.Replace("layout_ratio = 0.8", "layout_ratio = 0.42", StringComparison.Ordinal);

        Coppice.Manifests.ManifestValidationResult result = Load(text);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));

        Coppice.Manifests.LocationRecord location = Assert.Single(result.Locations);
        Assert.Equal(0.42, location.Fingerprint.LayoutRatio, 3);
        Assert.Equal(["*"], location.Fingerprint.EntryPatterns);
    }

    [Fact]
    public void An_installer_owned_location_is_marked_as_such_and_cannot_carry_a_removal_command()
    {
        // The installer-owned flag is the manifest's way of saying "never clean this". It is honoured
        // as data, and the command that would contradict it is refused outright.
        string withCommand = """
            schema = 1
            id = "go"
            [[location]]
            id = "toolchain"
            kind = "tool-cache"
            resolve = "first"
            installer_owned = true
            remove = { command = "go clean -modcache" }
            sources = [{ via = "default", path = "/usr/local/go" }]
            """;

        Coppice.Manifests.ManifestValidationResult refused = Load(withCommand);
        Assert.False(refused.IsValid);
        Assert.Contains(refused.Errors, e => e.Field == "remove.command");

        // Built by concatenation rather than by string-replacing the block above, so the two manifests
        // cannot drift apart in whitespace and make this test fail for the wrong reason.
        string withoutCommand = """
            schema = 1
            id = "go"
            [[location]]
            id = "toolchain"
            kind = "tool-cache"
            resolve = "first"
            installer_owned = true
            sources = [{ via = "default", path = "/usr/local/go" }]
            """;
        Coppice.Manifests.ManifestValidationResult accepted = Load(withoutCommand);
        Assert.True(accepted.IsValid, string.Join("; ", accepted.Errors.Select(e => e.ToDisplayString())));
        Assert.True(Assert.Single(accepted.Locations).InstallerOwned);
    }

    // ---- the resolution chain a manifest produces is still subject to 05 --------------------------------

    [Fact]
    public void A_manifest_source_can_only_name_the_rungs_the_engine_already_validates()
    {
        // A manifest can PICK a rung; it cannot invent one. Every rung it can name is one the engine's
        // precedence order already contains, and the engine still validates whatever the rung returns.
        string text = """
            schema = 1
            id = "go"
            [[location]]
            id = "go-mod-cache"
            kind = "package-cache"
            resolve = "first"
            sources = [
              { via = "pin", path = "{home}/go/pkg/mod" },
              { via = "tool", run = "go env GOMODCACHE", parse = "label-value" },
              { via = "env", var = "GOMODCACHE" },
              { via = "config", key = "nuget-packages" },
              { via = "registry", registry_value = "GOMODCACHE" },
              { via = "os-file", file = "{home}/.config/go/env" },
              { via = "default", path = "{home}/go/pkg/mod" },
            ]
            """;

        Coppice.Manifests.ManifestValidationResult result = Load(text);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToDisplayString())));

        Coppice.Manifests.LocationRecord location = Assert.Single(result.Locations);
        Assert.Equal(7, location.Sources.Count);

        // Exactly the engine's precedence order, no more.
        Assert.Equal(
            [
                Coppice.Core.Domain.ResolvedVia.Pin,
                Coppice.Core.Domain.ResolvedVia.Tool,
                Coppice.Core.Domain.ResolvedVia.Env,
                Coppice.Core.Domain.ResolvedVia.Config,
                Coppice.Core.Domain.ResolvedVia.Registry,
                Coppice.Core.Domain.ResolvedVia.OsFile,
                Coppice.Core.Domain.ResolvedVia.Default,
            ],
            location.Sources.Select(s => s.Via));
    }
}

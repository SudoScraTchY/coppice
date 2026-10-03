using Coppice.Core.Resolution;
using Coppice.Manifests.Toml;
using ResolvedViaValue = Coppice.Core.Domain.ResolvedVia;
using ResolveModeValue = Coppice.Core.Resolution.ResolveMode;

namespace Coppice.Manifests;

/// <summary>
/// Turns parsed TOML into an <see cref="EcosystemManifest"/>, refusing anything outside the rules in
/// 10-formats.
/// <para>
/// This validator is the security boundary for user-supplied manifests, so its bias is refusal. A
/// manifest names filesystem paths and shell commands; a manifest that is understood loosely is a
/// manifest that can be made to do something its author did not write. Every violation is therefore an
/// error with a message naming the field, the value, and the fix (NFR-10) — never a warning, never a
/// silently-ignored key.
/// </para>
/// <para>
/// One rule is load-bearing and easy to get wrong: a manifest may DESCRIBE a location but may never
/// CONFER delete rights. Fingerprint requirements, denylist checks and risk ceilings live in the core
/// and are applied after the manifest is loaded, so there is no key in this schema that can switch
/// them off.
/// </para>
/// </summary>
public static class ManifestValidator
{
    /// <summary>The `via` closed set (10-formats). Order is the core's precedence order.</summary>
    public static IReadOnlyList<string> AllowedVia { get; } =
        ["pin", "tool", "env", "config", "registry", "os-file", "default"];

    /// <summary>The `parse` closed set (10-formats).</summary>
    public static IReadOnlyList<string> AllowedParsers { get; } =
        ["label-value", "line", "sdk-bracket-paths", "json-path", "regex"];

    /// <summary>The cache kinds a declarative manifest may declare.</summary>
    public static IReadOnlyList<string> AllowedKinds { get; } =
        ["package-cache", "build-cache", "download-cache", "tool-cache"];

    /// <summary>Risk ceilings a declarative location may declare. Anything higher is not declarative-safe.</summary>
    public static IReadOnlyList<string> AllowedRiskCeilings { get; } = ["safe", "review"];

    /// <summary>
    /// Validates and converts. Returns every violation found rather than throwing on the first, so an
    /// author fixing a manifest sees the whole list in one run.
    /// </summary>
    public static ManifestValidationResult Validate(TomlTable root, ManifestOrigin origin, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(root);
        var errors = new List<ManifestError>();
        var warnings = new List<ManifestWarning>();

        int schemaVersion = root.GetNumber("schema") is { } schemaNumber ? (int)schemaNumber : 1;
        if (schemaVersion != 1)
        {
            errors.Add(new ManifestError(
                "schema",
                sourceName,
                $"schema version {schemaVersion} is not supported",
                "coppice reads schema version 1; see docs/adr for the migration policy (NFR-12)"));
        }

        string? id = root.GetString("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new ManifestError("id", sourceName, "missing or not a string", "add `id = \"go\"` at the top level"));
        }
        else if (!IsValidIdentifier(id))
        {
            errors.Add(new ManifestError(
                "id",
                sourceName,
                $"'{id}' is not a valid ecosystem id",
                "use lower-case letters, digits and dashes only, e.g. \"rust\""));
        }

        string displayName = root.GetString("display_name") ?? id ?? string.Empty;
        var locations = new List<LocationRecord>();
        IReadOnlyList<TomlTable> declared = root.GetTableArray("location");

        if (declared.Count == 0)
        {
            errors.Add(new ManifestError(
                "location",
                sourceName,
                "declares no locations",
                "add at least one [[location]] block"));
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (TomlTable table in declared)
        {
            string locationId = table.GetString("id") ?? "(unnamed)";
            string where = $"{sourceName} [{locationId}]";

            if (!seenIds.Add(locationId))
            {
                errors.Add(new ManifestError(
                    "location.id",
                    where,
                    $"duplicate location id '{locationId}'",
                    "every location id must be unique within a manifest"));
            }

            ValidateLocation(table, where, errors, warnings, locations);
        }

        return new ManifestValidationResult(errors, warnings, schemaVersion, id, displayName, locations, origin, sourceName);
    }

    private static void ValidateLocation(
        TomlTable table,
        string where,
        List<ManifestError> errors,
        List<ManifestWarning> warnings,
        List<LocationRecord> locations)
    {
        // --- id -------------------------------------------------------------------------------
        string id = table.GetString("id") ?? string.Empty;
        if (id.Length == 0)
        {
            errors.Add(new ManifestError("location.id", where, "missing or not a string", "add `id = \"...\"`"));
        }

        // --- kind: closed set ------------------------------------------------------------------
        string? kindText = table.GetString("kind");
        ManifestCacheKind kind;

        kind = default;

        if (kindText is null)
        {
            errors.Add(new ManifestError(
                "location.kind",
                where,
                $"missing (found {table.Describe("kind")})",
                $"set kind to one of: {string.Join(", ", AllowedKinds)}"));
        }
        else if (!TryParseToken(kindText, out kind))
        {
            errors.Add(new ManifestError(
                "location.kind",
                where,
                $"'{kindText}' is not a known cache kind",
                $"use one of: {string.Join(", ", AllowedKinds)}"));
        }

        // --- resolve: closed set --------------------------------------------------------------
        string? resolveText = table.GetString("resolve");
        ResolveModeValue resolve = ResolveModeValue.First;

        if (resolveText is null)
        {
            errors.Add(new ManifestError(
                "location.resolve",
                where,
                "missing",
                "set `resolve = \"first\"` for a single root or \"all\" for a multi-root location"));
        }
        else if (resolveText == "first")
        {
            resolve = ResolveModeValue.First;
        }
        else if (resolveText == "all")
        {
            resolve = ResolveModeValue.All;
        }
        else
        {
            errors.Add(new ManifestError(
                "location.resolve",
                where,
                $"'{resolveText}' is not a valid resolve mode",
                "use \"first\" or \"all\""));
        }

        // --- sources --------------------------------------------------------------------------
        // The manifest key is `sources` (plural), per 10-formats. An earlier version read
        // `GetTableArray("source")`, so EVERY source went unvalidated: no rung was checked for its
        // identifying field, no tool source was required to name a parser, and a bogus `via` in a
        // perfectly real manifest produced no error. The `sources`-missing case below is what
        // actually caught empty manifests — the contents were never looked at.
        var sources = new List<ManifestSource>();
        IReadOnlyList<TomlTable> sourceTables = table.GetTableArray("sources");

        if (sourceTables.Count == 0)
        {
            errors.Add(new ManifestError(
                "location.sources",
                where,
                "declares no resolution sources",
                "add a `sources` array with at least one entry, e.g. `sources = [{ via = \"default\", path = \"{home}/.cache/go\" }]`"));
        }

        foreach (TomlTable source in sourceTables)
        {
            ValidateSource(source, where, errors, sources);
        }

        // --- fingerprint ----------------------------------------------------------------------
        TomlTable? fingerprint = table.GetTable("fingerprint");
        double layoutRatio = 0;
        var entryPatterns = new List<string>();
        var requiredMarkers = new List<string>();

        if (fingerprint is not null)
        {
            if (fingerprint.Has("layout_ratio"))
            {
                if (fingerprint.GetNumber("layout_ratio") is { } ratio)
                {
                    layoutRatio = ratio;

                    // A ratio outside [0,1] is always a mistake, and a typo here would silently disable
                    // or invert a safety gate — so it is an error, not a clamp.
                    if (ratio is < 0 or > 1)
                    {
                        errors.Add(new ManifestError(
                            "fingerprint.layout_ratio",
                            where,
                            $"{ratio.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} is outside 0..1",
                            "layout_ratio is a fraction, e.g. 0.8 means 80% of depth-1 entries must match"));
                    }
                }
                else
                {
                    errors.Add(new ManifestError(
                        "fingerprint.layout_ratio",
                        where,
                        $"expected a number, found {fingerprint.Describe("layout_ratio")}",
                        "write it as a plain number, e.g. 0.8 (no quotes)"));
                }
            }

            entryPatterns.AddRange(fingerprint.GetStringArray("entry_patterns") ?? []);
            requiredMarkers.AddRange(fingerprint.GetStringArray("required_markers") ?? []);

            if (layoutRatio > 0 && entryPatterns.Count == 0)
            {
                errors.Add(new ManifestError(
                    "fingerprint.entry_patterns",
                    where,
                    "layout_ratio is set but entry_patterns is empty",
                    "a ratio with no patterns can never match; list the patterns the ratio is measured against"));
            }

            if (layoutRatio == 0 && entryPatterns.Count > 0)
            {
                warnings.Add(new ManifestWarning(
                    "fingerprint.layout_ratio",
                    where,
                    "entry_patterns are declared but layout_ratio is 0, so no layout check will run"));
            }
        }

        // --- removal ---------------------------------------------------------------------------
        TomlTable? removal = table.GetTable("remove");
        string? removeCommand = removal?.GetString("command");

        if (removal is not null && string.IsNullOrWhiteSpace(removeCommand))
        {
            errors.Add(new ManifestError(
                "remove.command",
                where,
                "present but empty",
                "give the native command, or remove the whole `remove` block"));
        }

        bool installerOwned = table.GetBool("installer_owned") ?? false;

        // Installer-owned roots belong to an installer, not the user. Offering to run a removal command
        // against one would be proposing something that damages a toolchain the user did not install.
        if (removeCommand is not null && installerOwned)
        {
            errors.Add(new ManifestError(
                "remove.command",
                where,
                "declared on an installer-owned location",
                "installer-owned roots are never cleaned; remove the `remove` block and set installer_owned = true"));
        }

        if (removeCommand is not null && !IsKnownRemoveCommand(removeCommand))
        {
            // Not an allowlist failure — a warning, because a valid native route for a new ecosystem
            // should not require a core change (OCP). It is surfaced in the audit per 10-formats.
            warnings.Add(new ManifestWarning(
                "remove.command",
                where,
                $"'{Shorten(removeCommand)}' is not a command coppice recognises; it will be shown but never run automatically"));
        }

        // --- risk ceiling ---------------------------------------------------------------------
        string? ceiling = table.GetString("risk_ceiling");
        if (ceiling is not null && !AllowedRiskCeilings.Contains(ceiling, StringComparer.Ordinal))
        {
            errors.Add(new ManifestError(
                "risk_ceiling",
                where,
                $"'{ceiling}' is not permitted for a declarative location",
                $"declarative locations may be capped at: {string.Join(", ", AllowedRiskCeilings)} — a higher tier (e.g. \"manual\") needs code, not config"));
        }

        double penalty = table.GetNumber("confidence_penalty") ?? 0;
        if (penalty is < 0 or > 1)
        {
            errors.Add(new ManifestError(
                "confidence_penalty",
                where,
                $"{penalty} is outside 0..1",
                "0 means full confidence, 1 means no confidence; use a fraction"));
        }

        locations.Add(new LocationRecord(
            id,
            kindText ?? string.Empty,
            kind,
            resolve,
            table.GetString("layout") ?? string.Empty,
            sources,
            new ManifestFingerprint { LayoutRatio = layoutRatio, EntryPatterns = entryPatterns, RequiredMarkers = requiredMarkers },
            removeCommand,
            penalty,
            installerOwned));
    }

    private static void ValidateSource(TomlTable source, string where, List<ManifestError> errors, List<ManifestSource> sources)
    {
        string? viaText = source.GetString("via");

        if (viaText is null)
        {
            errors.Add(new ManifestError(
                "source.via",
                where,
                $"missing (found {source.Describe("via")})",
                $"via is required and must be one of: {string.Join(", ", AllowedVia)}"));
            return;
        }

        ResolvedViaValue via = default;
        if (!TryParseToken(viaText, out via))
        {
            errors.Add(new ManifestError(
                "source.via",
                where,
                $"'{viaText}' is not a valid resolution source",
                $"via is a closed set: {string.Join(", ", AllowedVia)}"));
            return;
        }

        // Each rung needs the one field that identifies it. A `tool` source with no command, or an
        // `env` source with no variable, cannot answer — accepting it would mean the location silently
        // resolves from a later rung and the author never learns their entry was ignored.
        string? required = via switch
        {
            ResolvedViaValue.Tool => "run",
            ResolvedViaValue.Env => "var",
            ResolvedViaValue.Config => "key",
            ResolvedViaValue.Pin => "path",
            ResolvedViaValue.Default => "path",
            ResolvedViaValue.Registry => "registry_value",
            ResolvedViaValue.OsFile => "file",
            _ => null,
        };

        if (required is not null)
        {
            string? value = source.GetString(required);
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new ManifestError(
                    $"source.{required}",
                    where,
                    $"a '{viaText}' source requires '{required}' (found {source.Describe(required)})",
                    $"add `{required} = \"...\"` to this source, or change via to a rung that does not need it"));
            }
        }

        ManifestParserKind? parse = null;
        string? parseText = source.GetString("parse");

        if (parseText is not null)
        {
            ManifestParserKind parsed = default;
            if (!TryParseToken(parseText, out parsed))
            {
                errors.Add(new ManifestError(
                    "source.parse",
                    where,
                    $"'{parseText}' is not a built-in parser",
                    $"parse is a closed set: {string.Join(", ", AllowedParsers)}"));
            }
            else
            {
                parse = parsed;

                if (parsed == ManifestParserKind.Regex)
                {
                    errors.Add(new ManifestError(
                        "source.parse",
                        where,
                        "the 'regex' parser requires a 'pattern' coppice cannot verify at load time",
                        "regex parsing is flagged in the audit per 10-formats; use a built-in parser, or add the pattern to the ecosystem as a code plugin"));
                }
            }
        }
        else if (via == ResolvedViaValue.Tool)
        {
            // A tool source without a parser means "run this and hope". The spec's parsers are the
            // only sanctioned way to read a tool's output, so this is refused rather than defaulted.
            errors.Add(new ManifestError(
                "source.parse",
                where,
                $"a 'tool' source requires 'parse' (found {source.Describe("parse")})",
                $"add `parse = \"...\"` using one of: {string.Join(", ", AllowedParsers)}"));
        }

        string? regexPattern = source.GetString("pattern");
        if (regexPattern is not null && parse != ManifestParserKind.Regex)
        {
            errors.Add(new ManifestError(
                "source.pattern",
                where,
                "'pattern' is only meaningful with parse = \"regex\"",
                "remove the pattern, or set parse = \"regex\""));
        }

        sources.Add(new ManifestSource
        {
            Via = via,
            Run = source.GetString("run"),
            Var = source.GetString("var"),
            Key = source.GetString("key"),
            Path = source.GetString("path"),
            RegistryValue = source.GetString("registry_value"),
            File = source.GetString("file"),
            Parse = parse,
            ReportInactive = source.GetBool("report_inactive") ?? true,
        });
    }

    /// <summary>
    /// Maps a kebab-case manifest token onto an enum member.
    /// <para>
    /// This exists because <c>Enum.TryParse("package_cache")</c> does NOT match <c>PackageCache</c>:
    /// TryParse ignores case but not underscores. The failure is quiet — it returns false and the
    /// validator reports "not a known cache kind" for a value that is perfectly valid — so the lookup
    /// is spelled out instead of trusted.
    /// </para>
    /// </summary>
    private static bool TryParseToken<T>(string token, out T value) where T : struct, Enum
    {
        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Kebab(candidate.ToString()), token, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Turns <c>OsFile</c> into <c>os-file</c> so the comparison runs on the manifest's own spelling.</summary>
    private static string Kebab(string pascal)
    {
        var builder = new System.Text.StringBuilder(pascal.Length + 4);

        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// An ecosystem id must start with a lower-case letter and contain only lower-case letters, digits
    /// and dashes. Leading digits are refused on purpose: "9lives" is a package name, not an ecosystem
    /// name, and an id that begins with a digit reads like a version in a report line and sorts before
    /// every real ecosystem.
    /// </summary>
    private static bool IsValidIdentifier(string id)
    {
        if (id.Length == 0 || !char.IsAsciiLetterLower(id[0]))
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return !id.EndsWith('-');
    }

    /// <summary>
    /// Native removal commands coppice knows. A miss is a warning, not an error: OCJ requires a new
    /// ecosystem to be addable without core changes, and refusing unknown-but-valid commands would
    /// break that. The list exists so the audit can flag what it does not recognise.
    /// </summary>
    private static bool IsKnownRemoveCommand(string command)
    {
        string head = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return head is "dotnet" or "go" or "cargo" or "rustup" or "npm" or "yarn" or "pnpm" or "nuget";
    }

    private static string Shorten(string text) =>
        text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 37), "...");
}

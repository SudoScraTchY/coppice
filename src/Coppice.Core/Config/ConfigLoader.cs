namespace Coppice.Core.Config;

/// <summary>
/// Loads and validates the user config (T-029, FR-22, NFR-10, NFR-12).
/// <para>
/// Two rules shape the design.
/// </para>
/// <para>
/// <b>Every problem is reported, not the first.</b> A user who mistyped three keys should be told about
/// three keys, because fixing one and re-running to discover the next two is the loop that makes people
/// give up on configuration entirely.
/// </para>
/// <para>
/// <b>An unknown key is an error.</b> A typo'd <c>keep_latets</c> silently means "use the default", and a
/// default that disables keep-latest-N is worse than a crash. Refusing loudly is the only safe response
/// to a key this build does not understand — it is also what makes adding a key later safe, since a file
/// written for a NEWER schema will be rejected here rather than half-honoured.
/// </para>
/// </summary>
public static class ConfigLoader
{
    /// <summary>Every key this build understands. Anything else in the file is refused.</summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "version",
        "projects.roots",
        "policy.preset",
        "policy.keep_latest",
        "policy.protect_referenced",
        "policy.protect_unknown_usage",
        "quarantine.enabled",
        "quarantine.directory",
        "quarantine.retention_days",
        "exclude.paths",
        "exclude.names",
    };

    /// <summary>Preset names, closed. An unrecognised preset is a typo, not a new policy.</summary>
    private static readonly HashSet<string> KnownPresets =
        new(StringComparer.Ordinal) { "conservative", "default", "aggressive" };

    /// <summary>Keys under [pins] — the table is open, since its keys are location ids.</summary>
    private const string PinsPrefix = "pins.";

    /// <summary>
    /// Loads from text. <paramref name="origin"/> is used in diagnostics so a user knows which file a
    /// message is about — "line 7" is useless when two config files exist.
    /// </summary>
    public static ConfigLoadResult Load(string text, string origin = "config.toml")
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            // An empty file is legitimate: it means "all defaults". Refusing it would make a
            // `touch config.toml` fail, which is a perfectly reasonable way to start a config.
            return ConfigLoadResult.Success(CoppiceConfig.Defaults with { LoadedFromFile = true });
        }

        ConfigDocument document;
        try
        {
            document = ConfigDocument.Parse(text);
        }
        catch (ConfigParseException ex)
        {
            return ConfigLoadResult.Fail(
                ConfigFailure.Malformed,
                $"{origin} could not be parsed: {ex.Detail}",
                [new ConfigDiagnostic(origin, ex.LineNumber, ex.Detail, "Check the line above; TOML expects `key = value` and `[table]` headers.")]);
        }

        var diagnostics = new List<ConfigDiagnostic>();
        CollectUnknownKeys(document, origin, diagnostics);
        CheckSchemaVersion(document, origin, diagnostics);

        // Every check contributes before anything returns. An early return after the first failing check
        // would mean a config with three typos is reported one at a time across three runs — which is
        // exactly the loop that makes people give up on configuration.
        CoppiceConfig config = Build(document, diagnostics);

        if (diagnostics.Count > 0)
        {
            // Classify by what the diagnostics actually say, not by which check ran first. The most
            // specific cause wins, so a file with a typo AND a bad value reports the typo — the thing the
            // user is most likely to have done and least likely to notice.
            bool versionProblem = diagnostics.Any(d => string.Equals(d.Field, "version", StringComparison.Ordinal));
            bool keyProblem = diagnostics.Any(d => d.Field.StartsWith("unknown key", StringComparison.Ordinal));

            ConfigFailure failure = versionProblem
                ? ConfigFailure.UnsupportedSchemaVersion
                : keyProblem
                    ? ConfigFailure.UnknownKey
                    : ConfigFailure.InvalidValue;

            return ConfigLoadResult.Fail(failure, Summarize(diagnostics), diagnostics);
        }

        return ConfigLoadResult.Success(config);
    }

    /// <summary>Unknown keys, reported individually so each one names itself.</summary>
    private static void CollectUnknownKeys(ConfigDocument document, string origin, List<ConfigDiagnostic> diagnostics)
    {
        foreach (string key in document.Keys.Order(StringComparer.Ordinal))
        {
            // A pin's key is a location id, so [pins] is open by design.
            if (KnownKeys.Contains(key) || key.StartsWith(PinsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            diagnostics.Add(new ConfigDiagnostic(
                $"unknown key '{key}'",
                document.LineOf(key),
                $"'{key}' is not a setting this build understands.",
                "Check the spelling against the documented config keys; an unrecognised key is refused rather than ignored, because silently defaulting it would change what gets deleted."));
        }
    }

    private static void CheckSchemaVersion(ConfigDocument document, string origin, List<ConfigDiagnostic> diagnostics)
    {
        int? declared = document.Int("version");

        if (declared is null)
        {
            return;
        }

        if (declared == CoppiceConfig.SupportedSchemaVersion)
        {
            return;
        }

        diagnostics.Add(new ConfigDiagnostic(
            "version",
            document.LineOf("version"),
            $"{origin} declares config version {declared}, but this build understands version {CoppiceConfig.SupportedSchemaVersion}.",
            declared > CoppiceConfig.SupportedSchemaVersion
                ? "Upgrade coppice. A newer config may set options this build would silently ignore."
                : "This config was written by an older version. Add a [migration] section, or remove settings this build does not know."));
    }

    private static CoppiceConfig Build(ConfigDocument document, List<ConfigDiagnostic> diagnostics)
    {
        var config = new CoppiceConfig { LoadedFromFile = true };

        if (document.Int("version") is { } version)
        {
            config = config with { SchemaVersion = version };
        }

        if (document.StringArray("projects.roots") is { } roots)
        {
            config = config with { ProjectRoots = roots };
        }

        PolicySettings policy = ReadPolicy(document, diagnostics);
        config = config with { Policy = policy };

        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in document.Keys.Order(StringComparer.Ordinal))
        {
            if (!key.StartsWith(PinsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string locationId = key[PinsPrefix.Length..];

            if (locationId.Length == 0)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    key,
                    document.LineOf(key),
                    "A pin needs a location id before the '='.",
                    "For example: [pins]\n\"nuget-packages\" = \"D:\\\\nuget\""));
                continue;
            }

            if (document.String(key) is { } path && path.Length > 0)
            {
                pins[locationId] = path;
            }
            else
            {
                diagnostics.Add(new ConfigDiagnostic(
                    key,
                    document.LineOf(key),
                    $"Pin for '{locationId}' has no path.",
                    "A pin must be a quoted absolute path."));
            }
        }

        return config with
        {
            Pins = pins,
            ExcludedPaths = document.StringArray("exclude.paths") ?? [],
            ExcludedNames = document.StringArray("exclude.names") ?? [],
            Quarantine = ReadQuarantine(document, diagnostics),
        };
    }

    private static PolicySettings ReadPolicy(ConfigDocument document, List<ConfigDiagnostic> diagnostics)
    {
        var settings = new PolicySettings();

        if (document.String("policy.preset") is { } preset)
        {
            if (!KnownPresets.Contains(preset))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "policy.preset",
                    document.LineOf("policy.preset"),
                    $"'{preset}' is not a known preset.",
                    $"Expected one of: {string.Join(", ", KnownPresets.Order(StringComparer.Ordinal))}."));
            }
            else
            {
                settings = settings with { Preset = preset };
            }
        }

        // KeepLatest is nullable on purpose: absent means "inherit the preset's", and collapsing that to 0
        // would disable keep-latest-N for every config that omits the line.
        if (document.Contains("policy.keep_latest"))
        {
            int? keep = document.Int("policy.keep_latest");

            if (keep is null)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "policy.keep_latest",
                    document.LineOf("policy.keep_latest"),
                    "Expected a whole number.",
                    $"Found: {document.SourceLine("policy.keep_latest", string.Empty)}"));
            }
            else if (keep < 0)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "policy.keep_latest",
                    document.LineOf("policy.keep_latest"),
                    $"keep_latest cannot be negative (got {keep}).",
                    "Use 0 to disable the rule, or a positive number of versions to keep."));
            }
            else
            {
                settings = settings with { KeepLatest = keep };
            }
        }

        if (document.Bool("policy.protect_referenced") is { } protectReferenced)
        {
            settings = settings with { ProtectReferenced = protectReferenced };
        }

        if (document.Bool("policy.protect_unknown_usage") is { } protectUnknown)
        {
            settings = settings with { ProtectUnknownUsage = protectUnknown };
        }

        return settings;
    }

    private static QuarantineSettings ReadQuarantine(ConfigDocument document, List<ConfigDiagnostic> diagnostics)
    {
        var settings = new QuarantineSettings();

        if (document.Bool("quarantine.enabled") is { } enabled)
        {
            settings = settings with { Enabled = enabled };
        }

        if (document.String("quarantine.directory") is { } directory)
        {
            settings = settings with { Directory = directory.Length == 0 ? null : directory };
        }

        if (document.Contains("quarantine.retention_days"))
        {
            if (document.Int("quarantine.retention_days") is { } days)
            {
                if (days < 0)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "quarantine.retention_days",
                        document.LineOf("quarantine.retention_days"),
                        $"retention_days cannot be negative (got {days}).",
                        "Use 0 to keep quarantined items indefinitely."));
                }
                else
                {
                    settings = settings with { RetentionDays = days };
                }
            }
            else
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "quarantine.retention_days",
                    document.LineOf("quarantine.retention_days"),
                    "Expected a whole number of days.",
                    "Use 0 to keep quarantined items indefinitely."));
            }
        }

        return settings;
    }

    private static string Summarize(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        string head = diagnostics.Count == 1
            ? "1 problem was found"
            : $"{diagnostics.Count} problems were found";

        return $"{head}:\n  " + string.Join("\n  ", diagnostics.Select(d => d.ToString()));
    }
}

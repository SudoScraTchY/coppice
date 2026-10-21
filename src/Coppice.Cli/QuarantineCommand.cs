using System.Globalization;
using System.Text.Json;
using Coppice.Adapters;
using Coppice.Core.Apply;
using Coppice.Core.Quarantine;
using Coppice.Ports;

namespace Coppice.Cli;

/// <summary>The flags and subcommand of the <c>quarantine</c> verb.</summary>
public sealed record QuarantineFlags(
    string Subcommand,
    string? EntryId,
    TimeSpan OlderThan,
    bool Yes,
    bool DryRun,
    bool Json,
    bool Ci)
{
    public static QuarantineFlags Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var positionals = new List<string>();
        TimeSpan olderThan = TimeSpan.FromDays(30);
        bool olderThanGiven = false;
        bool yes = false;
        bool dryRun = false;
        bool json = false;
        bool ci = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--older-than":
                    olderThan = ParseAge(Value(args, ref i, "--older-than"));
                    olderThanGiven = true;
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--ci":
                    // FR-21: machine-readable and non-interactive.
                    ci = true;
                    break;
                case "--no-color":
                    // Accepted and ignored: this renderer never emits color.
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new ArgumentException($"unknown option '{args[i]}'");
                    }

                    positionals.Add(args[i]);
                    break;
            }
        }

        if (positionals.Count == 0)
        {
            throw new ArgumentException("quarantine needs a subcommand: list, restore or prune");
        }

        string subcommand = positionals[0].ToLowerInvariant();
        string? entryId = null;

        switch (subcommand)
        {
            case "list":
                if (positionals.Count > 1)
                {
                    throw new ArgumentException($"list takes no arguments, got '{positionals[1]}'");
                }

                if (olderThanGiven)
                {
                    throw new ArgumentException("--older-than belongs to prune, not list");
                }

                if (dryRun)
                {
                    throw new ArgumentException("--dry-run belongs to prune, not list");
                }

                break;
            case "restore":
                if (positionals.Count != 2)
                {
                    throw new ArgumentException("restore needs exactly one entry id");
                }

                entryId = positionals[1];
                if (olderThanGiven)
                {
                    throw new ArgumentException("--older-than belongs to prune, not restore");
                }

                if (dryRun)
                {
                    throw new ArgumentException("--dry-run belongs to prune, not restore");
                }

                break;
            case "prune":
                if (positionals.Count > 1)
                {
                    throw new ArgumentException($"prune takes no entry id, got '{positionals[1]}'");
                }

                break;
            default:
                throw new ArgumentException($"unknown subcommand '{positionals[0]}'; choose list, restore or prune");
        }

        return new QuarantineFlags(subcommand, entryId, olderThan, yes, dryRun, json, ci);
    }

    private static string Value(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{option} needs a value");
        }

        return args[++index];
    }

    /// <summary>
    /// Ages are days by default, with <c>d</c>, <c>h</c> and <c>m</c> suffixes. Zero means "everything",
    /// which is explicit rather than a hidden default.
    /// </summary>
    private static TimeSpan ParseAge(string raw)
    {
        string text = raw.Trim();
        char suffix = text.Length > 0 ? char.ToLowerInvariant(text[^1]) : '\0';
        double multiplier = suffix switch
        {
            'd' => 1,
            'h' => 1.0 / 24,
            'm' => 1.0 / (24 * 60),
            _ => 1,
        };

        string number = suffix is 'd' or 'h' or 'm' ? text[..^1] : text;

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
            || !double.IsFinite(amount)
            || amount < 0)
        {
            throw new ArgumentException($"'{raw}' is not a non-negative age; use e.g. 30d, 12h or 90m");
        }

        try
        {
            return TimeSpan.FromDays(amount * multiplier);
        }
        catch (OverflowException)
        {
            throw new ArgumentException($"'{raw}' is too large to be an age");
        }
    }
}

/// <summary>
/// <c>coppice quarantine list|restore|prune</c> (T-044, FR-14, S-11).
/// <para>
/// The quarantine is the undo surface. <c>list</c> is read-only; <c>restore</c> is a mutation that goes
/// through <see cref="QuarantineRestore"/>, so it re-runs the hash, containment and occupancy checks and
/// lands in the audit log; <c>prune</c> is the one operation in Coppice that permanently discards data, so
/// it names what it will delete, requires a typed confirmation (or <c>--yes</c>), and audits every discard.
/// </para>
/// <para>
/// No container, daemon or helper process is involved: these commands operate on the user's own files and
/// state directly, through the filesystem port.
/// </para>
/// </summary>
public sealed class QuarantineCommand
{
    private const int ItemsShown = 10;

    private readonly IFileSystem _fs;
    private readonly IEnvironment _env;
    private readonly IStateStore _state;
    private readonly IAppendLog _log;
    private readonly IApplyConsole _console;
    private readonly IClock _clock;

    public QuarantineCommand(
        IFileSystem fs,
        IEnvironment env,
        IStateStore state,
        IAppendLog log,
        IApplyConsole console,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(console);

        _fs = fs;
        _env = env;
        _state = state;
        _log = log;
        _console = console;
        _clock = clock ?? new SystemClock();
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        QuarantineFlags flags;
        try
        {
            flags = QuarantineFlags.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }

        try
        {
            return Execute(flags);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _console.WriteError($"coppice: {ex.Message}");
            return ExitCodes.RuntimeError;
        }
    }

    private int Execute(QuarantineFlags flags)
    {
        bool canAsk = _console.IsInteractive && !flags.Ci;

        // Same gate as apply: a mutating command that cannot ask must be told --yes before it reads the
        // index. A prune dry run promises not to mutate, so it is allowed to proceed without it.
        if (flags.Subcommand is "restore" or "prune"
            && !(flags.Subcommand == "prune" && flags.DryRun)
            && !canAsk
            && !flags.Yes)
        {
            return Usage(
                $"quarantine {flags.Subcommand} needs --yes when it cannot ask "
                + "(stdin or stdout is redirected, or --ci).");
        }

        QuarantineServices services = ApplyWiring.CreateQuarantine(_fs, _env, _state, _log, _clock);

        return flags.Subcommand switch
        {
            "list" => List(services, flags),
            "restore" => Restore(services, flags, canAsk),
            _ => Prune(services, flags, canAsk),
        };
    }

    private int List(QuarantineServices services, QuarantineFlags flags)
    {
        IReadOnlyList<QuarantineEntry> entries = services.Index.ListAsync().GetAwaiter().GetResult();

        if (flags.Json || flags.Ci)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                count = entries.Count,
                totalBytes = entries.Aggregate(0UL, (sum, entry) => checked(sum + entry.SizeBytes)),
                entries = entries.Select(entry => new
                {
                    id = entry.EntryId,
                    quarantinedAtUtc = entry.QuarantinedAtUtc,
                    sizeBytes = entry.SizeBytes,
                    ecosystem = entry.Ecosystem,
                    kind = entry.Kind,
                    name = entry.Name,
                    version = entry.Version,
                    itemId = entry.ItemId,
                    locationId = entry.LocationId,
                    root = entry.Root,
                    originalPath = entry.OriginalPath,
                    quarantinePath = entry.QuarantinePath,
                    contentHash = entry.ContentHash,
                    contentPresent = ContentExists(entry),
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));

            return ExitCodes.Success;
        }

        if (entries.Count == 0)
        {
            _console.WriteLine("coppice quarantine — nothing is in quarantine.");
            return ExitCodes.Success;
        }

        ulong total = entries.Aggregate(0UL, (sum, entry) => checked(sum + entry.SizeBytes));
        _console.WriteLine($"coppice quarantine — {entries.Count} item(s), {ReportTotals.FormatBytes(total)}");
        _console.WriteLine(string.Empty);

        foreach (QuarantineEntry entry in entries)
        {
            string missing = ContentExists(entry) ? string.Empty : "  [content missing]";
            _console.WriteLine(
                $"  {entry.EntryId}  {entry.QuarantinedAtUtc:yyyy-MM-dd HH:mm:ss'Z'}  "
                + $"{ReportTotals.FormatBytes(entry.SizeBytes),8}  {ItemLabel(entry)}{missing}");
            _console.WriteLine($"    from: {entry.OriginalPath}");
        }

        _console.WriteLine(string.Empty);
        _console.WriteLine("Restore one with: coppice quarantine restore <id>");
        return ExitCodes.Success;
    }

    private int Restore(QuarantineServices services, QuarantineFlags flags, bool canAsk)
    {
        QuarantineEntry? entry = services.Index.GetAsync(flags.EntryId!).GetAwaiter().GetResult();

        if (entry is null)
        {
            _console.WriteError($"coppice: no quarantine entry '{flags.EntryId}'.");
            return ExitCodes.RuntimeError;
        }

        if (!ContentExists(entry))
        {
            _console.WriteError(
                $"coppice: the quarantined copy of '{entry.Name} {entry.Version}' is gone — it was pruned "
                + "or removed by hand.");
            return ExitCodes.RuntimeError;
        }

        if (!flags.Yes && canAsk)
        {
            _console.WriteLine($"coppice quarantine restore — {entry.EntryId}");
            _console.WriteLine($"  {ItemLabel(entry)} · {ReportTotals.FormatBytes(entry.SizeBytes)}");
            _console.WriteLine($"  from: {entry.OriginalPath}");
            _console.WritePrompt($"Type 'yes' to restore it to '{entry.OriginalPath}': ");

            string? answer = _console.ReadLine();
            if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                if (flags.Json || flags.Ci)
                {
                    _console.WriteLine(JsonSerializer.Serialize(new
                    {
                        schema = 1,
                        restored = false,
                        declined = true,
                        entryId = entry.EntryId,
                        path = (string?)null,
                        reason = (string?)null,
                        message = "Declined. Nothing was moved.",
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    _console.WriteLine("Declined. Nothing was moved.");
                }

                return ExitCodes.Success;
            }
        }

        RestoreResult result = services.Restore.RestoreAsync(entry.EntryId).GetAwaiter().GetResult();

        if (flags.Json || flags.Ci)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                restored = result.Restored,
                declined = false,
                entryId = entry.EntryId,
                path = result.RestoredPath,
                reason = result.Failure?.ToString(),
                message = result.Message,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (result.Restored)
        {
            _console.WriteLine($"Restored {entry.Name} {entry.Version} to {result.RestoredPath}");
            _console.WriteLine("The restore is in the audit log.");
        }
        else
        {
            _console.WriteError($"coppice: {result.Message}");
        }

        return result.Restored ? ExitCodes.Success : RestoreExit(result.Failure);
    }

    private int Prune(QuarantineServices services, QuarantineFlags flags, bool canAsk)
    {
        IReadOnlyList<QuarantineEntry> all = services.Index.ListAsync().GetAwaiter().GetResult();
        DateTimeOffset cutoff = _clock.UtcNow - flags.OlderThan;
        List<QuarantineEntry> eligible =
        [
            .. all.Where(entry => entry.QuarantinedAtUtc <= cutoff)
                .OrderBy(entry => entry.QuarantinedAtUtc)
                .ThenBy(entry => entry.EntryId, StringComparer.Ordinal),
        ];

        ulong eligibleBytes = eligible.Aggregate(0UL, (sum, entry) => checked(sum + entry.SizeBytes));

        if (eligible.Count == 0)
        {
            return ReportNothingToPrune(flags);
        }

        if (flags.DryRun)
        {
            ReportPrune(flags, eligible, [], eligibleBytes, prunedBytes: 0, failedCount: 0);
            if (!(flags.Json || flags.Ci))
            {
                _console.WriteLine("Dry run: nothing was deleted.");
            }

            return ExitCodes.Success;
        }

        if (!flags.Yes && canAsk)
        {
            _console.WriteLine($"coppice quarantine prune — older than {AgeNote(flags.OlderThan)}");
            _console.WriteLine(
                $"{eligible.Count} item(s), {ReportTotals.FormatBytes(eligibleBytes)} will be DELETED PERMANENTLY:");
            foreach (QuarantineEntry entry in eligible.Take(ItemsShown))
            {
                _console.WriteLine($"  {entry.EntryId}  {ItemLabel(entry)}  {entry.OriginalPath}");
            }

            if (eligible.Count > ItemsShown)
            {
                _console.WriteLine($"  … {eligible.Count - ItemsShown} more");
            }

            _console.WritePrompt($"Type 'yes' to delete these {eligible.Count} item(s) permanently: ");
            string? answer = _console.ReadLine();
            if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                if (flags.Json || flags.Ci)
                {
                    _console.WriteLine(JsonSerializer.Serialize(new
                    {
                        schema = 1,
                        dryRun = false,
                        declined = true,
                        olderThan = AgeJson(flags),
                        eligibleCount = eligible.Count,
                        prunedCount = 0,
                        prunedBytes = 0UL,
                        failedCount = 0,
                        message = "Declined. Nothing was deleted.",
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    _console.WriteLine("Declined. Nothing was deleted.");
                }

                return ExitCodes.Success;
            }
        }

        IReadOnlyList<QuarantineEntry> pruned = services.Restore
            .PruneAsync(flags.OlderThan)
            .GetAwaiter()
            .GetResult();

        HashSet<string> prunedIds = [.. pruned.Select(entry => entry.EntryId)];
        List<QuarantineEntry> failed = [.. eligible.Where(entry => !prunedIds.Contains(entry.EntryId))];
        ulong prunedBytes = pruned.Aggregate(0UL, (sum, entry) => checked(sum + entry.SizeBytes));

        ReportPrune(flags, eligible, pruned, eligibleBytes, prunedBytes, failed.Count);

        if (failed.Count > 0)
        {
            _console.WriteError(
                $"coppice: {failed.Count} item(s) could not be deleted and remain in quarantine; check permissions.");
            return ExitCodes.RuntimeError;
        }

        return ExitCodes.Success;
    }

    private int ReportNothingToPrune(QuarantineFlags flags)
    {
        if (flags.Json || flags.Ci)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                dryRun = flags.DryRun,
                declined = false,
                olderThan = AgeJson(flags),
                eligibleCount = 0,
                prunedCount = 0,
                prunedBytes = 0UL,
                failedCount = 0,
                message = "Nothing is older than the given age.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            _console.WriteLine(
                $"Nothing in quarantine is older than {AgeNote(flags.OlderThan)}. Nothing was deleted.");
        }

        return ExitCodes.Success;
    }

    private void ReportPrune(
        QuarantineFlags flags,
        IReadOnlyList<QuarantineEntry> eligible,
        IReadOnlyList<QuarantineEntry> pruned,
        ulong eligibleBytes,
        ulong prunedBytes,
        int failedCount)
    {
        if (flags.Json || flags.Ci)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                dryRun = flags.DryRun,
                declined = false,
                olderThan = AgeJson(flags),
                eligibleCount = eligible.Count,
                eligibleBytes,
                prunedCount = pruned.Count,
                prunedBytes,
                failedCount,
                pruned = pruned.Select(entry => new
                {
                    id = entry.EntryId,
                    sizeBytes = entry.SizeBytes,
                    originalPath = entry.OriginalPath,
                    quarantinePath = entry.QuarantinePath,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (flags.DryRun)
        {
            _console.WriteLine($"coppice quarantine prune — older than {AgeNote(flags.OlderThan)}");
            _console.WriteLine(
                $"Would delete {eligible.Count} item(s), reclaiming {ReportTotals.FormatBytes(eligibleBytes)}.");
            return;
        }

        if (pruned.Count == 0)
        {
            _console.WriteLine("Nothing was deleted.");
            return;
        }

        _console.WriteLine(
            $"Deleted {pruned.Count} item(s), reclaimed {ReportTotals.FormatBytes(prunedBytes)}. "
            + "The discards are in the audit log.");
    }

    private bool ContentExists(QuarantineEntry entry) =>
        _fs.DirectoryExists(entry.QuarantinePath) || _fs.FileExists(entry.QuarantinePath);

    private static string ItemLabel(QuarantineEntry entry) =>
        $"{entry.Ecosystem} {entry.Kind} {entry.Name} {entry.Version}".Trim();

    private static string AgeJson(QuarantineFlags flags) =>
        flags.OlderThan.ToString("c", CultureInfo.InvariantCulture);

    private static string AgeNote(TimeSpan age)
    {
        if (age <= TimeSpan.Zero)
        {
            return "any age";
        }

        if (age.TotalDays >= 1)
        {
            return Amount(age.TotalDays, "day");
        }

        if (age.TotalHours >= 1)
        {
            return Amount(age.TotalHours, "hour");
        }

        return Amount(age.TotalMinutes, "minute");
    }

    private static string Amount(double value, string unit) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{value:0.##} {unit}{(value == 1 ? string.Empty : "s")}");

    private static int RestoreExit(RestoreFailure? failure) => failure switch
    {
        // A refusal that protected a destination is a safety abort, not a runtime error: the tool worked,
        // and what it found was a reason not to move anything.
        RestoreFailure.DestinationOccupied
            or RestoreFailure.DestinationUnsafe
            or RestoreFailure.ContentAltered => ExitCodes.SafetyAbort,
        _ => ExitCodes.RuntimeError,
    };

    private int Usage(string message)
    {
        _console.WriteError($"coppice: {message}");
        _console.WriteError("Run 'coppice help' for usage.");
        return ExitCodes.UsageError;
    }
}

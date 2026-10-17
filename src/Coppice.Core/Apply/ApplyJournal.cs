using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppice.Core.Apply;

/// <summary>
/// One line of the write-ahead journal (T-034, FR-16, S-16).
/// <para>
/// A flat record rather than a type per phase, so a reader written today can still parse a journal that a
/// later build extended: unknown fields are ignored, and only <see cref="Phase"/> is required.
/// </para>
/// </summary>
public sealed record JournalRecord
{
    public const string BeginPhase = "begin";
    public const string ResumePhase = "resume";
    public const string IntentPhase = "intent";
    public const string OutcomePhase = "outcome";
    public const string EndPhase = "end";

    public required string Phase { get; init; }

    public DateTimeOffset Ts { get; init; }

    public string? PlanId { get; init; }

    /// <summary>The plan checksum on begin/resume. A journal only ever resumes the plan it was written for.</summary>
    public string? Checksum { get; init; }

    public int? Steps { get; init; }

    public string? StepId { get; init; }

    /// <summary><c>quarantine</c> or <c>native</c>.</summary>
    public string? Action { get; init; }

    public string? From { get; init; }

    /// <summary>Where a path step's item goes. Derived and deterministic, so a resume can look for it.</summary>
    public string? To { get; init; }

    public string? Root { get; init; }

    public string? Result { get; init; }

    public string? Refusal { get; init; }

    public string? Message { get; init; }

    public ulong? Bytes { get; init; }

    public int? Ok { get; init; }

    public int? Failed { get; init; }

    public int? Skipped { get; init; }
}

/// <summary>What a journal says happened, as far as it can be trusted.</summary>
public sealed record JournalState
{
    public static JournalState Missing { get; } = new() { Exists = false };

    public required bool Exists { get; init; }

    /// <summary>A line other than a torn tail failed to parse. Nothing in the journal is trusted then.</summary>
    public bool Corrupt { get; init; }

    public string? CorruptDetail { get; init; }

    /// <summary>The file does not end in a newline, so the next append must start a fresh line first.</summary>
    public bool NeedsSeparator { get; init; }

    public JournalRecord? Begin { get; init; }

    public bool Ended { get; init; }

    /// <summary>The latest intent per step id.</summary>
    public IReadOnlyDictionary<string, JournalRecord> Intents { get; init; } = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);

    /// <summary>The outcome per step id. A step with an intent and no outcome is the crash window.</summary>
    public IReadOnlyDictionary<string, JournalRecord> Outcomes { get; init; } = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
}

/// <summary>
/// The write-ahead journal for one plan: <c>state/journal/&lt;planId&gt;.jsonl</c> (FR-16).
/// <para>
/// Every mutation is bracketed: an <c>intent</c> line is durable BEFORE the gateway is called, and an
/// <c>outcome</c> line follows it. A kill -9 can therefore leave at most one step with an intent and no
/// outcome, and that step's intent names both places the item can be — which is what lets a resume decide
/// what happened instead of guessing.
/// </para>
/// <para>
/// A kill mid-append tears at most the LAST line. That one is tolerated: it is either an intent whose
/// mutation never started, or an outcome whose step the resume re-derives from the filesystem. A torn line
/// anywhere else means the journal was edited or the disk lied, and the reader reports it as corrupt —
/// apply then refuses (exit 3) rather than resuming from a record it cannot read.
/// </para>
/// </summary>
public sealed class ApplyJournal
{
    public const string KeyPrefix = "journal/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IAppendLog _log;

    public ApplyJournal(IAppendLog log, string planId)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        _log = log;
        Key = KeyFor(planId);
    }

    public string Key { get; }

    public static string KeyFor(string planId) => KeyPrefix + planId + ".jsonl";

    public Task AppendAsync(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _log.AppendLineAsync(Key, Serialize(record));
    }

    /// <summary>Terminates a torn final line, so the next record starts on a line of its own.</summary>
    public Task AppendSeparatorAsync() => _log.AppendLineAsync(Key, string.Empty);

    public async Task<JournalState> ReadAsync() => Parse(await _log.ReadAllTextAsync(Key).ConfigureAwait(false));

    public static string Serialize(JournalRecord record) => JsonSerializer.Serialize(record, JsonOptions);

    public static JournalState Parse(string? text)
    {
        if (text is null)
        {
            return JournalState.Missing;
        }

        string[] lines = text.Split('\n');
        var parsed = new JournalRecord?[lines.Length];
        string? corrupt = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            parsed[i] = TryParse(line);
        }

        var intents = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        JournalRecord? begin = null;
        bool ended = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Length == 0)
            {
                continue;
            }

            JournalRecord? record = parsed[i];
            if (record is null)
            {
                // A torn line is legitimate in exactly two places: at the very end (the kill came mid-append),
                // or immediately before the begin/resume record that a later run wrote after terminating it.
                if (IsLastNonEmpty(lines, i) || NextStartsRun(lines, parsed, i))
                {
                    continue;
                }

                corrupt ??= $"line {i + 1} is not a journal record";
                continue;
            }

            switch (record.Phase)
            {
                case JournalRecord.BeginPhase:
                    if (begin is not null)
                    {
                        corrupt ??= $"line {i + 1} is a second begin record";
                    }

                    begin = record;
                    break;
                case JournalRecord.ResumePhase:
                    if (begin is null)
                    {
                        corrupt ??= $"line {i + 1} resumes a journal that never began";
                    }

                    ended = false;
                    break;
                case JournalRecord.IntentPhase when record.StepId is not null:
                    intents[record.StepId] = record;
                    break;
                case JournalRecord.OutcomePhase when record.StepId is not null:
                    outcomes[record.StepId] = record;
                    break;
                case JournalRecord.EndPhase:
                    ended = true;
                    break;
                default:
                    corrupt ??= $"line {i + 1} has an unknown phase '{record.Phase}'";
                    break;
            }
        }

        if (corrupt is null && begin is null && (intents.Count > 0 || outcomes.Count > 0 || ended))
        {
            corrupt = "the journal has records but no begin record";
        }

        return new JournalState
        {
            Exists = true,
            Corrupt = corrupt is not null,
            CorruptDetail = corrupt,
            NeedsSeparator = text.Length > 0 && !text.EndsWith('\n'),
            Begin = begin,
            Ended = ended,
            Intents = intents,
            Outcomes = outcomes,
        };
    }

    private static JournalRecord? TryParse(string line)
    {
        try
        {
            JournalRecord? record = JsonSerializer.Deserialize<JournalRecord>(line, JsonOptions);
            return record is null || string.IsNullOrEmpty(record.Phase) ? null : record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsLastNonEmpty(string[] lines, int index)
    {
        for (int j = index + 1; j < lines.Length; j++)
        {
            if (lines[j].TrimEnd('\r').Length > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool NextStartsRun(string[] lines, JournalRecord?[] parsed, int index)
    {
        for (int j = index + 1; j < lines.Length; j++)
        {
            if (lines[j].TrimEnd('\r').Length > 0)
            {
                return parsed[j]?.Phase is JournalRecord.ResumePhase or JournalRecord.BeginPhase;
            }
        }

        return false;
    }
}

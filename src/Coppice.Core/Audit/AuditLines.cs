namespace Coppice.Core.Audit;

/// <summary>What reading the whole audit log found.</summary>
/// <param name="Records">The parseable records, oldest first.</param>
/// <param name="TornTail">True when a torn append was found — the last non-empty line, or one the next writer already terminated with a separator.</param>
/// <param name="TornLine">1-based ordinal (over non-empty lines) of the torn line, when <paramref name="TornTail"/>.</param>
/// <param name="UnreadableLine">1-based ordinal of a line that does not parse and is NOT a tolerated torn write. The log was edited.</param>
/// <param name="LineCount">Non-empty lines read.</param>
public sealed record AuditLogContents(
    IReadOnlyList<AuditRecord> Records,
    bool TornTail,
    int? TornLine,
    int? UnreadableLine,
    int LineCount);

/// <summary>
/// The audit log's reader. Like the journal's, it tolerates a torn write — a kill mid-append leaves a
/// prefix with no newline — in exactly two shapes: as the final non-empty line, or as a line the NEXT
/// writer already terminated with an empty separator line when it recovered the chain. Anything else that
/// fails to parse is corruption, and <see cref="AuditVerifier"/> reports it as the error it is.
/// </summary>
public static class AuditLines
{
    public static AuditLogContents Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new AuditLogContents([], false, null, null, 0);
        }

        string[] lines = text.Split('\n');
        var records = new List<AuditRecord>();
        bool torn = false;
        int? tornLine = null;
        int? unreadable = null;
        int ordinal = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            ordinal++;

            if (TryParse(line) is { } record)
            {
                records.Add(record);
                continue;
            }

            if (IsLastNonEmpty(lines, i) || NextLineIsEmpty(lines, i))
            {
                torn = true;
                tornLine ??= ordinal;
            }
            else
            {
                unreadable ??= ordinal;
            }
        }

        return new AuditLogContents(records, torn, tornLine, unreadable, ordinal);
    }

    private static AuditRecord? TryParse(string line)
    {
        try
        {
            AuditRecord? record = System.Text.Json.JsonSerializer.Deserialize<AuditRecord>(line, AuditRecord.JsonOptions);
            return record is null || string.IsNullOrEmpty(record.Action) || string.IsNullOrEmpty(record.Result)
                ? null
                : record;
        }
        catch (System.Text.Json.JsonException)
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

    private static bool NextLineIsEmpty(string[] lines, int index) =>
        index + 1 < lines.Length && lines[index + 1].TrimEnd('\r').Length == 0;
}

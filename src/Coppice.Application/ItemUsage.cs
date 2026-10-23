using Coppice.Core.Domain;

namespace Coppice.Application;

/// <summary>
/// Usage as the scan recorded it. A missing or unparseable fact is Unknown, the safe verdict.
/// One reader for both the plan and the report counters, so they cannot disagree about what a scan proved.
/// </summary>
public static class ItemUsage
{
    public static Usage Of(Item item) =>
        item.Facts.TryGetValue("usage", out string? raw)
            ? Enum.TryParse(raw, ignoreCase: true, out Usage parsed) ? parsed : Usage.Unknown
            : Usage.Unknown;
}

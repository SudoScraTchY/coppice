using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>Usage counts for the scan report (FR-18).</summary>
public sealed record UsageBreakdown(
    int Referenced,
    int Unreferenced,
    int Unknown,
    int Total)
{
    public static UsageBreakdown FromItems(IReadOnlyList<Item> items)
    {
        int refc = 0, unref = 0, unk = 0;
        foreach (Item item in items)
        {
            if (item.Facts.TryGetValue("usage", out string? u))
            {
                if (string.Equals(u, "Referenced", StringComparison.Ordinal))
                {
                    refc++;
                }
                else if (string.Equals(u, "Unreferenced", StringComparison.Ordinal))
                {
                    unref++;
                }
                else if (string.Equals(u, "Unknown", StringComparison.Ordinal))
                {
                    unk++;
                }
            }
            else
            {
                unk++;
            }
        }
        return new UsageBreakdown(refc, unref, unk, items.Count);
    }
}

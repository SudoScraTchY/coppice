using Coppice.Core.Domain;

namespace Coppice.Cli;

/// <summary>
/// Usage counts for the scan report (FR-18).
/// <para>
/// This is the SINGLE place usage is counted. The CSV and Markdown exporters compute their own
/// <see cref="ReportTotals"/> from the same rule, and a third counter would be free to disagree with the
/// other two — and a user comparing two exports of one scan would have no way to tell which was right.
/// </para>
/// <para>
/// An item whose usage fact is missing OR unparseable counts as Unknown. The first is obvious: the scan
/// could not determine it. The second is the same condition arriving in a different shape — a fact
/// reading "Sideways" tells us no more than no fact at all, and counting it as neither referenced nor
/// unreferenced would leave the buckets short of the item total and quietly overstate what is known.
/// </para>
/// </summary>
public sealed record UsageBreakdown(
    int Referenced,
    int Unreferenced,
    int Unknown,
    int Total)
{
    public static UsageBreakdown FromItems(IReadOnlyList<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int refc = 0, unref = 0, unk = 0;

        foreach (Item item in items)
        {
            switch (ReportTotals.UsageOf(item))
            {
                case Usage.Referenced:
                    refc++;
                    break;
                case Usage.Unreferenced:
                    unref++;
                    break;
                default:
                    unk++;
                    break;
            }
        }

        return new UsageBreakdown(refc, unref, unk, items.Count);
    }

    /// <summary>The buckets account for every item. A breakdown that does not add up is not a breakdown.</summary>
    /// <para>
    /// Deliberately a method rather than a property: this record is serialized into the scan report JSON,
    /// and a computed property would appear in the document as a field named "IsConsistent" — putting a
    /// self-check into the tool's output where a consumer would read it as data.
    /// </para>
    public bool IsConsistent() => Referenced + Unreferenced + Unknown == Total;
}

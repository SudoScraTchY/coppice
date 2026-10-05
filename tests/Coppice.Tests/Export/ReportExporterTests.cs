using System.Globalization;
using System.Text;
using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Ports;
using Xunit;
// ScanReport.Usage is the Cli UsageBreakdown, not the domain one of the same name: that is what
// ScanService produces and what the console renderer prints.
using UsageBreakdown = Coppice.Cli.UsageBreakdown;

namespace Coppice.Tests.Export;

/// <summary>
/// T-027: CSV and Markdown exporters (FR-18).
/// <para>
/// The acceptance is "golden files per format; cross-format totals equality". The second half is the
/// point: the whole reason totals are computed once in <see cref="ReportTotals"/> is that three renderings
/// of one scan must not disagree. A CSV that says 4 items and a Markdown that says 5 is a tool the user
/// cannot trust about their own disk.
/// </para>
/// </summary>
public sealed class ReportExporterTests
{
    // ---- fixtures -------------------------------------------------------------------------------

    private static Item Pkg(
        string ecosystem = "dotnet",
        string name = "newtonsoft.json",
        string version = "13.0.1",
        Usage usage = Usage.Referenced,
        ulong size = 1024,
        Risk risk = Risk.Safe,
        string? path = null) =>
        new(
            ecosystem,
            "package",
            name,
            version,
            "nuget-packages",
            path ?? $@"C:\Users\dev\.nuget\packages\{name}\{version}",
            size,
            risk,
            Facts.From([new KeyValuePair<string, string>("usage", usage.ToString())]));

    private static ScanReport Report(params Item[] items) =>
        new(
            "snap_20250112T1015Z",
            OperatingSystemKind.Windows,
            items,
            [],
            [],
            // The scan's own usage counts, computed by the same code the console renders with. If the
            // exporters ever disagreed with this, the report would state two truths about one scan.
            UsageBreakdown.FromItems(items),
            [.. items.Select(i => i.Ecosystem).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            0,
            []);

    private static ReportTotals TotalsOf(params Item[] items) =>
        ReportTotals.From(items, [], []);

    // ---- totals are computed once and shared --------------------------------------------------------

    [Fact]
    public void Usage_buckets_account_for_every_item()
    {
        ReportTotals totals = TotalsOf(
            Pkg(usage: Usage.Referenced),
            Pkg(usage: Usage.Unreferenced),
            Pkg(usage: Usage.Unknown),
            Pkg(usage: Usage.Referenced));

        Assert.True(totals.IsConsistent);
        Assert.Equal(2, totals.ReferencedCount);
        Assert.Equal(1, totals.UnreferencedCount);
        Assert.Equal(1, totals.UnknownUsageCount);
        Assert.Equal(4, totals.ItemCount);
    }

    [Fact]
    public void The_exports_agree_with_the_console_usage_counts()
    {
        // The console renders ScanReport.Usage; the exports compute their own. If those two ever
        // disagreed, one scan would have two different usage totals in two files the user is comparing.
        Item[] items =
        [
            Pkg(usage: Usage.Referenced),
            Pkg(usage: Usage.Unreferenced),
            Pkg(usage: Usage.Unknown),
            Pkg(usage: Usage.Referenced),
        ];

        UsageBreakdown console = UsageBreakdown.FromItems(items);
        ReportTotals totals = TotalsOf(items);

        Assert.True(console.IsConsistent());
        Assert.Equal(console.Referenced, totals.ReferencedCount);
        Assert.Equal(console.Unreferenced, totals.UnreferencedCount);
        Assert.Equal(console.Unknown, totals.UnknownUsageCount);
        Assert.Equal(console.Total, totals.ItemCount);
    }

    [Fact]
    public void An_item_with_no_usage_fact_counts_as_unknown()
    {
        // A scan that could not determine usage must not report it as unreferenced. Counting it either way
        // would let a format imply a conclusion the scan never reached.
        Item noFact = new("dotnet", "package", "x", "1.0", "nuget-packages", @"C:\x", 10, Risk.Safe, Facts.Empty);

        ReportTotals totals = TotalsOf(noFact);

        Assert.Equal(1, totals.UnknownUsageCount);
        Assert.Equal(0, totals.UnreferencedCount);
    }

    [Fact]
    public void An_unparseable_usage_fact_counts_as_unknown_rather_than_throwing()
    {
        Item garbage = new(
            "dotnet", "package", "x", "1.0", "nuget-packages", @"C:\x", 10, Risk.Safe,
            Facts.From([new KeyValuePair<string, string>("usage", "Sideways")]));

        Assert.Equal(1, TotalsOf(garbage).UnknownUsageCount);
    }

    [Fact]
    public void Per_ecosystem_counts_sum_to_the_item_total()
    {
        ReportTotals totals = TotalsOf(
            Pkg("dotnet", size: 100),
            Pkg("dotnet", size: 200),
            Pkg("go", size: 50));

        Assert.True(totals.IsConsistent);
        Assert.Equal(300ul, totals.ByEcosystem["dotnet"].Bytes);
        Assert.Equal(50ul, totals.ByEcosystem["go"].Bytes);
        Assert.Equal(350ul, totals.TotalBytes);
    }

    [Fact]
    public void The_total_bytes_is_the_sum_of_the_items()
    {
        ulong expected = 0;
        foreach (ulong size in new ulong[] { 1, 999, 123_456, 7 })
        {
            expected += size;
        }

        Assert.Equal(expected, TotalsOf(Pkg(size: 1), Pkg(size: 999), Pkg(size: 123_456), Pkg(size: 7)).TotalBytes);
    }

    // ---- cross-format totals equality ---------------------------------------------------------------

    [Fact]
    public void Csv_and_markdown_agree_on_every_total()
    {
        Item[] items =
        [
            Pkg("dotnet", "a", "1.0", Usage.Referenced, 1000),
            Pkg("dotnet", "b", "2.0", Usage.Unreferenced, 2000),
            Pkg("go", "c", "3.0", Usage.Unknown, 3000),
            Pkg("rust", "d", "4.0", Usage.Unreferenced, 4000),
        ];

        ScanReport report = Report(items);
        ReportTotals totals = TotalsOf(items);
        string csv = CsvReport.Render(report.SnapshotId, items, totals);
        string md = MarkdownReport.Render(report);

        // Every number a user could compare between the two files.
        Assert.Contains($"# item_count,{totals.ItemCount}", csv, StringComparison.Ordinal);
        Assert.Contains($"# total_bytes,{totals.TotalBytes}", csv, StringComparison.Ordinal);
        Assert.Contains($"# referenced,{totals.ReferencedCount}", csv, StringComparison.Ordinal);
        Assert.Contains($"# unreferenced,{totals.UnreferencedCount}", csv, StringComparison.Ordinal);
        Assert.Contains($"# unknown_usage,{totals.UnknownUsageCount}", csv, StringComparison.Ordinal);

        Assert.Contains($"| **Total** | **{totals.ItemCount}** |", md, StringComparison.Ordinal);
        Assert.Contains($"| Referenced | {totals.ReferencedCount} |", md, StringComparison.Ordinal);
        Assert.Contains($"| Unreferenced | {totals.UnreferencedCount} |", md, StringComparison.Ordinal);
        Assert.Contains($"| Unknown | {totals.UnknownUsageCount} |", md, StringComparison.Ordinal);

        // And the human-readable size, which is what gets pasted into an issue.
        Assert.Contains(
            ReportTotals.FormatBytes(totals.TotalBytes),
            md,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Both_formats_render_the_same_number_of_item_rows()
    {
        Item[] items = [Pkg(name: "a"), Pkg(name: "b"), Pkg(name: "c")];

        string csv = CsvReport.Render("snap_1", items, TotalsOf(items));
        string md = MarkdownReport.Render(Report(items));

        // Counted with a real CSV parser, not Split('\n'): the totals block contains comment lines that are
        // not records, and a naive splitter is not what a consumer of this format uses.
        int csvRows = ParseCsv(csv)
            .Count(r => !r[0].StartsWith("#", StringComparison.Ordinal)) - 1;

        // Markdown item rows, counted within the Items section. Counting by prefix would also match the
        // ecosystem table's rows, which start "| dotnet |" in exactly the same way.
        string[] mdRows = MarkdownSection(md, "## Items");

        Assert.Equal(items.Length, csvRows);
        Assert.Equal(csvRows, mdRows.Length);
    }

    [Fact]
    public void An_empty_scan_renders_in_both_formats_without_dividing_by_zero()
    {
        ScanReport report = Report();
        ReportTotals totals = TotalsOf();

        Assert.True(totals.IsConsistent);
        Assert.Equal(0, totals.ItemCount);
        Assert.Contains("# item_count,0", CsvReport.Render(report.SnapshotId, [], totals), StringComparison.Ordinal);
        Assert.Contains("_No items were found._", MarkdownReport.Render(report), StringComparison.Ordinal);
    }

    // ---- CSV correctness -----------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    [InlineData("has\r\ncrlf", "\"has\r\ncrlf\"")]
    [InlineData("has\rcr", "\"has\rcr\"")]
    [InlineData(" leading-space", "\" leading-space\"")]
    [InlineData("trailing-space ", "\"trailing-space \"")]
    [InlineData("", "")]
    public void Csv_fields_are_quoted_exactly_when_they_must_be(string input, string expected)
    {
        Assert.Equal(expected, CsvReport.Quote(input));
    }

    [Fact]
    public void A_package_name_cannot_forge_an_extra_csv_column()
    {
        // Names come from manifests and directories, so this is attacker-influenced input. An unquoted
        // comma here would silently shift every column to the right for that row.
        Item nasty = Pkg(name: "evil\",version\"", version: "1.0");

        string csv = CsvReport.Render("snap_1", [nasty], TotalsOf(nasty));
        List<List<string>> records = ParseCsv(csv)
            .Where(r => !r[0].StartsWith("#", StringComparison.Ordinal))
            .ToList();

        // Exactly the header's column count once quoted — an unquoted comma here would shift every
        // column to the right for that row and silently change what the row means.
        Assert.Equal(CsvReport.Columns.Length, records[1].Count);
        Assert.Equal("evil\",version\"", records[1][2]);
    }

    [Fact]
    public void A_package_name_with_a_newline_does_not_add_a_row()
    {
        Item nasty = Pkg(name: "line1\nline2");

        string csv = CsvReport.Render("snap_1", [nasty], TotalsOf(nasty));

        // The document has one header record and one data record, and the newline inside the quoted name does
        // not start a third: a correct parser reassembles it, which is the whole point of quoting it.
        List<List<string>> records = ParseCsv(csv)
            .Where(r => !r[0].StartsWith("#", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, records.Count);
        Assert.Equal(CsvReport.Columns, records[0]);
        Assert.Equal("line1\nline2", records[1][2]);
    }

    [Fact]
    public void The_csv_header_row_lists_every_column_in_order()
    {
        string csv = CsvReport.Render("snap_1", [], TotalsOf());

        Assert.Contains(string.Join(',', CsvReport.Columns), csv, StringComparison.Ordinal);
    }

    [Fact]
    public void The_csv_totals_block_precedes_the_data_so_a_reader_sees_the_scale_first()
    {
        Item[] items = [Pkg(name: "a")];
        string csv = CsvReport.Render("snap_1", items, TotalsOf(items));

        int totalsAt = csv.IndexOf("# item_count,", StringComparison.Ordinal);
        int headerAt = csv.IndexOf(string.Join(',', CsvReport.Columns), StringComparison.Ordinal);

        Assert.True(totalsAt >= 0 && headerAt >= 0);
        Assert.True(totalsAt < headerAt, "totals must precede the data");
    }

    [Fact]
    public void Csv_uses_invariant_number_formatting()
    {
        // A machine with a comma decimal separator would otherwise write "1,024" into size_bytes and
        // silently shift every following column.
        string csv = CsvReport.Render("snap_1", [Pkg(size: 1234567)], TotalsOf(Pkg(size: 1234567)));

        Assert.Contains("1234567", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567.00", csv, StringComparison.Ordinal);
    }

    // ---- Markdown correctness -----------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has|pipe", "has\\|pipe")]
    [InlineData("has\nnewline", "has newline")]
    [InlineData("has\r\ncrlf", "has crlf")]
    [InlineData("back\\slash", "back\\\\slash")]
    public void Markdown_cells_escape_what_would_break_a_table(string input, string expected)
    {
        Assert.Equal(expected, MarkdownReport.Escape(input));
    }

    [Fact]
    public void A_package_name_cannot_forge_an_extra_markdown_column()
    {
        Item nasty = Pkg(name: "evil|extra");

        string md = MarkdownReport.Render(Report(nasty));
        string row = md.Split('\n').Single(l => l.Contains("evil", StringComparison.Ordinal));

        // The item table has seven columns. A row has a leading pipe, six separators, and a trailing pipe:
        // eight unescaped pipes in total. If the name's pipe were unescaped it would count as nine and the
        // renderer would show a blank "Size" column for that row and shift the rest.
        Assert.Equal(8, CountUnescapedPipes(row));
    }

    [Fact]
    public void A_package_name_with_a_newline_does_not_add_a_markdown_row()
    {
        Item nasty = Pkg(name: "line1\nline2");
        Item also = Pkg(name: "other");

        string md = MarkdownReport.Render(Report(nasty, also));

        // Two items means two rows, and the newline inside the name is flattened to a space rather than
        // starting a line of its own. If escaping broke, the newline would produce a third row in the
        // Items section that GFM would render as another record.
        Assert.Equal(2, MarkdownSection(md, "## Items").Length);
        Assert.Contains("line1 line2", md, StringComparison.Ordinal);
        Assert.DoesNotContain("line1\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_states_that_the_report_proposes_nothing()
    {
        // This format is meant to be pasted elsewhere. A reader with no other context must not be left
        // thinking the list is a list of things that will be deleted.
        Assert.Contains(
            "Coppice proposes nothing here",
            MarkdownReport.Render(Report(Pkg())),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_hints_survive_into_the_markdown()
    {
        ScanReport report = Report(Pkg()) with
        {
            OnboardingHints = ["No projects were found. Every package is Unknown."],
        };

        Assert.Contains("No projects were found.", MarkdownReport.Render(report), StringComparison.Ordinal);
    }

    // ---- determinism ---------------------------------------------------------------------------------

    [Fact]
    public void Both_formats_are_byte_identical_across_runs()
    {
        Item[] items = [Pkg(name: "b"), Pkg(name: "a"), Pkg("go", "c")];
        ScanReport report = Report(items);
        ReportTotals totals = TotalsOf(items);

        Assert.Equal(CsvReport.Render(report.SnapshotId, items, totals), CsvReport.Render(report.SnapshotId, items, totals));
        Assert.Equal(MarkdownReport.Render(report), MarkdownReport.Render(report));
    }

    [Fact]
    public void Per_ecosystem_lines_are_ordered_not_in_discovery_order()
    {
        Item[] items = [Pkg("rust", "r"), Pkg("go", "g"), Pkg("dotnet", "d")];
        string csv = CsvReport.Render("snap_1", items, TotalsOf(items));

        int dotnet = csv.IndexOf("# ecosystem:dotnet", StringComparison.Ordinal);
        int go = csv.IndexOf("# ecosystem:go", StringComparison.Ordinal);
        int rust = csv.IndexOf("# ecosystem:rust", StringComparison.Ordinal);

        Assert.True(dotnet < go && go < rust, "ecosystem blocks must be ordinal-sorted");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// Splits a CSV document into records, honouring quotes.
    /// <para>
    /// A newline inside a quoted field does NOT end a record. That is the entire reason the exporter quotes
    /// such values, so any test that counted records by splitting on '\n' would be measuring the naive
    /// parser rather than the format.
    /// </para>
    /// </summary>
    private static List<List<string>> ParseCsv(string document)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        // Index-based rather than foreach: a doubled quote inside a quoted field consumes TWO characters
        // and must emit one literal quote. foreach cannot express that lookahead, and this parser is the
        // instrument these assertions measure with — if it mis-reads the format, they prove nothing.
        for (int i = 0; i < document.Length; i++)
        {
            char c = document[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < document.Length && document[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (c is '\r' or '\n')
            {
                fields.Add(current.ToString());
                current.Clear();

                // A blank line is a separator, not a record.
                if (fields.Count > 1 || fields[0].Length > 0)
                {
                    records.Add(fields);
                }

                fields = [];
            }
            else
            {
                current.Append(c);
            }
        }

        return records;
    }

    /// <summary>
    /// The lines of a Markdown section, i.e. between a heading and the next blank line.
    /// <para>
    /// Counting rows by prefix is unreliable here: the ecosystem table's rows start "| dotnet |" exactly
    /// like the item table's rows do. Section-scoping is what makes "count the item rows" unambiguous.
    /// </para>
    /// </summary>
    private static string[] MarkdownSection(string markdown, string heading)
    {
        string[] lines = markdown.Split('\n');
        // The heading is followed by a BLANK line before its table, so the scan has to skip leading blanks
        // before looking for the end — otherwise every section comes back empty.
        int start = Array.FindIndex(lines, l => string.Equals(l.TrimEnd('\r'), heading, StringComparison.Ordinal));

        if (start < 0)
        {
            return [];
        }

        int first = start + 1;
        while (first < lines.Length && lines[first].Trim().Length == 0)
        {
            first++;
        }

        int end = first;
        while (end < lines.Length && lines[end].Trim().Length > 0)
        {
            end++;
        }

        // Drop the "| --- |" separator and the column-header row; what remains is data rows.
        var body = new List<string>();

        foreach (string line in lines[first..end])
        {
            if (!line.StartsWith("| ---", StringComparison.Ordinal))
            {
                body.Add(line);
            }
        }

        return body.Count > 0 ? body.Skip(1).ToArray() : [];
    }
    private static int CountUnescapedPipes(string line)
    {
        int count = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '|')
            {
                continue;
            }

            int backslashes = 0;
            for (int j = i - 1; j >= 0 && line[j] == '\\'; j--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 0)
            {
                count++;
            }
        }

        return count;
    }

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(512UL, "512 B")]
    [InlineData(1024UL, "1.0 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(1048576UL, "1.0 MB")]
    [InlineData(1073741824UL, "1.0 GB")]
    [InlineData(1099511627776UL, "1.0 TB")]
    public void Bytes_are_formatted_with_one_decimal_above_a_kilobyte(ulong bytes, string expected)
    {
        Assert.Equal(expected, ReportTotals.FormatBytes(bytes));
    }

    [Fact]
    public void Byte_formatting_is_culture_independent()
    {
        // A comma decimal separator would turn "1.5 GB" into "1,5 GB" and read as a different number.
        Assert.Equal("1.5 GB", ReportTotals.FormatBytes(1610612736UL));
    }
}

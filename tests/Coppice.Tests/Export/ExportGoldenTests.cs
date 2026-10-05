using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Ports;
using Xunit;
// ScanReport.Problems is the Core domain type; Ports declares a portable Problem of the same name for
// the plugin boundary, and the ambiguity is a real signal that the two types are distinct concepts.
using Problem = Coppice.Core.Domain.Problem;
using UsageBreakdown = Coppice.Cli.UsageBreakdown;

namespace Coppice.Tests.Export;

/// <summary>
/// T-027 acceptance, first half: "golden files per format".
/// <para>
/// The cross-format totals test proves the formats agree with each other. It does not prove either one
/// is RIGHT — two formats could agree on a wrong number. The goldens pin what each format actually
/// produces, so a change in the shape of an export — a column reordered, a section dropped, a header
/// reworded — shows up as a diff someone has to look at.
/// </para>
/// <para>
/// Recorded on first run and then FAILED, following the T-017 convention: a golden written and passed in
/// the same run is a snapshot of whatever the code does today, which is the one thing a golden exists to
/// prevent.
/// </para>
/// </summary>
public sealed class ExportGoldenTests
{
    /// <summary>
    /// One fixed scan covering every section and every usage verdict, across three ecosystems, plus a
    /// problem and an issue. Deliberately not minimal: a golden of an empty report pins nothing about the
    /// sections that only appear when there is something to put in them.
    /// </summary>
    private static ScanReport GoldenScan() =>
        new(
            "snap_20250112T1015Z",
            OperatingSystemKind.Windows,
            [
                Item("dotnet", "newtonsoft.json", "13.0.1", Usage.Referenced, 11_927_552, Risk.Safe),
                Item("dotnet", "newtonsoft.json", "13.0.3", Usage.Referenced, 11_927_552, Risk.Safe),
                Item("dotnet", "newtonsoft.json", "9.0.1", Usage.Unreferenced, 6_291_456, Risk.Safe),
                Item("dotnet", "serilog", "4.0.0", Usage.Unreferenced, 2_097_152, Risk.Review),
                Item("dotnet", "leftover", "1.0.0", Usage.Unknown, 1024, Risk.Manual),
                Item("go", "github.com/pkg/errors", "v0.9.1", Usage.Referenced, 65_536, Risk.Safe),
                Item("go", "example.com/unused", "v1.0.0", Usage.Unreferenced, 32_768, Risk.Safe),
                Item("rust", "serde", "1.0.197", Usage.Referenced, 4_194_304, Risk.Safe),
            ],
            [new("nuget-packages", "root-unreadable", "Could not list the package directory.", @"C:\Users\dev\.nuget\packages")],
            [
                new Problem
                {
                    Ecosystem = "dotnet",
                    Code = "NO_FINGERPRINT",
                    Severity = Severity.Warning,
                    Summary = "Package directory has no lockfile, so it cannot be fingerprinted.",
                    Path = @"C:\Users\dev\.nuget\packages",
                    Detail = Facts.From([new KeyValuePair<string, string>("locationId", "nuget-packages")]),
                },
            ],
            UsageBreakdown.FromItems([]),
            ["dotnet", "go", "rust"],
            14,
            ["No projects were found. Reference resolution needs project roots."]);

    private static Item Item(string ecosystem, string name, string version, Usage usage, ulong size, Risk risk) =>
        new(
            ecosystem,
            "package",
            name,
            version,
            ecosystem switch
            {
                "dotnet" => "nuget-packages",
                "go" => "go-mod-cache",
                _ => "cargo-registry",
            },
            ecosystem == "go" ? $"/home/dev/go/pkg/mod/{name}@{version}" : $@"C:\Users\dev\.cache\{name}\{version}",
            size,
            risk,
            Facts.From([new KeyValuePair<string, string>("usage", usage.ToString())]));

    private static string GoldenPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "golden", name);

    private static void AssertGolden(string name, string actual)
    {
        string path = GoldenPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail(
                $"No golden at {name}. It has been recorded. Read it, confirm the shape is what a consumer of "
                + "this format should expect, and commit it — a golden nobody reviewed is worse than none.");
        }

        Assert.Equal(
            File.ReadAllText(path, System.Text.Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal),
            actual.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void The_csv_golden_matches()
    {
        ScanReport report = GoldenScan();
        ReportTotals totals = ReportTotals.From(report.Items, report.Problems, report.Issues);

        AssertGolden("export-scan.csv", CsvReport.Render(report.SnapshotId, report.Items, totals));
    }

    [Fact]
    public void The_markdown_golden_matches()
    {
        AssertGolden("export-scan.md", MarkdownReport.Render(GoldenScan()));
    }

    [Fact]
    public void The_two_goldens_cover_every_section_the_report_can_emit()
    {
        // A golden that silently stopped rendering a section would still pass — the file would just be
        // shorter. These assertions say which sections must be present, so losing one fails by name.
        string md = MarkdownReport.Render(GoldenScan());

        Assert.Contains("## Usage", md, StringComparison.Ordinal);
        Assert.Contains("## By ecosystem", md, StringComparison.Ordinal);
        Assert.Contains("## Items", md, StringComparison.Ordinal);
        Assert.Contains("## Problems", md, StringComparison.Ordinal);
        Assert.Contains("## Issues", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_goldens_exist_and_are_substantial()
    {
        foreach (string name in new[] { "export-scan.csv", "export-scan.md" })
        {
            Assert.True(File.Exists(GoldenPath(name)), $"missing golden: {name}");
            Assert.True(
                File.ReadAllText(GoldenPath(name)).Split('\n').Length > 10,
                $"golden {name} is too short to pin a format");
        }
    }
}

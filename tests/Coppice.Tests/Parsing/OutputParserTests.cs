using Xunit;

namespace Coppice.Tests.Parsing;

using Issue = Coppice.Parsing.ParseIssue;

/// <summary>
/// T-021: the resolver parser library (FR-02).
/// <para>
/// These tests are driven by the RECORDED tool outputs, not by hand-written strings, so a change in
/// how a tool actually prints its output breaks them instead of being papered over by a fixture that
/// happens to agree with the parser.
/// </para>
/// <para>
/// Two invariants matter more than any individual parse:
/// <list type="bullet">
/// <item>Nothing throws. Tool output is untrusted, and a poisoned tool must not abort a scan.</item>
/// <item>Nothing is guessed. An unrecognised shape yields a ParseIssue and a lower confidence, never a
/// candidate path — because candidates are fed to the denylist, and a guess there is a defect that
/// hides until someone is unlucky.</item>
/// </list>
/// </para>
/// </summary>
public class OutputParserTests
{
    private static readonly string[] OperatingSystems = ["Windows", "Linux", "MacOS"];

    private static string Recorded(string stem, string os)
    {
        string path = Path.Combine("recorded", $"{stem}.{os}.txt");
        Assert.True(File.Exists(path), $"missing recording: {path}");

        // Headers are '#' comments; the payload is everything after them.
        return string.Join(
            "\n",
            File.ReadAllLines(path).Where(line => !line.StartsWith('#')).Where(line => line.Trim().Length > 0));
    }

    // ---- the acceptance cases the card names ----------------------------------------------------------

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void go_env_GOMODCACHE_parses_on_every_OS(string os)
    {
        Coppice.Parsing.ParseResult result =
            Coppice.Parsing.OutputParsers.ParseLabelValueFor(Recorded("go-env-gomodcache", os), "GOMODCACHE");

        Assert.True(result.IsConfident, string.Join("; ", result.Issues.Select(i => i.ToString())));
        Assert.Single(result.Values);

        string cache = result.Values[0];

        // The apostrophes Go wraps its values in must be gone. A path that still starts with one
        // matches no directory on disk, so the location silently resolves to nothing.
        Assert.DoesNotContain('\'', cache);
        Assert.StartsWith(cache, cache.Trim('\''));
        Assert.True(Coppice.Parsing.OutputParsers.LooksAbsolute(cache), $"'{cache}' is not absolute");

        // The separator is the OS's, and asserting "pkg/mod" everywhere would have failed on Windows
        // for a reason that has nothing to do with the parser.
        string tail = os == "Windows" ? @"pkg\mod" : "pkg/mod";
        Assert.EndsWith(tail, cache, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void npm_config_get_cache_parses_on_every_OS(string os)
    {
        // `npm config get cache` prints a BARE path with no label and no quotes — the `line` parser.
        Coppice.Parsing.ParseResult result =
            Coppice.Parsing.OutputParsers.ParseLines(Recorded("npm-config-get-cache", os));

        Assert.True(result.IsConfident, string.Join("; ", result.Issues.Select(i => i.ToString())));
        Assert.Single(result.Values);
        Assert.True(Coppice.Parsing.OutputParsers.LooksAbsolute(result.Values[0]));
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void npm_root_g_parses_on_every_OS(string os)
    {
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLines(Recorded("npm-root-g", os));

        Assert.True(result.IsConfident, string.Join("; ", result.Issues.Select(i => i.ToString())));
        Assert.Single(result.Values);
        Assert.EndsWith("node_modules", result.Values[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void go_env_full_dump_yields_one_value_per_label(string os)
    {
        // The multi-label case: `go env` with no argument prints every variable at once.
        string output = Recorded("go-env", os);

        foreach (string label in new[] { "GOMODCACHE", "GOCACHE", "GOPATH", "GOROOT" })
        {
            Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLabelValueFor(output, label);
            Assert.True(result.IsConfident, $"label {label}: {string.Join("; ", result.Issues.Select(i => i.ToString()))}");
            Assert.Single(result.Values);
        }

        // A label that is absent is an ISSUE, not an empty success. "I asked and it was not there" and
        // "I did not look" are different states, and resolution depends on telling them apart.
        Coppice.Parsing.ParseResult missing = Coppice.Parsing.OutputParsers.ParseLabelValueFor(output, "NOSUCHVAR");
        Assert.False(missing.IsConfident);
        Assert.Empty(missing.Values);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void An_empty_go_variable_is_reported_rather_than_read_as_a_path(string os)
    {
        // `GOEXE=''` on every platform: an empty value is an issue, not the string "''".
        Coppice.Parsing.ParseResult result =
            Coppice.Parsing.OutputParsers.ParseLabelValueFor(Recorded("go-env", os), "GOEXE");

        Assert.False(result.IsConfident);
        Assert.Empty(result.Values);
    }

    // ---- dotnet's recorded output, now through the generic parsers ------------------------------------

    [Theory]
    [MemberData(nameof(OsMatrix))]
    public void The_generic_label_value_parser_reads_dotnet_nuget_locals(string os)
    {
        foreach (string which in new[] { "global-packages", "http-cache", "temp" })
        {
            Coppice.Parsing.ParseResult result =
                Coppice.Parsing.OutputParsers.ParseLabelValue(Recorded($"dotnet-nuget-locals-{which}", os));

            Assert.True(result.IsConfident, $"{os}/{which}: {string.Join("; ", result.Issues.Select(i => i.ToString()))}");
            Assert.Single(result.Values);
            Assert.True(Coppice.Parsing.OutputParsers.LooksAbsolute(result.Values[0]));
        }
    }

    [Theory]
    [MemberData(nameof(OsMatrix))]
    public void The_generic_bracket_parser_reads_dotnet_list_sdks(string os)
    {
        Coppice.Parsing.ParseResult result =
            Coppice.Parsing.OutputParsers.ParseBracketPaths(Recorded("dotnet--list-sdks", os));

        Assert.True(result.IsConfident, string.Join("; ", result.Issues.Select(i => i.ToString())));
        Assert.NotEmpty(result.Values);
        Assert.All(result.Values, v => Assert.True(Coppice.Parsing.OutputParsers.LooksAbsolute(v), v));
    }

    [Theory]
    [MemberData(nameof(OsMatrix))]
    public void The_generic_bracket_parser_reads_dotnet_list_runtimes(string os)
    {
        Coppice.Parsing.ParseResult result =
            Coppice.Parsing.OutputParsers.ParseBracketPaths(Recorded("dotnet--list-runtimes", os));

        Assert.True(result.IsConfident, string.Join("; ", result.Issues.Select(i => i.ToString())));
        Assert.NotEmpty(result.Values);
    }

    public static TheoryData<string> OsMatrix
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string os in OperatingSystems)
            {
                data.Add(os);
            }

            return data;
        }
    }

    // ---- the "never a guess" contract ------------------------------------------------------------------

    [Fact]
    public void A_missing_tool_reports_an_issue_and_no_paths()
    {
        // What a poisoned or absent `go` actually prints into the capture. Treating this as a path
        // would hand the denylist a candidate it cannot meaningfully evaluate.
        string notFound = "bash: go: command not found\r\n";

        Coppice.Parsing.ParseResult lines = Coppice.Parsing.OutputParsers.ParseLines(notFound);
        Assert.Empty(lines.Values);
        Assert.False(lines.IsConfident);

        Coppice.Parsing.ParseResult label = Coppice.Parsing.OutputParsers.ParseLabelValue(notFound);
        Assert.Empty(label.Values);
        Assert.False(label.IsConfident);
    }

    [Fact]
    public void Prose_output_never_becomes_a_candidate_path()
    {
        foreach (string output in new[]
                 {
                     "No global tools installed.",
                     "The following workloads are installed:",
                     "error: could not determine cache directory",
                     "Usage: go env [var ...]",
                 })
        {
            Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLines(output);
            Assert.Empty(result.Values);
            Assert.False(result.IsConfident);
        }
    }

    [Fact]
    public void A_relative_path_is_not_treated_as_absolute()
    {
        // "packages" is a plausible-looking answer from a tool whose output we misread. Treating it as
        // absolute would let resolution continue with a path that means nothing.
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLines("packages\n./cache\n");
        Assert.Empty(result.Values);
        Assert.Equal(["packages", "./cache"], result.Issues.Select(i => i.Line));
    }

    [Fact]
    public void Empty_output_is_confident_and_empty()
    {
        // No output is not a failure: a tool with nothing to report is a legitimate answer. The
        // difference from "I could not ask" is made by the CALLER, which knows whether it ran the tool.
        foreach (string? output in new[] { null, "", "   ", "\n\n" })
        {
            Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLines(output);
            Assert.Empty(result.Values);
            Assert.Empty(result.Issues);
            Assert.True(result.IsConfident);
        }
    }

    [Theory]
    [InlineData("key: /home/dev")]
    [InlineData("key = /home/dev")]
    [InlineData("KEY: /home/dev")]
    [InlineData("  key:   /home/dev  ")]
    [InlineData("key:/home/dev")]
    [InlineData("key: C:\\tools")]
    public void Label_value_accepts_the_separator_spellings_tools_actually_use(string line)
    {
        // The value is a real path, because the parser now REQUIRES one — which is the whole point of
        // the guard that made this test fail the first time it ran.
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLabelValue(line);

        Assert.True(result.IsConfident, string.Join("; ", result.Issues.Select(i => i.ToString())));
        Assert.Single(result.Values);
    }

    [Theory]
    [InlineData("key: value")]
    [InlineData("global-packages: not-a-path")]
    public void Label_value_reports_a_non_path_value_instead_of_accepting_prose(string line)
    {
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLabelValue(line);

        Assert.Empty(result.Values);
        Assert.False(result.IsConfident);
        Assert.Contains("not an absolute path", Assert.Single(result.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_drive_colon_does_not_beat_the_label_separator()
    {
        // "PATH=C:\tools": a naive first-colon search splits at the drive and yields the label
        // "PATH=C", which matches nothing.
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseLabelValue("PATH=C:\\tools");
        Assert.Equal(["C:\\tools"], result.Values);
    }

    [Fact]
    public void Quoted_values_are_unwrapped()
    {
        Assert.Equal(
            "/home/dev/go/pkg/mod",
            Coppice.Parsing.OutputParsers.ParseLabelValue("GOMODCACHE='/home/dev/go/pkg/mod'").Values[0]);

        Assert.Equal(
            "C:\\Users\\dev\\go",
            Coppice.Parsing.OutputParsers.ParseLabelValue("GOPATH=\"C:\\Users\\dev\\go\"").Values[0]);

        // A lone apostrophe is data, not a quote pair — trimming it would corrupt a real path.
        Assert.Equal(
            "/home/o'brien/go",
            Coppice.Parsing.OutputParsers.ParseLabelValue("GOMODCACHE=/home/o'brien/go").Values[0]);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("C:\\", "C:\\")]
    [InlineData("/home/dev/go/pkg/mod", "/home/dev/go/pkg/mod")]
    [InlineData("C:\\Users\\dev\\go\\", "C:\\Users\\dev\\go")]
    public void Trailing_separators_are_trimmed_but_a_root_survives(string input, string expected)
    {
        // Trimming "C:\" to "C:" would produce a drive-RELATIVE path naming a different directory.
        Assert.Equal(expected, Coppice.Parsing.OutputParsers.TrimTrailingSeparators(input));
    }

    // ---- json-path -----------------------------------------------------------------------------------

    [Fact]
    public void Json_path_reads_a_nested_property()
    {
        const string npmJson = """{"cache":"C:\\Users\\dev\\npm-cache","prefix":"C:\\Program Files\\nodejs"}""";

        Assert.Equal(
            "C:\\Users\\dev\\npm-cache",
            Coppice.Parsing.OutputParsers.ParseJsonPath(npmJson, "cache").Values[0]);

        Assert.Equal(
            "C:\\Program Files\\nodejs",
            Coppice.Parsing.OutputParsers.ParseJsonPath(npmJson, "prefix").Values[0]);
    }

    [Fact]
    public void Json_path_reports_a_missing_property_rather_than_throwing()
    {
        const string npmJson = """{"cache":"C:\\npm-cache"}""";
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseJsonPath(npmJson, "nonexistent");

        Assert.Empty(result.Values);
        Assert.False(result.IsConfident);
        Assert.Contains("nonexistent", result.Issues[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_path_reports_invalid_json_rather_than_throwing()
    {
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseJsonPath("{not json", "cache");

        Assert.Empty(result.Values);
        Assert.False(result.IsConfident);
        Assert.Contains("invalid JSON", result.Issues[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_path_reports_a_non_string_value()
    {
        // `{"cache": {"level": 1}}` — an object where a path was expected must say so, not stringify.
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseJsonPath("""{"cache":{"level":1}}""", "cache");

        Assert.Empty(result.Values);
        Assert.Contains("object", result.Issues[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- regex (available to code plugins, refused in manifests) ----------------------------------------

    [Fact]
    public void Regex_extraction_works_with_a_capture_group()
    {
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseRegex(
            "GOMODCACHE=/home/dev/go/pkg/mod\nGOROOT=/usr/local/go",
            @"^GOMODCACHE=(?<v>.+)$");

        // Only the GOMODCACHE line matches; GOROOT does not, and becomes an issue rather than a value.
        // That is the contract: read what you recognise, report what you do not.
        Assert.Equal(["/home/dev/go/pkg/mod"], result.Values);
        Assert.Equal("GOROOT=/usr/local/go", Assert.Single(result.Issues).Line);
    }

    [Fact]
    public void An_invalid_regex_is_reported_rather_than_thrown()
    {
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseRegex("value", "([unclosed");

        Assert.Empty(result.Values);
        Assert.False(result.IsConfident);
        Assert.Contains("invalid pattern", result.Issues[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_matching_lines_are_issues_so_the_caller_can_lower_confidence()
    {
        Coppice.Parsing.ParseResult result = Coppice.Parsing.OutputParsers.ParseRegex(
            "/home/dev/go\nnot a match\n",
            @"^/home");

        // With no capture group the parser returns the MATCH, not the line — so "/home/dev/go"
        // yields "/home". A group is required to capture the whole path; that is a caller decision,
        // and asserting it here stops anyone assuming the line comes back intact.
        Assert.Equal(["/home"], result.Values);
        Issue issue = Assert.Single(result.Issues);
        Assert.Equal("not a match", issue.Line);
    }

    // ---- recording hygiene ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(OsMatrix))]
    public void Every_parser_recording_declares_its_provenance_honestly(string os)
    {
        foreach (string stem in new[] { "go-env-gomodcache", "go-env", "npm-config-get-cache", "npm-root-g" })
        {
            string[] lines = File.ReadAllLines(Path.Combine("recorded", $"{stem}.{os}.txt"));

            // At least one '#' header, and it must say WHICH os and WHICH command, or a recording
            // cannot be traced back to what produced it.
            List<string> headers = [.. lines.Where(l => l.StartsWith('#'))];
            Assert.NotEmpty(headers);
            Assert.Contains(headers, h => h.Contains("os:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(headers, h => h.Contains("command:", StringComparison.OrdinalIgnoreCase));

            // A real capture and a synthetic one must be distinguishable at a glance.
            Assert.Contains(headers, h => h.Contains("REAL", StringComparison.OrdinalIgnoreCase)
                                          || h.Contains("SYNTHETIC", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Only_the_machines_that_actually_had_the_tool_may_claim_a_real_capture()
    {
        // npm is installed on the Windows development machine and nowhere else here, so claiming a real
        // Linux capture would launder a guess into an authority. This pins the claim to reality.
        foreach (string os in OperatingSystems)
        {
            string[] headers = [.. File.ReadAllLines(Path.Combine("recorded", $"npm-config-get-cache.{os}.txt"))
                .Where(l => l.StartsWith('#'))];

            // Substring "REAL" matches "NOT A REAL CAPTURE" too, so the marker is the SYNTHETIC
            // banner. Getting this wrong would have made the whole provenance check vacuous.
            bool claimsReal = headers.All(h => !h.Contains("SYNTHETIC", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(os == "Windows", claimsReal);
        }

        // Go is not installed on any machine here, so no Go recording may claim otherwise.
        foreach (string os in OperatingSystems)
        {
            string[] headers = [.. File.ReadAllLines(Path.Combine("recorded", $"go-env-gomodcache.{os}.txt"))
                .Where(l => l.StartsWith('#'))];

            // Every Go recording must carry the synthetic banner, because Go was not installed on any
            // machine these files were produced on.
            Assert.Contains(headers, h => h.Contains("SYNTHETIC", StringComparison.OrdinalIgnoreCase));
            Assert.All(headers.Where(h => h.Contains("provenance", StringComparison.OrdinalIgnoreCase)),
                h => Assert.Contains("not installed", h, StringComparison.OrdinalIgnoreCase));
        }
    }
}

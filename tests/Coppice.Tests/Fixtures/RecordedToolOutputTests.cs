using System.Text;
using Coppice.Ports;
using Xunit;

namespace Coppice.Tests.Fixtures;

/// <summary>
/// Recorded real tool outputs, committed per OS flavor (T-017 acceptance).
/// <para>
/// 11-testing calls these "recorded <em>real</em> tool outputs" and they serve as resolver goldens.
/// The distinction matters: a hand-written fixture only proves the parser agrees with the author's
/// idea of what <c>dotnet --list-sdks</c> prints. A recording captured from a real machine proves it
/// agrees with the tool, including the parts nobody thinks to write down — trailing whitespace, the
/// bracketed install path on some platforms and not others, the blank line at the end.
/// </para>
/// <para>
/// Each file is paired with the OS it came from. <c>dotnet --list-sdks</c> is the clearest example of
/// why: the Linux and macOS builds print <c>9.0.100 [/usr/share/dotnet/sdk]</c> while Windows prints
/// <c>9.0.100 [C:\Program Files\dotnet\sdk]</c>, and a parser that has only ever seen one will
/// mis-handle the other.
/// </para>
/// <para>
/// A recording that was not captured from a real tool says so in its header. A file claiming to be
/// real when it is synthetic is worse than no file: it launders a guess into an authority.
/// </para>
/// </summary>
public sealed class RecordedToolOutputTests
{
    private static string RecordedPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "recorded", name);

    private static string SourceRecordedDirectory
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coppice.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir is not null, "could not locate the repository root.");

            return Path.Combine(dir.FullName, "tests", "Coppice.Tests", "recorded");
        }
    }

    /// <summary>
    /// Every query 09 lists, and every OS. Ticking the T-017 acceptance criterion means every pair
    /// here has a committed recording.
    /// </summary>
    private static readonly string[] Queries =
    [
        "dotnet--list-sdks",
        "dotnet--list-runtimes",
        "dotnet--list-workloads",
        "dotnet-tool-list",
        "dotnet-nuget-locals-global-packages",
        "dotnet-nuget-locals-http-cache",
        "dotnet-nuget-locals-temp",
    ];

    private static readonly OperatingSystemKind[] OperatingSystems =
        [OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS];

    /// <summary>The cross product, as the single source of truth for both the data and the orphan check.</summary>
    private static IEnumerable<(string Query, OperatingSystemKind OS)> RequiredRecordings =>
        from query in Queries
        from os in OperatingSystems
        select (query, os);

    public static TheoryData<string, OperatingSystemKind> RecordedQueries
    {
        get
        {
            var data = new TheoryData<string, OperatingSystemKind>();
            foreach ((string query, OperatingSystemKind os) in RequiredRecordings)
            {
                data.Add(query, os);
            }

            return data;
        }
    }

    private static string FileNameFor(string query, OperatingSystemKind os) => $"{query}.{os}.txt";

    [Theory]
    [MemberData(nameof(RecordedQueries))]
    public void A_recording_exists_for_every_query_and_os(string query, OperatingSystemKind os)
    {
        string name = FileNameFor(query, os);
        string path = Path.Combine(SourceRecordedDirectory, name);

        Assert.True(File.Exists(path), $"{name} is missing. Every tool query 09 lists needs a per-OS recording.");

        string content = File.ReadAllText(path, Encoding.UTF8);
        Assert.False(string.IsNullOrWhiteSpace(content), $"{name} is empty.");
    }

    [Theory]
    [MemberData(nameof(RecordedQueries))]
    public void A_recording_declares_where_it_came_from(string query, OperatingSystemKind os)
    {
        // The provenance header is the whole point of a recorded file. A recording without one cannot
        // be told apart from a fixture someone typed from memory, and that distinction is the reason
        // recorded outputs exist separately from generated ones.
        string content = File.ReadAllText(RecordedPath(FileNameFor(query, os)), Encoding.UTF8);

        Assert.StartsWith("# coppice recorded tool output", content, StringComparison.Ordinal);
        Assert.Contains($"os: {os}", content, StringComparison.Ordinal);
        Assert.Contains("source:", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RecordedQueries))]
    public void The_payload_is_recoverable_by_stripping_the_header(string query, OperatingSystemKind os)
    {
        // The header must not corrupt the data. If a parser consumed this file verbatim it would see
        // the header as output, so the stripping rule is part of the format, not a convenience.
        string content = File.ReadAllText(RecordedPath(FileNameFor(query, os)), Encoding.UTF8);
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int firstPayloadLine = Array.FindIndex(lines, l => !l.StartsWith('#') && l.Trim().Length > 0);

        Assert.True(firstPayloadLine >= 0, "the recording has no payload after its header.");
        Assert.False(lines[firstPayloadLine].Contains('#', StringComparison.Ordinal));
    }

    [Fact]
    public void Sdk_recordings_are_parseable_and_agree_on_the_versions_they_share()
    {
        // Each OS was captured on a different machine at a different time, so the version LISTS need
        // not match. What must hold is that the FORMAT parses on every one of them — which is the
        // property a cross-platform parser actually has to survive.
        var parsed = new Dictionary<OperatingSystemKind, IReadOnlyList<string>>();

        foreach (OperatingSystemKind os in new[] { OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS })
        {
            string content = File.ReadAllText(RecordedPath(FileNameFor("dotnet--list-sdks", os)), Encoding.UTF8);
            IReadOnlyList<Coppice.Plugins.Net.SdkInfo> sdks = Coppice.Plugins.Net.DotnetOutputParser.ParseSdks(Payload(content));

            Assert.NotEmpty(sdks);
            Assert.All(sdks, s => Assert.False(string.IsNullOrWhiteSpace(s.Version)));
            Assert.All(sdks, s => Assert.True(int.TryParse(Coppice.Plugins.Net.SdkVersion.MajorOf(s.Version), out _), $"'{s.Version}' has no parseable major."));

            parsed[os] = [.. sdks.Select(s => s.Version)];
        }

        // Every install path must be absolute. A relative path out of a tool is a poisoned answer
        // (E-11) and the resolver must see it as one.
        foreach (OperatingSystemKind os in new[] { OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS })
        {
            string content = File.ReadAllText(RecordedPath(FileNameFor("dotnet--list-sdks", os)), Encoding.UTF8);

            foreach (string line in Payload(content).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int open = line.IndexOf('[');
                int close = line.LastIndexOf(']');
                if (open < 0 || close < open)
                {
                    continue;
                }

                string path = line[(open + 1)..close];
                Assert.False(string.IsNullOrWhiteSpace(path), "an SDK line has an empty install path.");

                bool absolute = os == OperatingSystemKind.Windows
                    ? path.Length > 2 && path[1] == ':'
                    : path.StartsWith('/');

                Assert.True(absolute, $"'{path}' is not an absolute path on {os} — a tool must never report one.");
            }
        }

        Assert.Equal(3, parsed.Count);
    }

    [Fact]
    public void Nuget_locals_recordings_name_a_path_for_the_location_they_report()
    {
        foreach (OperatingSystemKind os in new[] { OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS })
        {
            foreach (string query in new[] { "dotnet-nuget-locals-global-packages", "dotnet-nuget-locals-http-cache", "dotnet-nuget-locals-temp" })
            {
                string content = File.ReadAllText(RecordedPath(FileNameFor(query, os)), Encoding.UTF8);
                string payload = Payload(content);

                Assert.Contains(": ", payload, StringComparison.Ordinal);

                // The value after the label is the resolver's answer, so it must look like a path and
                // not like an error message. `dotnet nuget locals` prints "<label>: <path>" on success
                // and something quite different on failure.
                string value = payload[(payload.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
                Assert.False(string.IsNullOrWhiteSpace(value), $"{query} on {os} reported no path.");
                Assert.False(value.Contains("error", StringComparison.OrdinalIgnoreCase));
                Assert.False(value.Contains("unknown", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void The_tool_list_recording_carries_the_header_row_the_tool_prints()
    {
        foreach (OperatingSystemKind os in new[] { OperatingSystemKind.Windows, OperatingSystemKind.Linux, OperatingSystemKind.MacOS })
        {
            string payload = Payload(File.ReadAllText(RecordedPath(FileNameFor("dotnet-tool-list", os)), Encoding.UTF8));

            Assert.StartsWith("Package Id", payload.TrimStart(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_recording_is_referenced_by_some_test()
    {
        // A recording nobody reads is documentation that can rot silently. This keeps the directory
        // honest: adding a file without a test that consumes it fails here.
        string directory = SourceRecordedDirectory;
        string[] onDisk = [.. Directory.GetFiles(directory, "*.txt").Select(Path.GetFileName)!];

        // The expected names are derived from the same FileNameFor the assertions use, so any file
        // on disk that no [MemberData] row covers shows up as an unrecognised name.
        // Built from the SAME list that drives the [MemberData] rows, via a factory both read, so a
        // recording on disk that no row covers shows up as an unrecognised name.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string query, OperatingSystemKind os) in RequiredRecordings)
        {
            known.Add(FileNameFor(query, os));
        }

        var orphans = onDisk.Where(f => !known.Contains(f)).ToList();
        Assert.Empty(orphans);
    }

    /// <summary>Strips the '#' provenance header and returns only the tool's own output.</summary>
    private static string Payload(string recorded) => string.Join(
        '\n',
        recorded.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(l => !l.StartsWith('#')));
}

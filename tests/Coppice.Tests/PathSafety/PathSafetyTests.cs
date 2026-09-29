using System.Text;
using Coppice.Core.PathSafety;
using Coppice.Ports;
using Coppice.Tests.Support;
namespace Coppice.Tests.PathSafety;

/// <summary>Card T-004 acceptance: table-driven canonicalization tests per OS.</summary>
public sealed class PathCanonicalizerTests
{
    [Theory]
    // Linux/POSIX: native separator is '/', case-sensitive.
    [InlineData("/foo/bar", "/foo/bar", OperatingSystemKind.Linux)]
    [InlineData("/foo/./bar", "/foo/bar", OperatingSystemKind.Linux)]
    [InlineData("/foo/baz/../bar", "/foo/bar", OperatingSystemKind.Linux)]
    [InlineData("///a///b///", "/a/b", OperatingSystemKind.Linux)]
    [InlineData("/a/..", "/", OperatingSystemKind.Linux)]
    public void Linux_canonicalize(string input, string expected, OperatingSystemKind os)
    {
        var result = PathCanonicalizer.Canonicalize(input, os);
        Assert.True(result.Ok);
        Assert.Equal(expected, result.Path);
    }

    [Theory]
    // Windows: native separator is '\', and a forward slash is normalized to it.
    [InlineData("C:/foo/bar", "C:\\foo\\bar", OperatingSystemKind.Windows)]
    [InlineData("C:\\foo\\..\\bar", "C:\\bar", OperatingSystemKind.Windows)]
    [InlineData("C:\\foo\\.\\bar", "C:\\foo\\bar", OperatingSystemKind.Windows)]
    [InlineData("C:\\foo\\bar\\..\\..", "C:\\", OperatingSystemKind.Windows)]
    [InlineData("C:\\a\\b\\c\\d\\..\\..\\e", "C:\\a\\b\\e", OperatingSystemKind.Windows)]
    [InlineData("C:\\foo\\bar\\", "C:\\foo\\bar", OperatingSystemKind.Windows)]
    [InlineData("C:\\\\foo\\\\bar", "C:\\foo\\bar", OperatingSystemKind.Windows)]
    public void Windows_canonicalize(string input, string expected, OperatingSystemKind os)
    {
        var result = PathCanonicalizer.Canonicalize(input, os);
        Assert.True(result.Ok);
        Assert.Equal(expected, result.Path);
    }

    [Theory]
    // macOS: POSIX separators, case-insensitive filesystem.
    [InlineData("/Users/alice/foo", "/Users/alice/foo", OperatingSystemKind.MacOS)]
    [InlineData("/Users/alice/../bob", "/Users/bob", OperatingSystemKind.MacOS)]
    [InlineData("/foo/./bar", "/foo/bar", OperatingSystemKind.MacOS)]
    public void MacOS_canonicalize(string input, string expected, OperatingSystemKind os)
    {
        var result = PathCanonicalizer.Canonicalize(input, os);
        Assert.True(result.Ok);
        Assert.Equal(expected, result.Path);
    }

    [Fact]
    public void The_long_path_prefix_is_stripped_so_both_spellings_compare_equal()
    {
        // E-8: `\\?\C:\cache` and `C:\cache` are the same directory. If canonicalization kept the
        // prefix on one spelling and not the other, every containment check against a long path
        // would fail open or closed at random.
        var plain = PathCanonicalizer.Canonicalize(@"C:\cache\pkg", OperatingSystemKind.Windows);
        var prefixed = PathCanonicalizer.Canonicalize(@"\\?\C:\cache\pkg", OperatingSystemKind.Windows);

        Assert.True(plain.Ok);
        Assert.True(prefixed.Ok);
        Assert.Equal(plain.Path, prefixed.Path);
        Assert.False(PathCanonicalizer.HasLongPathPrefix(plain.Path));
    }

    [Fact]
    public void ToLongPathForm_round_trips_and_is_only_for_io()
    {
        string canonical = PathCanonicalizer.Canonicalize(@"C:\cache\pkg", OperatingSystemKind.Windows).Path;
        string longForm = PathCanonicalizer.ToLongPathForm(canonical, OperatingSystemKind.Windows);

        Assert.Equal(@"\\?\C:\cache\pkg", longForm);
        Assert.Equal(canonical, PathCanonicalizer.Canonicalize(longForm, OperatingSystemKind.Windows).Path);

        // POSIX paths are untouched.
        Assert.Equal("/cache/pkg", PathCanonicalizer.ToLongPathForm("/cache/pkg", OperatingSystemKind.Linux));
    }

    [Fact]
    public void A_unc_path_is_converted_to_the_unc_long_form()
    {
        Assert.Equal(@"\\?\UNC\server\share", PathCanonicalizer.ToLongPathForm(@"\\server\share", OperatingSystemKind.Windows));
    }

    [Theory]
    [InlineData("foo.", CanonicalFailure.TrailingDotOrSpace, OperatingSystemKind.Windows)]
    [InlineData("foo ", CanonicalFailure.TrailingDotOrSpace, OperatingSystemKind.Windows)]
    [InlineData("/a/b.c:", CanonicalFailure.AdsNotation, OperatingSystemKind.Windows)]
    [InlineData("name:stream", CanonicalFailure.AdsNotation, OperatingSystemKind.Windows)]
    public void Canonicalize_refuses_names_windows_cannot_represent(string input, CanonicalFailure expected, OperatingSystemKind os)
    {
        var result = PathCanonicalizer.Canonicalize(input, os);
        Assert.False(result.Ok);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Canonicalize_rejects_a_null_byte()
    {
        var result = PathCanonicalizer.Canonicalize("foo\0bar", OperatingSystemKind.Linux);
        Assert.False(result.Ok);
        Assert.Equal(CanonicalFailure.NullByte, result.Reason);
    }

    [Fact]
    public void Canonicalize_rejects_an_empty_path()
    {
        var result = PathCanonicalizer.Canonicalize("   ", OperatingSystemKind.Linux);
        Assert.False(result.Ok);
        Assert.Equal(CanonicalFailure.EmptyPath, result.Reason);
    }

    [Theory]
    // A refusal must be a value, never an exception: a plugin returning a hostile path must not
    // be able to abort a whole scan.
    [InlineData("name:stream", OperatingSystemKind.Windows)]
    [InlineData("foo. ", OperatingSystemKind.Windows)]
    [InlineData("a\0b", OperatingSystemKind.Linux)]
    [InlineData("", OperatingSystemKind.Linux)]
    public void Canonicalize_never_throws_on_untrusted_input(string input, OperatingSystemKind os)
    {
        var result = PathCanonicalizer.Canonicalize(input, os);
        Assert.False(result.Ok);
        Assert.NotEqual(CanonicalFailure.None, result.Reason);
        Assert.NotEmpty(result.Message);
    }

    [Theory]
    [InlineData("/foo/bar", "/FOO/BAR", OperatingSystemKind.Linux, false)]
    [InlineData("/foo/bar", "/FOO/BAR", OperatingSystemKind.Windows, true)]
    [InlineData("/foo/bar", "/FOO/BAR", OperatingSystemKind.MacOS, true)]
    public void PathsEqual_respects_case_rules(string a, string b, OperatingSystemKind os, bool expected)
    {
        Assert.Equal(expected, PathCanonicalizer.PathsEqual(a, b, os));
    }

    [Theory]
    // Windows and macOS filesystems are case-insensitive, so paths fold for COMPARISON.
    // Linux is case-sensitive and must not fold, or two distinct real directories would collapse.
    [InlineData("/Users/Foo", "/USERS/FOO", OperatingSystemKind.MacOS)]
    [InlineData(@"C:\Users\Foo", "C:\\USERS\\FOO", OperatingSystemKind.Windows)]
    [InlineData("/Users/Foo", "/Users/Foo", OperatingSystemKind.Linux)]
    [InlineData("/home/alice", "/home/alice", OperatingSystemKind.Linux)]
    public void FoldCase_respects_os(string input, string expected, OperatingSystemKind os)
    {
        Assert.Equal(expected, PathCanonicalizer.FoldCase(input, os));
    }

    [Fact]
    public void HasNfcNfdCollision_detects_decomposed_against_composed()
    {
        // "café" written decomposed (e + combining acute) is a DIFFERENT string from the
        // composed form, but the same name to macOS/Linux filesystems (E-9).
        const string composed = "caf\u00e9";
        const string decomposed = "cafe\u0301";

        Assert.NotEqual(composed, decomposed);
        Assert.True(PathCanonicalizer.HasNfcNfdCollision(composed, [decomposed], OperatingSystemKind.MacOS));
        Assert.True(PathCanonicalizer.HasNfcNfdCollision(decomposed, [composed], OperatingSystemKind.Linux));
    }

    [Fact]
    public void HasNfcNfdCollision_ignores_genuinely_distinct_names()
    {
        Assert.False(PathCanonicalizer.HasNfcNfdCollision("caf\u00e9", ["cafe"], OperatingSystemKind.Linux));
    }

    [Theory]
    // Windows does not fold these two names, so reporting a collision there would be a false
    // positive. Linux and macOS do fold them, which is what E-9 is about.
    [InlineData(OperatingSystemKind.Windows, false)]
    [InlineData(OperatingSystemKind.Linux, true)]
    [InlineData(OperatingSystemKind.MacOS, true)]
    public void HasNfcNfdCollision_applies_only_where_the_filesystem_folds(OperatingSystemKind os, bool expected)
    {
        Assert.Equal(expected, PathCanonicalizer.HasNfcNfdCollision("caf\u00e9", ["cafe\u0301"], os));
    }
}

public sealed class DenylistTests
{
    // The denylist is a PURE function of (path, os, home): it never reads the running machine, so
    // the fixture home below is the only home these cases see.
    private const string WindowsHome = @"C:\Users\Alice";
    private const string PosixHome = "/Users/alice";

    private static string HomeFor(OperatingSystemKind os) => os == OperatingSystemKind.Windows ? WindowsHome : PosixHome;

    [Theory]
    // Installer-owned trees: the path and everything under it.
    [InlineData(@"C:\Windows", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Windows\System32", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Program Files", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Program Files\Some SDK", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Program Files (x86)", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\ProgramData", OperatingSystemKind.Windows, true)]
    // Any drive letter, not just C:.
    [InlineData(@"D:\Program Files", OperatingSystemKind.Windows, true)]
    [InlineData(@"D:\Windows", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Users\Alice\Desktop", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Users\Alice\Documents", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Users\Alice\Downloads", OperatingSystemKind.Windows, true)]
    [InlineData(@"C:\Users\Alice\OneDrive", OperatingSystemKind.Windows, true)]
    // The profile root itself is protected...
    [InlineData(@"C:\Users\Alice", OperatingSystemKind.Windows, true)]
    // ...but its children are exactly the caches this tool exists to clean.
    [InlineData(@"C:\Users\Alice\AppData\Local", OperatingSystemKind.Windows, false)]
    [InlineData(@"C:\Users\Alice\.nuget\packages", OperatingSystemKind.Windows, false)]
    [InlineData(@"C:\Users\Alice\go\pkg\mod", OperatingSystemKind.Windows, false)]
    [InlineData(@"C:\Projects\myapp", OperatingSystemKind.Windows, false)]
    public void Windows_denylist(string path, OperatingSystemKind os, bool expectedDenied)
    {
        Assert.Equal(expectedDenied, Denylist.IsDenied(path, os, HomeFor(os)));
    }

    [Theory]
    [InlineData("/", OperatingSystemKind.Linux, true)]
    [InlineData("/usr", OperatingSystemKind.Linux, true)]
    [InlineData("/usr/local", OperatingSystemKind.Linux, true)]
    [InlineData("/usr/local/go/bin", OperatingSystemKind.Linux, true)]
    [InlineData("/etc", OperatingSystemKind.Linux, true)]
    [InlineData("/var", OperatingSystemKind.Linux, true)]
    [InlineData("/var/cache", OperatingSystemKind.Linux, true)]
    [InlineData("/boot", OperatingSystemKind.Linux, true)]
    [InlineData("/bin", OperatingSystemKind.Linux, true)]
    [InlineData("/sbin", OperatingSystemKind.Linux, true)]
    [InlineData("/lib", OperatingSystemKind.Linux, true)]
    [InlineData("/lib64", OperatingSystemKind.Linux, true)]
    [InlineData("/opt", OperatingSystemKind.Linux, true)]
    // /home is protected as a ROOT, but a module cache under it is the whole point of the tool.
    [InlineData("/home", OperatingSystemKind.Linux, true)]
    [InlineData("/home/alice/go/pkg/mod", OperatingSystemKind.Linux, false)]
    [InlineData("/home/alice/.cache", OperatingSystemKind.Linux, false)]
    [InlineData("/tmp", OperatingSystemKind.Linux, false)]
    [InlineData("/srv/projects", OperatingSystemKind.Linux, false)]
    public void Linux_denylist(string path, OperatingSystemKind os, bool expectedDenied)
    {
        Assert.Equal(expectedDenied, Denylist.IsDenied(path, os, HomeFor(os)));
    }

    [Theory]
    [InlineData("/", OperatingSystemKind.MacOS, true)]
    [InlineData("/System", OperatingSystemKind.MacOS, true)]
    [InlineData("/System/Library", OperatingSystemKind.MacOS, true)]
    [InlineData("/Library", OperatingSystemKind.MacOS, true)]
    [InlineData("/Users", OperatingSystemKind.MacOS, true)]
    // The user's own home is a protected ROOT on macOS, exactly as on Windows and Linux...
    [InlineData("/Users/alice", OperatingSystemKind.MacOS, true)]
    // ...but what lives inside it is precisely what this tool exists to clean.
    [InlineData("/Users/alice/Library/Caches", OperatingSystemKind.MacOS, false)]
    [InlineData("/Users/alice/go/pkg/mod", OperatingSystemKind.MacOS, false)]
    [InlineData("/Users/alice/Desktop", OperatingSystemKind.MacOS, true)]
    [InlineData("/Users/alice/Documents", OperatingSystemKind.MacOS, true)]
    [InlineData("/Users/alice/Downloads", OperatingSystemKind.MacOS, true)]
    [InlineData("/Applications", OperatingSystemKind.MacOS, false)]
    // /opt is protected on Linux; on macOS it is where Homebrew keeps user-managed toolchains.
    [InlineData("/opt/homebrew", OperatingSystemKind.MacOS, false)]
    [InlineData("/opt", OperatingSystemKind.Linux, true)]
    public void MacOS_denylist(string path, OperatingSystemKind os, bool expectedDenied)
    {
        Assert.Equal(expectedDenied, Denylist.IsDenied(path, os, HomeFor(os)));
    }

    [Fact]
    public void WhichDenied_names_the_matching_entry()
    {
        Assert.Equal("/usr", Denylist.WhichDenied("/usr/local/go", OperatingSystemKind.Linux, PosixHome));
        Assert.Equal("{0}:\\program files", Denylist.WhichDenied(@"C:\Program Files\SDK", OperatingSystemKind.Windows, WindowsHome));
        Assert.Equal("{home}", Denylist.WhichDenied(@"C:\Users\Alice", OperatingSystemKind.Windows, WindowsHome));
        Assert.Null(Denylist.WhichDenied("/srv/projects", OperatingSystemKind.Linux, PosixHome));
    }

    [Fact]
    public void A_denial_without_a_home_directory_is_not_silently_ignored()
    {
        // The home rules cannot be evaluated without a home. Refusing to throw keeps the scanner
        // alive, but the root's fingerprint check is what actually stops a bad resolve (FR-03).
        Assert.False(Denylist.IsDenied(@"C:\Users\Alice", OperatingSystemKind.Windows, homeDirectory: null));
        Assert.True(Denylist.IsDenied(@"C:\Program Files", OperatingSystemKind.Windows, homeDirectory: null));
    }

    [Fact]
    public void Denylist_is_independent_of_the_running_machine()
    {
        // Same inputs, same answer, regardless of what this machine's home happens to be.
        Assert.True(Denylist.IsDenied("/home", OperatingSystemKind.Linux, "/home/alice"));
        Assert.True(Denylist.IsDenied("/home", OperatingSystemKind.Linux, "/somewhere/else"));
        Assert.False(Denylist.IsDenied("/home/alice/.cache", OperatingSystemKind.Linux, "/somewhere/else"));
    }
}

public sealed class ContainmentCheckTests
{
    [Fact]
    public void Symlink_escape_is_rejected()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/pkg")
            .AddDirectory("/cache/pkg/safe")
            .AddSymlink("/cache/pkg/escape", "/etc/secret");

        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "/cache/pkg", "/cache/pkg/escape");

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.EscapedRoot, result.Reason);
    }

    [Fact]
    public void Junction_escape_is_rejected()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Windows)
            .AddDirectory(@"C:\cache\pkg")
            .AddDirectory(@"C:\cache\pkg\safe")
            .AddJunction(@"C:\cache\pkg\loop", @"C:\");

        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Windows);
        var result = ContainmentCheck.Check(paths, @"C:\cache\pkg", @"C:\cache\pkg\loop");

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.EscapedRoot, result.Reason);
    }

    [Fact]
    public void Valid_child_path_is_accepted()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/pkg")
            .AddDirectory("/cache/pkg/sub")
            .AddFile("/cache/pkg/sub/file.dll", "x");

        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "/cache/pkg", "/cache/pkg/sub/file.dll");

        Assert.True(result.Safe);
    }

    [Fact]
    public void Root_itself_is_rejected()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/pkg");

        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "/cache/pkg", "/cache/pkg");

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.IsRoot, result.Reason);
    }

    [Fact]
    public void Denied_path_is_rejected_before_containment()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/pkg");

        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "/cache/pkg", "/etc/passwd");

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.Denied, result.Reason);
    }

    [Fact]
    public void Empty_root_is_rejected()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "", "/cache/pkg/file");

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.InvalidRoot, result.Reason);
    }

    [Fact]
    public void Empty_candidate_is_rejected()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "/cache/pkg", "");

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.CanonicalizationFailed, result.Reason);
    }

    [Fact]
    public void Canonicalized_path_stays_within_root()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux)
            .AddDirectory("/cache/pkg")
            .AddDirectory("/cache/pkg/sub");

        var paths = new IFileSystemPaths(fs, OperatingSystemKind.Linux);
        var result = ContainmentCheck.Check(paths, "/cache/pkg", "/cache/pkg/sub/../file");

        Assert.True(result.Safe);
    }
}

/// <summary>Card T-004 acceptance: long-path round-trip with \\?\ forms.</summary>
public sealed class LongPathTests
{
    [Theory]
    [InlineData(@"\\?\C:\foo\bar", @"C:\foo\bar")]
    [InlineData(@"\\?\C:\foo\..\bar", @"C:\bar")]
    [InlineData(@"\\?\C:\a\b\c\d\..\..\e", @"C:\a\b\e")]
    public void LongPath_round_trips_without_prefix(string input, string expected)
    {
        var result = PathCanonicalizer.Canonicalize(input, OperatingSystemKind.Windows);
        Assert.True(result.Ok);
        Assert.Equal(expected, result.Path);
    }
}

/// <summary>
/// Card T-004 acceptance: property-based tests for the canonicalization invariants. Written as a
/// seeded randomized sweep rather than a generator library, so a failure is reproducible from the
/// printed seed and the suite carries no extra dependency.
/// </summary>
public sealed class PropertyBasedTests
{
    private static readonly OperatingSystemKind[] AllOses =
        [OperatingSystemKind.Linux, OperatingSystemKind.Windows, OperatingSystemKind.MacOS];

    private const int Iterations = 2000;

    private static readonly string[] Segments = ["a", "b", "c", "..", ".", "d"];

    /// <summary>Canonicalize is idempotent: applying it twice yields the same canonical path.</summary>
    [Fact]
    public void Canonicalize_is_idempotent()
    {
        var random = new Random(20260929);
        var failures = new List<string>();

        for (int i = 0; i < Iterations; i++)
        {
            string path = BuildPath(random);

            foreach (OperatingSystemKind os in AllOses)
            {
                CanonicalResult first = PathCanonicalizer.Canonicalize(path, os);
                if (!first.Ok)
                {
                    continue;
                }

                CanonicalResult second = PathCanonicalizer.Canonicalize(first.Path, os);
                if (!second.Ok || !string.Equals(first.Path, second.Path, StringComparison.Ordinal))
                {
                    failures.Add($"{os}: '{path}' -> '{first.Path}' -> '{(second.Ok ? second.Path : second.Message)}'");
                }
            }
        }

        Assert.True(failures.Count == 0, "Canonicalize is not idempotent:\n" + string.Join("\n", failures.Take(10)));
    }

    /// <summary>
    /// A path can never canonicalize ABOVE the filesystem root, however many <c>..</c> segments it
    /// carries (E-3). Note this is deliberately weaker than "a path never leaves its root": a
    /// <c>..</c> segment is MEANT to move a path, and <c>/cache/pkg/../other</c> legitimately
    /// becomes <c>/cache/other</c>. The safety property is the floor, not immobility — which is why
    /// the gateway re-verifies containment against the resolved path before it mutates anything.
    /// </summary>
    [Fact]
    public void Canonicalize_never_climbs_above_the_filesystem_root()
    {
        var random = new Random(20260930);
        var failures = new List<string>();

        for (int i = 0; i < Iterations; i++)
        {
            string path = BuildPath(random);
            CanonicalResult result = PathCanonicalizer.Canonicalize(path, OperatingSystemKind.Linux);

            if (result.Ok)
            {
                // A canonical absolute POSIX path is either "/" or starts with "/" and has no
                // ".." left in it.
                bool rooted = result.Path == "/" || result.Path.StartsWith("/", StringComparison.Ordinal);
                bool noTraversal = !result.Path.Split('/').Contains("..");
                if (!rooted || !noTraversal)
                {
                    failures.Add($"'{path}' -> '{result.Path}'");
                }
            }
        }

        Assert.True(failures.Count == 0, "Canonicalize escaped the filesystem root:\n" + string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void A_parent_segment_past_the_root_is_refused_never_clamped()
    {
        // Silently clamping /a/../../etc to /etc would be the worst possible failure: the caller
        // would believe it validated one path while the tool acted on another. Refusing is the only
        // acceptable answer, and both spellings below must refuse.
        foreach (string input in new[] { "/..", "/../..", "/a/../../etc" })
        {
            CanonicalResult result = PathCanonicalizer.Canonicalize(input, OperatingSystemKind.Linux);

            Assert.False(result.Ok, $"Expected '{input}' to be refused rather than clamped.");
            Assert.Equal(CanonicalFailure.EscapedRoot, result.Reason);
        }
    }

    [Fact]
    public void A_parent_segment_that_lands_exactly_on_the_root_is_allowed()
    {
        // /a/.. does not escape anything — it lands on the root, which is a legitimate path.
        CanonicalResult result = PathCanonicalizer.Canonicalize("/a/..", OperatingSystemKind.Linux);
        Assert.True(result.Ok);
        Assert.Equal("/", result.Path);
    }

    /// <summary>Combining an untrusted relative segment onto a root can never climb above it.</summary>
    [Fact]
    public void Combine_never_climbs_above_the_root()
    {
        var random = new Random(20261001);
        var failures = new List<string>();

        for (int i = 0; i < Iterations; i++)
        {
            string[] relative = BuildSegments(random, max: 6);
            string root = "/cache/pkg";
            string combined = PathCanonicalizer.Combine(root, string.Join('/', relative), OperatingSystemKind.Linux);

            if (!combined.StartsWith("/cache/pkg", StringComparison.Ordinal))
            {
                failures.Add($"relative='{string.Join('/', relative)}' combined='{combined}'");
            }
        }

        Assert.True(failures.Count == 0, "Combine escaped the root:\n" + string.Join("\n", failures.Take(10)));
    }

    /// <summary>A <c>..</c> that climbs above the root is rejected, never silently clamped.</summary>
    [Fact]
    public void Parent_traversal_above_the_root_is_rejected()
    {
        foreach (OperatingSystemKind os in AllOses)
        {
            CanonicalResult result = PathCanonicalizer.Canonicalize(
                os == OperatingSystemKind.Windows ? @"C:\a\..\..\..\escape" : "/a/../../../escape", os);

            Assert.False(result.Ok, $"Expected {os} to reject a path that climbs above the root.");
            Assert.NotEqual(CanonicalFailure.None, result.Reason);
        }
    }

    private static string[] BuildSegments(Random random, int max) =>
        [.. Enumerable.Range(0, random.Next(0, max + 1)).Select(_ => Segments[random.Next(Segments.Length)])];

    private static string BuildPath(Random random) => "/" + string.Join('/', BuildSegments(random, 6));
}

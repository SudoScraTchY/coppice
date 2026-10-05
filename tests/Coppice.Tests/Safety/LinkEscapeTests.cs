using Coppice.Core.PathSafety;
using Coppice.Ports;
using Coppice.Tests.Support;
using FsCheck.Fluent;
using Xunit;

namespace Coppice.Tests.Safety;

/// <summary>
/// The escape suite for LINK RESOLUTION (T-028, 11-testing E-1 symlink farms, E-2 junction loops).
/// <para>
/// E-3…E-9 in <see cref="PathSafetyEscapeTests"/> test the canonicalizer on a path STRING. These test the
/// filesystem: a link is only dangerous once resolved, and the resolution is where the string stops being
/// what the OS will actually open.
/// </para>
/// <para>
/// The invariant: <b>a link that leaves the root is refused, and a loop terminates</b>. The second half
/// matters as much as the first — a resolver that follows a cycle forever is a scan that hangs on a
/// directory the user cannot delete from Explorer either.
/// </para>
/// </summary>
public sealed class LinkEscapeTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;

    private static string Root => @"C:\cache";

    private static string Outside => @"C:\Windows\System32";

    private static IFileSystemPaths Paths(FakeFileSystem fs) => new(fs, Win);

    /// <summary>A fake filesystem with the root and its immediate children present.</summary>
    private static FakeFileSystem WithRoot(params string[] children)
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        foreach (string child in children)
        {
            fs.AddDirectory(PathCanonicalizer.Combine(Root, child, Win));
        }

        return fs;
    }

    // ---- E-1: symlink farms -------------------------------------------------------------------------

    [Fact]
    public void E1_A_symlink_out_of_the_root_is_refused()
    {
        FakeFileSystem fs = WithRoot("innocent");
        fs.AddSymlink(PathCanonicalizer.Combine(Root, "escape", Win), Outside);

        ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, PathCanonicalizer.Combine(Root, "escape", Win));

        Assert.False(result.Safe, "a link pointing outside the root must be refused");
        Assert.Equal(ContainmentFailure.EscapedRoot, result.Reason);
    }

    [Fact]
    public void E1_A_symlink_that_stays_inside_the_root_is_allowed()
    {
        // The other half, and the one a paranoid check gets wrong: refusing every link would refuse
        // package managers that legitimately symlink one cache entry to another.
        FakeFileSystem fs = WithRoot("innocent");
        fs.AddSymlink(PathCanonicalizer.Combine(Root, "link", Win), PathCanonicalizer.Combine(Root, "innocent", Win));

        ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, PathCanonicalizer.Combine(Root, "link", Win));

        Assert.True(result.Safe, $"an in-root link must be allowed: {result.Message}");
    }

    [Fact]
    public void E1_A_symlink_to_the_root_itself_is_refused_as_the_root()
    {
        // Distinct from an escape: "you named the root" and "you tried to leave it" need different
        // diagnoses, because only one of them is an attack.
        FakeFileSystem fs = WithRoot("innocent");
        fs.AddSymlink(PathCanonicalizer.Combine(Root, "link", Win), Root);

        ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, PathCanonicalizer.Combine(Root, "link", Win));

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.IsRoot, result.Reason);
    }

    [Fact]
    public void E1_A_farm_of_links_all_pointing_outside_is_refused_individually()
    {
        PropertyRunner.Run(
            nameof(E1_A_farm_of_links_all_pointing_outside_is_refused_individually),
            HostileNames.LinkNames(),
            Gen.Elements("/etc", @"C:\Windows", "/", Root + "/../../.."),
            (linkName, target) =>
            {
                FakeFileSystem fs = WithRoot();
                string linkPath = PathCanonicalizer.Combine(Root, linkName, Win);
                fs.AddSymlink(linkPath, target);

                ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, linkPath);

                // Either the link is refused, or it resolved to something genuinely inside the root. What
                // must never happen is a "safe" verdict on a path that leaves.
                return !result.Safe || IsInsideRoot(fs, linkPath);
            });
    }

    [Fact]
    public void E1_a_symlink_chain_terminating_outside_is_refused_at_the_end()
    {
        // a -> b -> c -> /etc. Resolving only the FIRST hop would report "inside the root" for a.
        FakeFileSystem fs = WithRoot();
        fs.AddSymlink(PathCanonicalizer.Combine(Root, "a", Win), PathCanonicalizer.Combine(Root, "b", Win));
        fs.AddSymlink(PathCanonicalizer.Combine(Root, "b", Win), PathCanonicalizer.Combine(Root, "c", Win));
        fs.AddSymlink(PathCanonicalizer.Combine(Root, "c", Win), Outside);

        ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, PathCanonicalizer.Combine(Root, "a", Win));

        Assert.False(result.Safe, "a three-hop chain leaving the root must be refused");
        Assert.Equal(ContainmentFailure.EscapedRoot, result.Reason);
    }

    // ---- E-2: junction / reparse loops ---------------------------------------------------------------

    [Fact]
    public void E2_a_junction_loop_terminates_rather_than_hanging()
    {
        FakeFileSystem fs = WithRoot();
        fs.AddJunction(PathCanonicalizer.Combine(Root, "loop", Win), PathCanonicalizer.Combine(Root, "loop", Win));
        fs.AddJunction(PathCanonicalizer.Combine(Root, "a", Win), PathCanonicalizer.Combine(Root, "b", Win));
        fs.AddJunction(PathCanonicalizer.Combine(Root, "b", Win), PathCanonicalizer.Combine(Root, "a", Win));

        foreach (string name in new[] { "loop", "a", "b" })
        {
            string path = PathCanonicalizer.Combine(Root, name, Win);

            // The call returning at all is half the assertion. A resolver that walks a cycle without a
            // hop limit hangs the scan on a directory the user cannot remove either.
            ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, path);

            Assert.False(result.Safe, $"junction loop '{name}' must not be reported safe");
        }
    }

    [Fact]
    public void E2_a_long_cycle_still_terminates()
    {
        FakeFileSystem fs = WithRoot();
        string root = Root;

        for (int i = 0; i < 30; i++)
        {
            fs.AddJunction(
                PathCanonicalizer.Combine(root, $"n{i}", Win),
                PathCanonicalizer.Combine(root, $"n{(i + 1) % 30}", Win));
        }

        ContainmentResult result = ContainmentCheck.Check(Paths(fs), root, PathCanonicalizer.Combine(root, "n0", Win));

        // Whatever it decides, it decided. The assertion is that this line is reached at all.
        Assert.False(result.Safe, "a 30-hop cycle must not be reported safe");
    }

    [Fact]
    public void E2_a_junction_out_of_the_root_is_refused()
    {
        FakeFileSystem fs = WithRoot();
        string junction = PathCanonicalizer.Combine(Root, "out", Win);
        fs.AddJunction(junction, Outside);

        ContainmentResult result = ContainmentCheck.Check(Paths(fs), Root, junction);

        Assert.False(result.Safe);
        Assert.Equal(ContainmentFailure.EscapedRoot, result.Reason);
    }

    [Fact]
    public void E2_a_link_through_a_traversal_that_lands_inside_is_not_an_escape()
    {
        // "../cache/innocent" leaves the root textually and arrives back inside it. Refusing it would be
        // over-refusal; accepting it is correct, because the RESOLVED path is what the OS opens.
        FakeFileSystem fs = WithRoot("innocent");

        ContainmentResult result = ContainmentCheck.Check(
            Paths(fs),
            Root,
            PathCanonicalizer.Combine(Root, "..", Win) + @"\cache\innocent");

        Assert.True(result.Safe, $"a path that normalizes back inside is safe: {result.Message}");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>Whether the candidate's RESOLVED path is genuinely within the root.</summary>
    private static bool IsInsideRoot(FakeFileSystem fs, string candidate)
    {
        IFileSystemPaths paths = Paths(fs);
        CanonicalResult canonical = paths.Canonicalize(candidate);

        if (!canonical.Ok)
        {
            return true;
        }

        string resolved = paths.ResolveRealPath(canonical.Path);
        char sep = PathCanonicalizer.SeparatorFor(Win);

        return resolved.StartsWith(Root + sep, StringComparison.OrdinalIgnoreCase);
    }
}

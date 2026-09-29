using System.Reflection;
using Coppice.Ports;

namespace Coppice.Tests.Support;

/// <summary>Card T-002 acceptance: the fake VFS must round-trip a fixture tree and express link escapes.</summary>
public sealed class FakeFileSystemTests
{
    private static FakeFileSystem Tree() => new FakeFileSystem(OperatingSystemKind.Linux)
        .AddFile("/cache/pkg/1.0.0/a.dll", "aaa")
        .AddFile("/cache/pkg/1.0.0/b.dll", "bbbb")
        .AddFile("/cache/pkg/2.0.0/c.dll", "ccccc");

    [Fact]
    public void Create_enumerate_read_back()
    {
        FakeFileSystem fs = Tree();

        Assert.True(fs.DirectoryExists("/cache/pkg/1.0.0"));
        Assert.True(fs.FileExists("/cache/pkg/1.0.0/a.dll"));

        Assert.Equal("aaa", fs.ReadSmallText("/cache/pkg/1.0.0/a.dll"));
        Assert.Equal("ccccc", EncodingOf(fs.ReadSmallBytes("/cache/pkg/2.0.0/c.dll")));
    }

    [Fact]
    public void MeasureSize_counts_bytes_recursively()
    {
        Assert.Equal(12UL, Tree().MeasureSize("/cache"));
    }

    [Fact]
    public void MeasureSize_counts_a_hard_linked_file_once()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .AddFileOfSize("/cache/real.bin", 1000, fileIdentity: "inode-1")
            .AddHardLink("/cache/alias.bin", "/cache/real.bin");

        Assert.Equal(1000UL, fs.MeasureSize("/cache"));
    }

    [Fact]
    public void Can_express_a_symlink_pointing_outside_the_root()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .AddFile("/cache/pkg/1.0.0/a.dll", "x")
            .AddFile("/etc/secret", "s3cr3t")
            .AddSymlink("/cache/pkg/1.0.0/escape", "/etc/secret");

        FileEntry entry = fs.GetEntry("/cache/pkg/1.0.0/escape")!;
        Assert.Equal(EntryKind.Symlink, entry.Kind);
        Assert.True(entry.IsLink);
        Assert.Equal("/etc/secret", entry.LinkTarget);
        Assert.Equal("/etc/secret", fs.ResolveLinkTarget("/cache/pkg/1.0.0/escape"));
    }

    [Fact]
    public void Can_express_a_junction_loop()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .AddFile("/cache/pkg/a.dll", "x")
            .AddJunction("/cache/pkg/loop", "/cache/pkg");

        Assert.Equal(EntryKind.Junction, fs.GetEntry("/cache/pkg/loop")!.Kind);
        Assert.Equal("/cache/pkg", fs.ResolveLinkTarget("/cache/pkg/loop"));
    }

    [Fact]
    public void Enumeration_does_not_descend_links_by_default()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .AddFile("/cache/pkg/a.dll", "x")
            .AddJunction("/cache/pkg/loop", "/cache/pkg");

        IReadOnlyList<FileEntry> entries = fs.EnumerateEntries("/cache/pkg");
        Assert.Contains(entries, e => e.Kind == EntryKind.Junction);
        Assert.Single(entries, e => e.Name == "a.dll");
    }

    [Fact]
    public void Locked_and_readonly_states_are_representable()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .AddFile("/cache/locked.bin", "x", lockState: LockState.Locked)
            .AddFile("/cache/ro.bin", "x", readOnly: true);

        Assert.Equal(LockState.Locked, fs.ProbeLock("/cache/locked.bin"));
        Assert.Equal("true", fs.GetEntry("/cache/ro.bin")!.Attributes["ReadOnly"]);
    }

    [Fact]
    public void Move_renames_a_whole_subtree()
    {
        FakeFileSystem fs = Tree();
        fs.Move("/cache/pkg/1.0.0", "/quarantine/pkg-1.0.0");

        Assert.False(fs.DirectoryExists("/cache/pkg/1.0.0"));
        Assert.True(fs.FileExists("/quarantine/pkg-1.0.0/a.dll"));
    }

    [Fact]
    public void Delete_removes_children_when_recursive()
    {
        FakeFileSystem fs = Tree();
        fs.DeleteFile("/cache/pkg/1.0.0", recursive: true);

        Assert.False(fs.FileExists("/cache/pkg/1.0.0/a.dll"));
        Assert.True(fs.DirectoryExists("/cache/pkg"));
    }

    [Fact]
    public void Missing_paths_behave_like_a_clean_machine()
    {
        FakeFileSystem fs = new FakeFileSystem();
        Assert.False(fs.DirectoryExists("/nope"));
        Assert.Empty(fs.EnumerateEntries("/nope"));
        Assert.Equal(0UL, fs.MeasureSize("/nope"));
        Assert.Throws<FileNotFoundException>(() => fs.ReadSmallText("/nope"));
    }

    private static string EncodingOf(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);
}

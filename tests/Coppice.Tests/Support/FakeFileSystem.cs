using System.Text;
using Coppice.Ports;

namespace Coppice.Tests.Support;

/// <summary>
/// In-memory <see cref="IFileSystem"/> for the fake OS. It can express what the real filesystem can
/// express and the escape suite needs: symlinks and junctions pointing outside a root, junction
/// loops, hard-linked files with a shared identity, locked files, and read-only files (E-1, E-2, E-5, E-12).
/// </summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly OperatingSystemKind _os;
    private readonly char _separator;

    public FakeFileSystem(OperatingSystemKind os = OperatingSystemKind.Linux)
    {
        _os = os;
        _separator = os == OperatingSystemKind.Windows ? '\\' : '/';
    }

    public char Separator => _separator;

    private sealed record Node(
        EntryKind Kind,
        byte[]? Content,
        string? LinkTarget,
        string? FileIdentity,
        LockState Lock,
        bool ReadOnly,
        DateTimeOffset LastWrite);

    // ---- Fixture construction ----

    /// <summary>
    /// Adds a directory, creating any missing ancestors. Ancestors are implied by the path, so
    /// creating them here keeps <c>AddDirectory</c> consistent with <c>AddFile</c> — otherwise
    /// <c>AddDirectory("/cache/pkg/1.0")</c> silently fails to make "/cache" exist, and every later
    /// assertion about that tree fails for a reason that has nothing to do with the code under test.
    /// </summary>
    public FakeFileSystem AddDirectory(string path)
    {
        string full = Normalize(path);
        _nodes[full] = new Node(EntryKind.Directory, null, null, null, LockState.Unlocked, false, DateTimeOffset.UnixEpoch);
        EnsureParentDirectories(full);
        return this;
    }

    public FakeFileSystem AddFile(string path, string content = "", bool readOnly = false, LockState lockState = LockState.Unlocked, string? fileIdentity = null)
    {
        string full = Normalize(path);
        _nodes[full] = new Node(
            EntryKind.File,
            Encoding.UTF8.GetBytes(content),
            null,
            fileIdentity ?? $"id:{full}",
            lockState,
            readOnly,
            DateTimeOffset.UnixEpoch);
        EnsureParentDirectories(full);
        return this;
    }

    public FakeFileSystem AddFileOfSize(string path, int sizeInBytes, string? fileIdentity = null)
    {
        string full = Normalize(path);
        _nodes[full] = new Node(EntryKind.File, new byte[sizeInBytes], null, fileIdentity ?? $"id:{full}", LockState.Unlocked, false, DateTimeOffset.UnixEpoch);
        EnsureParentDirectories(full);
        return this;
    }

    /// <summary>A second path sharing the first's identity — a hard link (FR-05 sizing must count it once).</summary>
    public FakeFileSystem AddHardLink(string linkPath, string targetPath)
    {
        string full = Normalize(linkPath);
        string target = Normalize(targetPath);
        if (!_nodes.TryGetValue(target, out Node? node))
        {
            throw new InvalidOperationException($"Cannot hard-link to missing file '{targetPath}'.");
        }

        _nodes[full] = node with { FileIdentity = node.FileIdentity };
        EnsureParentDirectories(full);
        return this;
    }

    public FakeFileSystem AddSymlink(string linkPath, string targetPath)
    {
        string full = Normalize(linkPath);
        _nodes[full] = new Node(EntryKind.Symlink, null, targetPath, null, LockState.Unlocked, false, DateTimeOffset.UnixEpoch);
        EnsureParentDirectories(full);
        return this;
    }

    public FakeFileSystem AddJunction(string linkPath, string targetPath)
    {
        string full = Normalize(linkPath);
        _nodes[full] = new Node(EntryKind.Junction, null, targetPath, null, LockState.Unlocked, false, DateTimeOffset.UnixEpoch);
        EnsureParentDirectories(full);
        return this;
    }

    private void EnsureParentDirectories(string full)
    {
        string? parent = ParentOf(full);
        while (!string.IsNullOrEmpty(parent))
        {
            if (!_nodes.ContainsKey(parent))
            {
                _nodes[parent] = new Node(EntryKind.Directory, null, null, null, LockState.Unlocked, false, DateTimeOffset.UnixEpoch);
            }

            parent = ParentOf(parent);
        }
    }

    // ---- IFileSystem ----

    public bool DirectoryExists(string path) =>
        _nodes.TryGetValue(Normalize(path), out Node? node) && node.Kind == EntryKind.Directory;

    public bool FileExists(string path) =>
        _nodes.TryGetValue(Normalize(path), out Node? node) && node.Kind == EntryKind.File;

    public IReadOnlyList<FileEntry> EnumerateEntries(string path, EnumerationRequest? options = null)
    {
        options ??= new EnumerationRequest();
        string root = Normalize(path);
        if (!_nodes.TryGetValue(root, out Node? rootNode) || rootNode.Kind != EntryKind.Directory)
        {
            return [];
        }

        string prefix = root.EndsWith(_separator) ? root : root + _separator;
        var results = new List<FileEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };

        foreach ((string full, Node node) in _nodes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileEntry? entry = GetEntry(full);
            if (entry is null || (entry.IsLink && !options.IncludeLinks))
            {
                continue;
            }

            // MaxDepth counts levels below the root, so MaxDepth = 1 means immediate children only
            // (matching System.IO's TopDirectoryOnly). It must agree with PhysicalFileSystem or a
            // test can pass against the fake and fail on a real machine.
            int level = CountDepth(full, prefix) + 1;
            if (options.MaxDepth is int max && level > max)
            {
                continue;
            }

            results.Add(entry);

            if (options.FollowLinks && entry.Kind == EntryKind.Directory)
            {
                // Guard against a junction loop: a reparse point can point back at an ancestor (E-2).
                visited.Add(ResolveRealPath(full));
            }
        }

        return results;
    }

    public FileEntry? GetEntry(string path)
    {
        string full = Normalize(path);
        if (!_nodes.TryGetValue(full, out Node? node))
        {
            return null;
        }

        return new FileEntry
        {
            Path = full,
            Name = NameOf(full),
            Kind = node.Kind,
            Length = node.Content?.LongLength ?? 0,
            LastWriteTimeUtc = node.LastWrite,
            Attributes = node.ReadOnly ? new Dictionary<string, string> { ["ReadOnly"] = "true" } : ReadOnlyDictionaryExtensions.Empty,
            Lock = node.Lock,
            LinkTarget = node.LinkTarget,
            FileIdentity = node.FileIdentity,
        };
    }

    public string ResolveLinkTarget(string path) => ResolveRealPath(Normalize(path));

    private string ResolveRealPath(string path)
    {
        string current = path;
        for (int hop = 0; hop < 40; hop++)
        {
            if (!_nodes.TryGetValue(current, out Node? node) || node.LinkTarget is null)
            {
                return current;
            }

            string target = Normalize(node.LinkTarget);
            if (!_nodes.ContainsKey(target))
            {
                return target;
            }

            if (!current.EndsWith(_separator) && !target.EndsWith(_separator))
            {
                return target;
            }

            string combined = IsRooted(target)
                ? target
                : current[..(current.LastIndexOf(_separator) + 1)] + target;
            combined = Normalize(combined);

            if (string.Equals(combined, current, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = combined;
        }

        return current;
    }

    public string ReadSmallText(string path, int maxBytes = 64 * 1024)
    {
        string full = Normalize(path);
        if (!_nodes.TryGetValue(full, out Node? node) || node.Content is null)
        {
            throw new FileNotFoundException("Not found in the fake filesystem.", path);
        }

        return Encoding.UTF8.GetString(node.Content, 0, Math.Min(node.Content.Length, maxBytes));
    }

    public byte[] ReadSmallBytes(string path, int maxBytes = 64 * 1024)
    {
        string full = Normalize(path);
        if (!_nodes.TryGetValue(full, out Node? node) || node.Content is null)
        {
            throw new FileNotFoundException("Not found in the fake filesystem.", path);
        }

        if (node.Content.Length > maxBytes)
        {
            throw new IOException($"'{path}' is larger than the {maxBytes}-byte small-file limit.");
        }

        return node.Content;
    }

    public ulong MeasureSize(string path)
    {
        string full = Normalize(path);
        if (!_nodes.TryGetValue(full, out Node? node))
        {
            return 0;
        }

        if (node.Kind == EntryKind.File)
        {
            return (ulong)node.Content!.LongLength;
        }

        string prefix = full.EndsWith(_separator) ? full : full + _separator;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        ulong total = 0;

        foreach ((string child, Node childNode) in _nodes)
        {
            if (childNode.Kind == EntryKind.File
                && child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && identities.Add(childNode.FileIdentity ?? child))
            {
                total += (ulong)childNode.Content!.LongLength;
            }
        }

        return total;
    }

    public LockState ProbeLock(string path) =>
        _nodes.TryGetValue(Normalize(path), out Node? node) ? node.Lock : LockState.Unknown;

    public void CreateDirectory(string path)
    {
        Writes.Add($"CreateDirectory {path}");
        string full = Normalize(path);
        _nodes[full] = new Node(EntryKind.Directory, null, null, null, LockState.Unlocked, false, DateTimeOffset.UnixEpoch);
        EnsureParentDirectories(full);
    }

    public void Move(string sourcePath, string destinationPath)
    {
        Writes.Add($"Move {sourcePath} -> {destinationPath}");
        string source = Normalize(sourcePath);
        string destination = Normalize(destinationPath);
        if (!_nodes.TryGetValue(source, out Node? node))
        {
            throw new FileNotFoundException("Move source not found.", sourcePath);
        }

        string prefix = source + _separator;
        var moved = _nodes.Where(p => p.Key == source || p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => destination + p.Key[source.Length..], p => p.Value, StringComparer.OrdinalIgnoreCase);

        foreach (string key in _nodes.Keys.Where(k => k == source || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _nodes.Remove(key);
        }

        foreach ((string key, Node value) in moved)
        {
            _nodes[key] = value;
        }

        EnsureParentDirectories(destination);
    }

    public void DeleteFile(string path, bool recursive)
    {
        Writes.Add($"DeleteFile {path} recursive={recursive}");
        string full = Normalize(path);
        if (!_nodes.Remove(full))
        {
            if (recursive)
            {
                return;
            }

            throw new FileNotFoundException("Delete target not found.", path);
        }

        if (!recursive)
        {
            return;
        }

        string prefix = full + _separator;
        foreach (string key in _nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _nodes.Remove(key);
        }
    }

    /// <summary>
    /// Every mutation attempted on this VFS, in order. The C-1 conformance check asserts this stays
    /// empty for any read-only stage: resolution and scanning must never write anything.
    /// </summary>
    public List<string> Writes { get; } = [];

    // ---- Path helpers ----

    public string Normalize(string path)
    {
        string p = path.Replace(_separator == '\\' ? '/' : '\\', _separator).TrimEnd(_separator);
        if (p.Length == 0)
        {
            p = _separator.ToString();
        }

        return p;
    }

    public string Join(params string[] segments) => Normalize(string.Join(_separator, segments));

    private bool IsRooted(string path) => _separator == '\\' ? path.Length >= 2 && path[1] == ':' : path.StartsWith(_separator);

    private string? ParentOf(string full)
    {
        int index = full.LastIndexOf(_separator);
        if (index <= 0)
        {
            return null;
        }

        return full[..index];
    }

    private string NameOf(string full)
    {
        int index = full.LastIndexOf(_separator);
        return index < 0 ? full : full[(index + 1)..];
    }

    private static int CountDepth(string full, string prefix) =>
        full[prefix.Length..].Count(c => c == '/' || c == '\\');
}

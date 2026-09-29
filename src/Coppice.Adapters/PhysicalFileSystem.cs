using System.Runtime.InteropServices;
using System.Text;
using Coppice.Ports;

namespace Coppice.Adapters;

/// <summary>
/// The production <see cref="IFileSystem"/>. This is the ONLY place in the product that calls
/// <c>System.IO</c> (NFR-08); core and plugins reach the disk exclusively through the port.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private const int MaxLinkHops = 40;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<FileEntry> EnumerateEntries(string path, EnumerationRequest? options = null)
    {
        options ??= new EnumerationRequest();
        if (!Directory.Exists(path))
        {
            return [];
        }

        var results = new List<FileEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(path, depth: 0, options, results, visited);
        return results;
    }

    private void Walk(string directory, int depth, EnumerationRequest options, List<FileEntry> results, HashSet<string> visited)
    {
        if (options.MaxDepth is int max && depth > max)
        {
            return;
        }

        // Guard against junction/symlink loops (E-2): a reparse point can point at an ancestor.
        if (!visited.Add(directory))
        {
            return;
        }

        string[] children;
        try
        {
            children = [.. Directory.EnumerateFileSystemEntries(directory)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (string child in children)
        {
            FileEntry? entry = GetEntry(child);
            if (entry is null)
            {
                continue;
            }

            if (entry.IsLink && !options.IncludeLinks)
            {
                continue;
            }

            results.Add(entry);

            bool descend = entry.Kind == EntryKind.Directory
                || (entry.IsLink && options.FollowLinks && Directory.Exists(child));

            if (descend)
            {
                Walk(child, depth + 1, options, results, visited);
            }
        }
    }

    public FileEntry? GetEntry(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            var info = new FileInfo(path);
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;

            EntryKind kind = isReparse
                ? isDir ? EntryKind.Junction : EntryKind.Symlink
                : isDir ? EntryKind.Directory : EntryKind.File;

            return new FileEntry
            {
                Path = path,
                Name = info.Name,
                Kind = kind,
                Length = isDir ? 0 : info.Exists ? info.Length : 0,
                LastWriteTimeUtc = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : DateTimeOffset.MinValue,
                Attributes = DescribeAttributes(attrs),
                Lock = LockState.Unknown,
                LinkTarget = isReparse ? TryReadLinkTarget(path) : null,
                FileIdentity = isDir ? null : $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.Name}",
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> DescribeAttributes(FileAttributes attrs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FileAttributes flag in Enum.GetValues<FileAttributes>())
        {
            if (flag != 0 && (attrs & flag) == flag)
            {
                map[flag.ToString()] = "true";
            }
        }

        return map;
    }

    private static string? TryReadLinkTarget(string path)
    {
        try
        {
            DirectoryInfo dir = new(path);
            if (dir.Exists && dir.LinkTarget is not null)
            {
                return dir.LinkTarget;
            }

            FileInfo file = new(path);
            return file.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string ResolveLinkTarget(string path)
    {
        var current = path;
        for (int hop = 0; hop < MaxLinkHops; hop++)
        {
            FileEntry? entry = GetEntry(current);
            if (entry is null || !entry.IsLink || string.IsNullOrEmpty(entry.LinkTarget))
            {
                return current;
            }

            current = Path.IsPathRooted(entry.LinkTarget)
                ? entry.LinkTarget
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, entry.LinkTarget));
        }

        // A link loop is never a valid real path; surface it as unchanged so the caller fails closed.
        return current;
    }

    public string ReadSmallText(string path, int maxBytes = 64 * 1024)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[maxBytes];
        int read = reader.Read(buffer, 0, maxBytes);
        return new string(buffer, 0, read);
    }

    public byte[] ReadSmallBytes(string path, int maxBytes = 64 * 1024)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > maxBytes)
        {
            throw new IOException($"'{path}' is larger than the {maxBytes}-byte small-file limit.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Recursive size with hard-link de-duplication (FR-05). Each unique file identity is counted
    /// once, and links are never descended into twice.
    /// </summary>
    public ulong MeasureSize(string path)
    {
        FileEntry? root = GetEntry(path);
        if (root is null)
        {
            return 0;
        }

        if (root.Kind == EntryKind.File)
        {
            return root.Length <= 0 ? 0 : (ulong)root.Length;
        }

        var countedIdentities = new HashSet<string>(StringComparer.Ordinal);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong total = 0;

        foreach (FileEntry entry in EnumerateEntries(path, new EnumerationRequest { FollowLinks = false }))
        {
            if (entry.Kind == EntryKind.File)
            {
                string identity = entry.FileIdentity ?? entry.Path;
                if (countedIdentities.Add(identity))
                {
                    total += entry.Length <= 0 ? 0 : (ulong)entry.Length;
                }
            }
            else if (entry.Kind == EntryKind.Directory)
            {
                visitedDirectories.Add(entry.Path);
            }
        }

        // Directories themselves contribute their own (usually zero) reported size on some platforms.
        if (root.Length > 0)
        {
            total += (ulong)root.Length;
        }

        return total;
    }

    public LockState ProbeLock(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Best-effort: an exclusive open on a locked file fails even with FileShare.None readers.
                using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return LockState.Unlocked;
            }
            catch (IOException)
            {
                return LockState.Locked;
            }
            catch (UnauthorizedAccessException)
            {
                return LockState.Locked;
            }
        }

        return LockState.Unknown;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Move(string sourcePath, string destinationPath)
    {
        string? parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(sourcePath, destinationPath);
    }

    public void DeleteFile(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
            return;
        }

        File.Delete(path);
    }
}

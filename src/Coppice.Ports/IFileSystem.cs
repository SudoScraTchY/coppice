namespace Coppice.Ports;

/// <summary>Target operating system family. Path safety is per-OS, so this is a first-class input.</summary>
public enum OperatingSystemKind
{
    Unknown = 0,
    Windows,
    Linux,
    MacOS,
}

/// <summary>How a directory entry relates to its parent. Drives hard-link and reparse-aware sizing (FR-05).</summary>
public enum EntryKind
{
    Unknown = 0,
    Directory,
    File,
    Symlink,
    Junction,
    ReparsePoint,
}

/// <summary>Whether an entry is locked or in use. Best-effort; a lock causes a skip, never a force (06-safety-model step 5).</summary>
public enum LockState
{
    Unknown = 0,
    Unlocked,
    Locked,
}

/// <summary>Metadata for one filesystem entry, as seen through <see cref="IFileSystem"/>.</summary>
public sealed record FileEntry
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required EntryKind Kind { get; init; }

    /// <summary>Logical size in bytes, before hard-link de-duplication.</summary>
    public long Length { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = ReadOnlyDictionaryExtensions.Empty;

    /// <summary>True when the entry cannot be deleted because it is open or permission-locked.</summary>
    public LockState Lock { get; init; } = LockState.Unknown;

    /// <summary>For links: the declared target, exactly as stored (not resolved).</summary>
    public string? LinkTarget { get; init; }

    /// <summary>Unique identity for hard-link de-duplication. Null when the OS does not expose one.</summary>
    public string? FileIdentity { get; init; }

    public bool IsLink => Kind is EntryKind.Symlink or EntryKind.Junction or EntryKind.ReparsePoint;
}

/// <summary>
/// Enumeration options; deliberately minimal so the fake VFS can honour the same contract.
/// Named <c>EnumerationRequest</c> rather than <c>EnumerationOptions</c> to avoid colliding with
/// <see cref="System.IO.EnumerationOptions"/> in adapter code.
/// </summary>
public sealed record EnumerationRequest
{
    /// <summary>Follow directory links while descending. Roots resolve their own links once; inside a root, default is false.</summary>
    public bool FollowLinks { get; init; }

    /// <summary>Include entries that are themselves links.</summary>
    public bool IncludeLinks { get; init; } = true;

    /// <summary>Maximum directory depth. Null means unlimited.</summary>
    public int? MaxDepth { get; init; }
}

/// <summary>
/// The ONLY filesystem capability in the system (DIP, 04-plugin-contract rule 1).
/// Core and plugins never touch <c>System.IO</c>; mutations live on this interface
/// so the Gateway remains the sole mutator and can be the sole implementer that writes.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>Enumerates immediate children of a directory. Returns empty when the directory is absent.</summary>
    IReadOnlyList<FileEntry> EnumerateEntries(string path, EnumerationRequest? options = null);

    /// <summary>Metadata for a single entry, or null when it does not exist.</summary>
    FileEntry? GetEntry(string path);

    /// <summary>Resolves the real path of a link target, or the path itself when it is not a link. Does not require the target to exist.</summary>
    string ResolveLinkTarget(string path);

    /// <summary>Reads a small text file. Throws <see cref="FileNotFoundException"/> when absent.</summary>
    string ReadSmallText(string path, int maxBytes = 64 * 1024);

    /// <summary>Reads a small binary file. Throws <see cref="FileNotFoundException"/> when absent.</summary>
    byte[] ReadSmallBytes(string path, int maxBytes = 64 * 1024);

    /// <summary>Recursive size in bytes, counting each unique hard-linked file once and not double-counting links.</summary>
    ulong MeasureSize(string path);

    /// <summary>Best-effort lock probe.</summary>
    LockState ProbeLock(string path);

    // ---- Mutation surface. Only the Gateway may call these (06-safety-model). ----

    void CreateDirectory(string path);

    /// <summary>Atomically renames within a volume — the quarantine primitive.</summary>
    void Move(string sourcePath, string destinationPath);

    void DeleteFile(string path, bool recursive);
}

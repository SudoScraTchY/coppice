using System.Runtime.InteropServices;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Adapters;

public sealed class SystemEnvironment : IEnvironment
{
    public OperatingSystemKind OS { get; } = Detect();

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? GetMachineVariable(string name)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
    }

    public string? HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? GetFolderPath(string wellKnownFolder) =>
        Enum.TryParse<Environment.SpecialFolder>(wellKnownFolder, ignoreCase: true, out Environment.SpecialFolder folder)
            ? Environment.GetFolderPath(folder)
            : null;

    public IReadOnlyList<string> PathEntries =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static OperatingSystemKind Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OperatingSystemKind.Windows;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OperatingSystemKind.MacOS : OperatingSystemKind.Linux;
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Filesystem-backed state store. Keys are stored as relative files under a single root so the
/// snapshot history stays inspectable by a human during debugging (v0.4 trends depend on it).
/// </summary>
public sealed class FileSystemStateStore : IStateStore
{
    private readonly string _root;
    private readonly IClock _clock;

    public FileSystemStateStore(string root, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _clock = clock ?? new SystemClock();
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Creates a state store in the default coppice data directory: {LocalAppData}/coppice on Windows,
    /// ~/.local/share/coppice on Linux, ~/Library/Application Support/coppice on macOS.
    /// </summary>
    public FileSystemStateStore(IFileSystem fs, IEnvironment env, IClock? clock = null)
        : this(GetDefaultRoot(env), clock)
    {
    }

    private static string GetDefaultRoot(IEnvironment env) => StateRoot.Default(env);

    /// <summary>
    /// Writes via a temp file and a rename, so a kill mid-write leaves the previous document or the new
    /// one, never half of either. Apply writes quarantine entries and plans while it mutates the disk, and a
    /// torn index entry is a quarantined item <c>restore</c> cannot find.
    /// </summary>
    public Task<string> WriteAsync(string key, string contentType, string json) =>
        WriteAsync(key, contentType, json, CancellationToken.None);

    public async Task<string> WriteAsync(string key, string contentType, string json, CancellationToken cancellationToken)
    {
        string path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temp = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return key;
    }

    public Task<StateDocument?> ReadAsync(string key) => ReadAsync(key, CancellationToken.None);

    public async Task<StateDocument?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        return new StateDocument
        {
            Key = key,
            ContentType = "application/json",
            Json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
            UpdatedAtUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
        };
    }

    public IReadOnlyList<string> ListKeys(string prefix)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(TempSuffix, StringComparison.Ordinal))
            .Select(p => KeyFor(p))
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)];
    }

    public Task DeleteAsync(string key) => DeleteAsync(key, CancellationToken.None);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        string path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string key) => StateRoot.Resolve(_root, key);

    private string KeyFor(string fullPath) =>
        Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private const string TempSuffix = ".coppice-tmp";
}

/// <summary>Where state lives, and the guard that keeps every key inside it.</summary>
internal static class StateRoot
{
    /// <summary>{LocalAppData}/coppice on Windows; $XDG_DATA_HOME (or ~/.local/share)/coppice elsewhere.</summary>
    public static string Default(IEnvironment env)
    {
        if (env.OS == OperatingSystemKind.Windows)
        {
            string? local = env.GetVariable("LOCALAPPDATA");
            return Path.Combine(local ?? Path.GetTempPath(), "coppice");
        }

        string? home = env.HomeDirectory;
        string xdg = env.GetVariable("XDG_DATA_HOME") ?? Path.Combine(home ?? Path.GetTempPath(), ".local", "share");
        return Path.Combine(xdg, "coppice");
    }

    public static string Resolve(string root, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"State key '{key}' escapes the state root.", nameof(key));
        }

        return full;
    }
}

/// <summary>
/// The durable append log under the state root (T-034 journal, T-035 audit). Each line is flushed to disk
/// before the call returns: a write-ahead record that is still in a buffer when the process dies was never
/// written.
/// <para>
/// Segments roll at a size cap: when the current file would exceed the cap, it is renamed aside and appends
/// continue in a fresh one. A roll NEVER discards — the cap bounds a segment, not the log — and
/// <see cref="ReadAllTextAsync"/> returns every segment oldest-first, so a hash chain laid down across a
/// roll still verifies end to end (T-035: "rotates safely without losing entries").
/// </para>
/// </summary>
public sealed class FileAppendLog : IAppendLog
{
    /// <summary>Segment cap. Large enough that years of normal use do not roll; small enough to test.</summary>
    public const long DefaultSegmentBytes = 16 * 1024 * 1024;

    private readonly string _root;
    private readonly long _maxSegmentBytes;

    public FileAppendLog(string root)
        : this(root, DefaultSegmentBytes)
    {
    }

    public FileAppendLog(IEnvironment env)
        : this(StateRoot.Default(env), DefaultSegmentBytes)
    {
    }

    public FileAppendLog(string root, long maxSegmentBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _maxSegmentBytes = maxSegmentBytes;
    }

    public async Task AppendLineAsync(string key, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string path = StateRoot.Resolve(_root, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (_maxSegmentBytes > 0 && File.Exists(path))
        {
            long incoming = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            if (new FileInfo(path).Length + incoming > _maxSegmentBytes)
            {
                Roll(path);
            }
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public async Task<string?> ReadAllTextAsync(string key)
    {
        string path = StateRoot.Resolve(_root, key);

        // Rolled segments first, oldest to newest, then the current file: the order the lines were written
        // in. A log that never rolled reads exactly as it did before rotation existed.
        string? combined = null;
        foreach (string segment in SegmentsInOrder(path))
        {
            await using var stream = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string text = await reader.ReadToEndAsync().ConfigureAwait(false);
            combined = combined is null ? text : combined + text;
        }

        return combined;
    }

    private IEnumerable<string> SegmentsInOrder(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);

        var rolled = new List<(int Order, string Path)>();
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.GetFiles(directory, name + ".*"))
            {
                string suffix = file[(file.LastIndexOf('.') + 1)..];
                if (int.TryParse(suffix, out int order))
                {
                    rolled.Add((order, file));
                }
            }
        }

        foreach ((_, string file) in rolled.OrderBy(segment => segment.Order))
        {
            yield return file;
        }

        if (File.Exists(path))
        {
            yield return path;
        }
    }

    /// <summary>Renames the current segment aside; the next append starts a fresh file. Nothing is discarded.</summary>
    private void Roll(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        int next = 1;

        foreach (string file in Directory.GetFiles(directory, name + ".*"))
        {
            string suffix = file[(file.LastIndexOf('.') + 1)..];
            if (int.TryParse(suffix, out int order) && order >= next)
            {
                next = order + 1;
            }
        }

        File.Move(path, $"{path}.{next:0000}");
    }
}

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
    public async Task<string> WriteAsync(string key, string contentType, string json, CancellationToken cancellationToken = default)
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

    public async Task<StateDocument?> ReadAsync(string key, CancellationToken cancellationToken = default)
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

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
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
/// The durable append log under the state root (T-034 journal). Each line is flushed to disk before the
/// call returns: a write-ahead record that is still in a buffer when the process dies was never written.
/// </summary>
public sealed class FileAppendLog : IAppendLog
{
    private readonly string _root;

    public FileAppendLog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    public FileAppendLog(IEnvironment env)
        : this(StateRoot.Default(env))
    {
    }

    public async Task AppendLineAsync(string key, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string path = StateRoot.Resolve(_root, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public async Task<string?> ReadAllTextAsync(string key)
    {
        string path = StateRoot.Resolve(_root, key);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}

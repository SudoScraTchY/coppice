using System.Runtime.InteropServices;
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

    public async Task<string> WriteAsync(string key, string contentType, string json, CancellationToken cancellationToken = default)
    {
        string path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
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

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"State key '{key}' escapes the state root.", nameof(key));
        }

        return full;
    }

    private string KeyFor(string fullPath) =>
        Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}

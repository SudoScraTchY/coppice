using Coppice.Core.Config;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// Where 10-formats says the user config lives. Kept in Application so the CLI, the TUI and the GUI
/// cannot disagree about the file. The CLI keeps a thin wrapper for its existing namespace.
/// </summary>
public static class UserConfigPath
{
    public static string For(IEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);

        string home = env.HomeDirectory ?? string.Empty;

        return env.OS switch
        {
            OperatingSystemKind.Windows => Join(
                env.OS, env.GetFolderPath("ApplicationData") ?? home, "coppice", "config.toml"),
            OperatingSystemKind.MacOS => Join(
                env.OS, home, "Library", "Application Support", "coppice", "config.toml"),
            _ => Join(env.OS, home, ".config", "coppice", "config.toml"),
        };
    }

    /// <summary>
    /// The opt-in user-manifest directory (FR-23). A configured relative path is relative to the config
    /// file's directory; an absolute path is used as written. The default is <c>&lt;config-dir&gt;/ecosystems</c>.
    /// </summary>
    public static string EcosystemsDirectory(IEnvironment env, CoppiceConfig config)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(config);

        string configDirectory = DirectoryOf(For(env));
        string? configured = config.Ecosystems.Directory;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return Join(env.OS, configDirectory, "ecosystems");
        }

        if (IsRooted(configured, env.OS))
        {
            return configured;
        }

        char separator = env.OS == OperatingSystemKind.Windows ? '\\' : '/';
        return Join(env.OS, configDirectory, configured.Replace('/', separator).Replace('\\', separator));
    }

    private static string DirectoryOf(string path)
    {
        int index = path.LastIndexOfAny(['\\', '/']);
        return index > 0 ? path[..index] : path;
    }

    private static bool IsRooted(string path, OperatingSystemKind os)
    {
        if (path.Length == 0)
        {
            return false;
        }

        if (path[0] is '/' or '\\')
        {
            return true;
        }

        return os == OperatingSystemKind.Windows
            && path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/';
    }

    // Path.Combine uses the HOST separator, which is wrong when IEnvironment describes another OS
    // (the fake machines in tests do exactly that, and a future remote scan could too).
    private static string Join(OperatingSystemKind os, params string[] parts) =>
        string.Join(os == OperatingSystemKind.Windows ? '\\' : '/', parts);
}

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

    // Path.Combine uses the HOST separator, which is wrong when IEnvironment describes another OS
    // (the fake machines in tests do exactly that, and a future remote scan could too).
    private static string Join(OperatingSystemKind os, params string[] parts) =>
        string.Join(os == OperatingSystemKind.Windows ? '\\' : '/', parts);
}

using Coppice.Ports;

namespace Coppice.Cli;

/// <summary>
/// Where 10-formats says the user config lives. Kept in the CLI because composing it uses the target
/// OS separator, and shared by the commands and the TUI so they cannot disagree about the file.
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

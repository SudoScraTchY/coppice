using System.Text.Json;
using Coppice.Ports;

namespace Coppice.Adapters;

/// <summary>
/// The shell's preferences as one JSON file under <c>{LocalApplicationData}/coppice/gui/preferences.json</c>.
/// <para>
/// This is the only place the GUI's preference file touches disk. The document keeps the shape the
/// in-GUI implementation used — <c>ProjectRoots</c>, <c>HelpSeen</c>, <c>Theme</c> — so an existing
/// preferences file keeps loading. A damaged or read-only profile is a convenience failure: load
/// returns defaults and save drops the write.
/// </para>
/// </summary>
public sealed class JsonFileShellPreferencesStore : IShellPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonFileShellPreferencesStore(IEnvironment environment)
        : this(DefaultPath(environment))
    {
    }

    public JsonFileShellPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public ShellPreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ShellPreferences();
            }

            ShellPreferences? stored = JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(_path));
            if (stored is null)
            {
                return new ShellPreferences();
            }

            // JSON can carry an explicit null even though the property is non-nullable in C#.
            if (stored.ProjectRoots is null)
            {
                stored.ProjectRoots = [];
            }

            if (string.IsNullOrWhiteSpace(stored.Theme))
            {
                stored.Theme = "System";
            }

            return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ShellPreferences();
        }
    }

    public void Save(ShellPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    new ShellPreferences
                    {
                        ProjectRoots = [.. preferences.ProjectRoots],
                        HelpSeen = preferences.HelpSeen,
                        Theme = preferences.Theme,
                    },
                    Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience. A read-only profile must not break the app.
        }
    }

    private static string DefaultPath(IEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string baseDirectory = environment.GetFolderPath("LocalApplicationData")
            ?? environment.HomeDirectory
            ?? Path.GetTempPath();

        return Path.Combine(baseDirectory, "coppice", "gui", "preferences.json");
    }
}

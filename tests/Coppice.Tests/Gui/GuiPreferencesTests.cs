using Coppice.Adapters;
using Coppice.Gui.Services;
using Xunit;

namespace Coppice.Tests.Gui;

/// <summary>
/// The preferences port keeps the old file's JSON shape and its forgiving defaults. A missing, damaged
/// or read-only file is a convenience failure and must never stop the shell from starting.
/// </summary>
public sealed class GuiPreferencesTests
{
    [Fact]
    public void Preferences_round_trip_through_the_json_store_with_the_existing_shape()
    {
        string directory = TempDirectory();
        string path = Path.Combine(directory, "preferences.json");

        try
        {
            var store = new JsonFileShellPreferencesStore(path);

            GuiPreferences loaded = GuiPreferences.Load(store);
            Assert.Empty(loaded.ProjectRoots);
            Assert.False(loaded.HelpSeen);
            Assert.Equal("System", loaded.Theme);

            loaded.ProjectRoots = ["/home/dev/code", " "];
            loaded.HelpSeen = true;
            loaded.Theme = "Dark";
            loaded.Save();

            string json = File.ReadAllText(path);
            Assert.Contains("\"ProjectRoots\"", json, StringComparison.Ordinal);
            Assert.Contains("\"HelpSeen\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Theme\"", json, StringComparison.Ordinal);

            GuiPreferences reloaded = GuiPreferences.Load(store);
            Assert.Equal(new[] { "/home/dev/code" }, reloaded.ProjectRoots);
            Assert.True(reloaded.HelpSeen);
            Assert.Equal("Dark", reloaded.Theme);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_damaged_file_and_an_unknown_theme_fall_back_to_safe_defaults()
    {
        string directory = TempDirectory();
        string path = Path.Combine(directory, "preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ this is not json");

            var store = new JsonFileShellPreferencesStore(path);
            GuiPreferences damaged = GuiPreferences.Load(store);

            Assert.Empty(damaged.ProjectRoots);
            Assert.False(damaged.HelpSeen);
            Assert.Equal("System", damaged.Theme);

            // Valid JSON can still carry a null root list and a theme the shell does not know.
            File.WriteAllText(path, """{"ProjectRoots":null,"HelpSeen":true,"Theme":"Purple"}""");

            GuiPreferences unknownTheme = GuiPreferences.Load(store);
            Assert.Empty(unknownTheme.ProjectRoots);
            Assert.True(unknownTheme.HelpSeen);
            Assert.Equal("System", unknownTheme.Theme);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "coppice-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

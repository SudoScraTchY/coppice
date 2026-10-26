using Coppice.Ports;

namespace Coppice.Gui.Services;

/// <summary>
/// Small desktop-shell preferences: project roots the user typed, whether the walkthrough was seen,
/// and the theme choice. This is GUI state only; nothing here affects a scan, plan or mutation.
/// <para>
/// The file lives behind <see cref="IShellPreferencesStore"/>. The view model keeps its synchronous
/// load/save contract, and the tolerant defaults stay here so a test can drive them without a disk.
/// </para>
/// </summary>
public sealed class GuiPreferences
{
    private readonly IShellPreferencesStore _store;

    private GuiPreferences(IShellPreferencesStore store) => _store = store;

    public List<string> ProjectRoots { get; set; } = [];

    public bool HelpSeen { get; set; }

    public string Theme { get; set; } = "System";

    public static GuiPreferences Load(IShellPreferencesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        ShellPreferences stored = store.Load();

        return new GuiPreferences(store)
        {
            ProjectRoots = [.. stored.ProjectRoots.Where(root => !string.IsNullOrWhiteSpace(root))],
            HelpSeen = stored.HelpSeen,
            Theme = stored.Theme is "Light" or "Dark" ? stored.Theme : "System",
        };
    }

    public void Save() => _store.Save(new ShellPreferences
    {
        ProjectRoots = [.. ProjectRoots],
        HelpSeen = HelpSeen,
        Theme = Theme,
    });
}

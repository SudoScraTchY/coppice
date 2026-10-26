namespace Coppice.Ports;

/// <summary>
/// The desktop shell's local preferences: the project roots the user typed, whether the walkthrough
/// was seen, and the theme choice. This is view state only; nothing here affects a scan, plan or mutation.
/// </summary>
public sealed class ShellPreferences
{
    public List<string> ProjectRoots { get; set; } = [];

    public bool HelpSeen { get; set; }

    public string Theme { get; set; } = "System";
}

/// <summary>
/// Synchronous read/write of the desktop shell's preferences. The window deliberately loads them before
/// its first frame and saves them while it closes, so this port has no async shape.
/// <para>
/// Implementations must be tolerant: a damaged, missing or read-only file returns defaults and drops
/// the write. Preferences are a convenience, never a reason to refuse to start or to crash on exit.
/// </para>
/// </summary>
public interface IShellPreferencesStore
{
    ShellPreferences Load();

    void Save(ShellPreferences preferences);
}

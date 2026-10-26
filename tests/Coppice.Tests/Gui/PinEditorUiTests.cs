using Coppice.Application;
using Coppice.Core.Quarantine;
using Coppice.Gui.Services;
using Coppice.Gui.ViewModels;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Gui;

/// <summary>
/// T-040: every roots row carries provenance, and the pin editor validates through the real resolution
/// path before it writes. A denylisted path leaves the writer untouched; a valid pin is written.
/// </summary>
public sealed class PinEditorUiTests
{
    private const string ConfigPath = "/home/dev/.config/coppice/config.toml";

    [Fact]
    public async Task Every_root_has_provenance_and_a_denylisted_pin_is_visible_and_not_saved()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile(ConfigPath, "version = 1\n");
        fs.AddDirectory("/home/dev/pinned/newtonsoft.json/1.0.0");
        fs.AddDirectory("/etc");

        var env = new FakeEnvironment(OperatingSystemKind.Linux);
        var runner = new FakeProcessRunner();
        var state = new FakeStateStore();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var log = new FakeAppendLog();
        var registry = EcosystemRegistry.CreateDefault(fs);
        var writer = new MemoryConfigWriter();
        var pinEditor = new PinEditorService(fs, runner, env, writer, ConfigPath);
        QuarantineServices quarantine = ApplyWiring.CreateQuarantine(fs, env, state, log, clock);

        using var services = new GuiServices(
            registry,
            new DoctorService(fs, runner, env, env.OS, registry),
            new ScanService(fs, runner, env, state, clock, registry),
            new SnapshotLibrary(state, clock),
            new PlanApplyService(fs, runner, env, state, log, clock, registry),
            new QuarantineLibrary(fs, quarantine.Index, quarantine.Restore),
            pinEditor);

        var vm = new MainWindowViewModel(services, GuiPreferences.Load(new FakeShellPreferencesStore()));
        await vm.RefreshRootsCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Roots);
        Assert.All(vm.Roots, row => Assert.False(string.IsNullOrWhiteSpace(row.Provenance)));

        vm.PinLocationId = "nuget-packages";
        vm.PinPath = "/etc";
        vm.PreviewPinCommand.Execute(null);
        Assert.Contains("Denied", vm.PinStatus, StringComparison.OrdinalIgnoreCase);

        await vm.SavePinCommand.ExecuteAsync(null);
        Assert.Null(writer.Content);

        vm.PinPath = "/home/dev/pinned";
        await vm.SavePinCommand.ExecuteAsync(null);
        Assert.NotNull(writer.Content);
        Assert.Contains("\"nuget-packages\" = \"/home/dev/pinned\"", writer.Content, StringComparison.Ordinal);
    }

    private sealed class MemoryConfigWriter : IConfigWriter
    {
        public string? Content { get; private set; }

        public void Write(string path, string content) => Content = content;
    }
}

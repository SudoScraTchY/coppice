using Coppice.Application;
using Coppice.Core.Config;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Config;

/// <summary>
/// T-040: a pin is previewed through the same resolution/denylist/fingerprint path a scan uses, and
/// only a passing preview is written. The write edits one [pins] entry and preserves everything else.
/// </summary>
public sealed class PinEditorServiceTests
{
    private const string ConfigPath = "/home/dev/.config/coppice/config.toml";

    private const string Config = """
        version = 1

        [policy]
        preset = "conservative"

        [pins]
        "nuget-packages" = "/home/dev/old-pin"
        """;

    [Fact]
    public void A_denylisted_pin_is_rejected_with_a_visible_reason()
    {
        (PinEditorService editor, _) = Editor();

        PinPreview preview = editor.Preview("nuget-packages", "/etc");

        Assert.False(preview.Valid);
        Assert.Equal("Denied", preview.Validity);
        Assert.NotEmpty(preview.Reason);
    }

    [Fact]
    public void A_valid_pin_previews_as_the_pin_source()
    {
        (PinEditorService editor, _) = Editor();

        PinPreview preview = editor.Preview("nuget-packages", "/home/dev/pinned");

        Assert.True(preview.Valid);
        Assert.Equal("Ok", preview.Validity);
        Assert.Contains("Pin", preview.Provenance, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_preview_writes_nothing()
    {
        (PinEditorService editor, MemoryConfigWriter writer) = Editor();

        PinSaveResult result = editor.Save("nuget-packages", "/etc");

        Assert.False(result.Saved);
        Assert.Null(writer.Content);
    }

    [Fact]
    public void A_valid_pin_is_written_and_unrelated_config_is_preserved()
    {
        (PinEditorService editor, MemoryConfigWriter writer) = Editor();

        PinSaveResult result = editor.Save("nuget-packages", "/home/dev/pinned");

        Assert.True(result.Saved);
        Assert.NotNull(writer.Content);
        Assert.Contains("[policy]", writer.Content, StringComparison.Ordinal);
        Assert.Contains("preset = \"conservative\"", writer.Content, StringComparison.Ordinal);
        Assert.Contains("\"nuget-packages\" = \"/home/dev/pinned\"", writer.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/dev/old-pin", writer.Content, StringComparison.Ordinal);
    }

    private static (PinEditorService Editor, MemoryConfigWriter Writer) Editor()
    {
        var fs = new FakeFileSystem(OperatingSystemKind.Linux);
        fs.AddFile(ConfigPath, Config);
        fs.AddDirectory("/home/dev/pinned/newtonsoft.json/1.0.0");
        fs.AddDirectory("/etc");

        var writer = new MemoryConfigWriter();
        return (
            new PinEditorService(
                fs,
                new FakeProcessRunner(),
                new FakeEnvironment(OperatingSystemKind.Linux),
                writer,
                ConfigPath),
            writer);
    }

    private sealed class MemoryConfigWriter : IConfigWriter
    {
        public string? Content { get; private set; }

        public void Write(string path, string content) => Content = content;
    }
}

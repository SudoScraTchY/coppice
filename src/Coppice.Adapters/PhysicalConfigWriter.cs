using Coppice.Ports;

namespace Coppice.Adapters;

/// <summary>The production config writer. The only place a pin edit touches the real disk.</summary>
public sealed class PhysicalConfigWriter : IConfigWriter
{
    public void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }
}

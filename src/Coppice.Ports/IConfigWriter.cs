namespace Coppice.Ports;

/// <summary>
/// A narrow write capability for the user's config file. Separate from <see cref="IFileSystem"/> on
/// purpose: the filesystem port is read-only so the Gateway stays the sole mutator of cache content.
/// </summary>
public interface IConfigWriter
{
    void Write(string path, string content);
}

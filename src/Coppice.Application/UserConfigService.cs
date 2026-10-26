using Coppice.Core.Config;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// Loads the user config from the platform path, or returns working defaults when it is absent.
/// <para>
/// The path is supplied by the front-end because composing it uses <c>System.IO.Path</c>, which the
/// application layer does not reach for directly. A missing file is not an error: refusing to run on
/// first launch would make the tool unusable. A file that exists but cannot be read IS an error,
/// because guessing defaults after the user wrote a policy would silently change what gets deleted.
/// </para>
/// </summary>
public static class UserConfigService
{
    public static ConfigLoadResult Load(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!fileSystem.FileExists(path))
        {
            return ConfigLoadResult.Success(CoppiceConfig.Defaults);
        }

        try
        {
            return ConfigLoader.Load(fileSystem.ReadSmallText(path), path);
        }
        catch (FileNotFoundException)
        {
            // Exists() won the race, then the file vanished. Treating that as "no config" is safer
            // than failing a read-only command because another process cleaned up the file.
            return ConfigLoadResult.Success(CoppiceConfig.Defaults);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ConfigLoadResult.Fail(
                ConfigFailure.Unreadable,
                $"{path} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The policy a config describes, with exclusions merged from both config lists. The retention
    /// engine matches an exclusion against an item path or its exact name, so a single list is correct;
    /// keeping the file's two sections separate is a documentation convenience, not a semantic split.
    /// </summary>
    public static Policy PolicyOf(CoppiceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string[] exclusions = [.. config.ExcludedPaths, .. config.ExcludedNames];
        return config.Policy.ToPolicy(exclusions);
    }
}

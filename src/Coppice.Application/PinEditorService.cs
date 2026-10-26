using System.Text;
using Coppice.Core.Config;
using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>One pin validation result, before anything is written.</summary>
public sealed record PinPreview(
    string LocationId,
    string Path,
    bool Valid,
    string Validity,
    string Reason,
    string Provenance,
    bool NeedsElevation);

/// <summary>One pin save result. A failed save writes nothing.</summary>
public sealed record PinSaveResult(bool Saved, string Message, PinPreview Preview);

/// <summary>
/// Validates a configuration pin through the same doctor/registry path a scan uses, then writes it to
/// <c>config.toml</c> only after it has passed the denylist and fingerprint gates (T-040, FR-22).
/// </summary>
public sealed class PinEditorService
{
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly IEnvironment _env;
    private readonly IConfigWriter _writer;
    private readonly string _configPath;

    public PinEditorService(
        IFileSystem fs,
        IProcessRunner runner,
        IEnvironment env,
        IConfigWriter writer,
        string configPath)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        _fs = fs;
        _runner = runner;
        _env = env;
        _writer = writer;
        _configPath = configPath;
    }

    public PinPreview Preview(string locationId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ConfigLoadResult loaded = UserConfigService.Load(_fs, _configPath);
        if (!loaded.IsSuccess)
        {
            return new PinPreview(
                locationId,
                path,
                false,
                "InvalidConfig",
                loaded.Message ?? "The config file could not be loaded.",
                string.Empty,
                false);
        }

        CoppiceConfig config = loaded.Config!;
        var pins = new Dictionary<string, string>(config.Pins, StringComparer.Ordinal)
        {
            [locationId] = path,
        };

        EcosystemRegistry registry = EcosystemRegistry.CreateForConfig(_fs, _env, config);
        var doctor = new DoctorService(_fs, _runner, _env, _env.OS, registry, pins);
        RootReport? root = doctor.Inspect().Roots
            .FirstOrDefault(candidate => string.Equals(candidate.LocationId, locationId, StringComparison.Ordinal));

        if (root is null)
        {
            return new PinPreview(locationId, path, false, "UnknownLocation", "No such location id.", string.Empty, false);
        }

        string provenance = root.Root.Via.ToString()
            + (string.IsNullOrWhiteSpace(root.Root.ViaDetail) ? string.Empty : $" · {root.Root.ViaDetail}");

        bool needsElevation = root.Root.Validity == RootValidity.NeedsElevation;
        string reason = root.Problem
            ?? (needsElevation
                ? "This location needs elevation. Coppice never elevates; it is report-only."
                : "Valid.");

        return new PinPreview(
            locationId,
            path,
            root.Root.Validity == RootValidity.Ok,
            root.Root.Validity.ToString(),
            reason,
            provenance,
            needsElevation);
    }

    public PinSaveResult Save(string locationId, string path)
    {
        PinPreview preview = Preview(locationId, path);
        if (!preview.Valid)
        {
            return new PinSaveResult(false, preview.Reason, preview);
        }

        string existing = _fs.FileExists(_configPath) ? _fs.ReadSmallText(_configPath) : string.Empty;
        string updated = ConfigText.UpdatePin(existing, locationId, path);

        try
        {
            _writer.Write(_configPath, updated);
            return new PinSaveResult(true, $"Pin saved for '{locationId}'.", preview);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PinSaveResult(false, $"The pin could not be written: {ex.Message}", preview);
        }
    }
}

/// <summary>
/// The smallest TOML edit that preserves every unrelated line: replace (or add) one key in
/// <c>[pins]</c>. Deliberately not a general TOML serializer.
/// </summary>
internal static class ConfigText
{
    public static string UpdatePin(string text, string locationId, string path)
    {
        List<string> lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        string key = Quote(locationId);
        string entry = $"{key} = {Quote(path)}";

        int section = lines.FindIndex(line => string.Equals(line.Trim(), "[pins]", StringComparison.Ordinal));

        if (section < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[pins]");
            lines.Add(entry);
            lines.Add(string.Empty);
            return string.Join('\n', lines);
        }

        int end = lines.Count;
        for (int i = section + 1; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        int existingKey = -1;
        for (int i = section + 1; i < end; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key, StringComparison.Ordinal)
                && trimmed[key.Length..].TrimStart().StartsWith("=", StringComparison.Ordinal))
            {
                existingKey = i;
                break;
            }
        }

        if (existingKey >= 0)
        {
            lines[existingKey] = entry;
        }
        else
        {
            lines.Insert(end, entry);
        }

        return string.Join('\n', lines);
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        builder.Append('"');
        return builder.ToString();
    }
}

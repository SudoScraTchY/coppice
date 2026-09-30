using Coppice.Ports;

namespace Coppice.Plugins.Net;

/// <summary>
/// Expands a profile default's placeholders against a real environment.
/// <para>
/// PURE and side-effect free: it takes an <see cref="IEnvironment"/> and returns a string. A default
/// table that read the real environment at construction time would bake one machine's home into
/// the data, and would be untestable.
/// </para>
/// <para>
/// Placeholders: <c>~</c> (home), <c>{user}</c> (OS user name), and <c>%VAR%</c> / <c>$VAR</c>.
/// An unresolvable placeholder yields null rather than a literal <c>~</c>-prefixed path, because a
/// path that silently fails to expand would fail the fingerprint for reasons that look like the
/// user's machine being broken.
/// </para>
/// </summary>
public static class NetDefaults
{
    /// <summary>Expands a default path, or returns null when a placeholder cannot be resolved.</summary>
    public static string? Expand(string? defaultPath, IEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(defaultPath))
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(environment);

        string result = defaultPath;
        bool resolvable = true;

        // ~ and ~/... (and ~\... on Windows)
        if (result == "~" || result.StartsWith("~/", StringComparison.Ordinal) || result.StartsWith(@"~\", StringComparison.Ordinal))
        {
            string? home = environment.HomeDirectory;
            if (string.IsNullOrEmpty(home))
            {
                resolvable = false;
            }
            else
            {
                result = result == "~" ? home : home + result[1..];
            }
        }

        // {user} — the OS user name, which is what makes the Linux NuGet temp default expressible.
        if (result.Contains("{user}", StringComparison.Ordinal))
        {
            string? user = environment.GetVariable("USERNAME")
                ?? environment.GetVariable("USER")
                ?? environment.GetVariable("LOGNAME");

            if (string.IsNullOrEmpty(user))
            {
                resolvable = false;
            }
            else
            {
                result = result.Replace("{user}", user, StringComparison.Ordinal);
            }
        }

        // %VAR% (Windows convention) and $VAR / ${VAR} (POSIX).
        result = ExpandVariables(result, environment);

        return resolvable ? result : null;
    }

    private static string ExpandVariables(string path, IEnvironment environment)
    {
        string result = path;

        while (true)
        {
            int start = result.IndexOf('%');
            if (start < 0)
            {
                break;
            }

            int end = result.IndexOf('%', start + 1);
            if (end < 0)
            {
                break;
            }

            string name = result[(start + 1)..end];
            string? value = environment.GetVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                // Leave it as-is rather than substituting an empty string, so the fingerprint
                // failure is legible as "%TEMP% was not set" instead of a root-level path.
                break;
            }

            result = string.Concat(result.AsSpan(0, start), value, result.AsSpan(end + 1));
        }

        // $VAR and ${VAR}, the POSIX convention. Windows paths rarely use it, but a config-supplied
        // default can, and an unexpanded "$" is worse than an unexpanded "%".
        while (true)
        {
            int start = result.IndexOf('$', StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            bool braced = start + 1 < result.Length && result[start + 1] == '{';
            int nameStart = braced ? start + 2 : start + 1;

            int end = braced
                ? result.IndexOf('}', nameStart)
                : IndexOfPathTerminator(result, nameStart);

            if (end <= nameStart)
            {
                break;
            }

            // For the braced form `end` points AT the '}', so the tail resumes one past it. Getting
            // this wrong leaves the brace in the path — "/xdg" + "NuGet/..." loses the separator too.
            int tailStart = braced ? end + 1 : end;

            string? value = environment.GetVariable(result[nameStart..end]);
            if (string.IsNullOrEmpty(value))
            {
                // Leave the placeholder visible so a failure names the variable that was unset.
                break;
            }

            result = string.Concat(result.AsSpan(0, start), value, result.AsSpan(tailStart));
        }

        return result;
    }
    /// <summary>Index of the character that ends an unbraced $VAR name, or -1.</summary>
    private static int IndexOfPathTerminator(string path, int from)
    {
        for (int i = from; i < path.Length; i++)
        {
            if (path[i] is '/' or '\\')
            {
                return i;
            }
        }

        return -1;
    }
}

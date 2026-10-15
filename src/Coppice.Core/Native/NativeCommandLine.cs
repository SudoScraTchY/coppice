using System.Text;

namespace Coppice.Core.Native;

/// <summary>
/// Splits a native removal command line into a file name and an argument list (T-033).
/// <para>
/// Manifests and ecosystem profiles carry the command as ONE string (<c>go clean -modcache</c>,
/// <c>dotnet tool uninstall -g &lt;id&gt;</c>), while the process port takes a file name plus an
/// argument list — so the split happens here, once, where it can be tested. The grammar is
/// deliberately small: whitespace separates tokens, and double quotes group a token that contains
/// whitespace (a tool installed under <c>Program Files</c>). Single quotes are NOT special: these
/// lines come from manifests written for this tool, not from a shell, and a second quoting grammar
/// would only make "what exactly will run?" harder to answer.
/// </para>
/// <para>
/// The first token is the file name; the rest are arguments. An empty or all-whitespace line yields an
/// empty file name, which the gateway treats as a plan defect rather than a command to run.
/// </para>
/// </summary>
public static class NativeCommandLine
{
    public static (string FileName, IReadOnlyList<string> Arguments) Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        List<string> parts = [];
        var current = new StringBuilder();
        bool inQuotes = false;
        bool building = false;

        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                building = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (building)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    building = false;
                }

                continue;
            }

            current.Append(c);
            building = true;
        }

        if (building)
        {
            parts.Add(current.ToString());
        }

        return parts.Count == 0
            ? (string.Empty, [])
            : (parts[0], [.. parts.Skip(1)]);
    }
}

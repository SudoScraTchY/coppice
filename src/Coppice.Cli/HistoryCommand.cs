using System.Globalization;
using Coppice.Application;
using Coppice.Ports;

namespace Coppice.Cli;

/// <summary>The flags of the <c>history</c> verb. Unknown flags are rejected, as everywhere else (07).</summary>
public sealed record HistoryFlags(int? Limit, bool Json)
{
    public static HistoryFlags Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int? limit = null;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        throw new ArgumentException("--limit needs a whole number");
                    }

                    string raw = args[++i];
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
                    {
                        throw new ArgumentException("--limit needs a positive whole number");
                    }

                    limit = parsed;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--ci":
                    // FR-21: CI mode is machine-readable, and this verb never prompts.
                    json = true;
                    break;
                case "--no-color":
                    // Accepted and ignored: this renderer never emits color.
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        return new HistoryFlags(limit, json);
    }
}

/// <summary>
/// <c>coppice history [--limit N] [--json]</c> (T-041, nice-to-have #4) — how the caches changed.
/// <para>
/// Every scan already stores a snapshot, but until now there was no way to see two of them together.
/// This verb loads the newest N snapshots (oldest first), prints each scan's totals and the delta from
/// the one before it, and repeats that per ecosystem. It builds its answer from
/// <see cref="SnapshotLibrary.LoadHistoryAsync"/>, the same application query the desktop shell's
/// trend page uses, so the CLI and the GUI cannot disagree about what grew.
/// </para>
/// <para>
/// Read-only: the only store it opens is the user's state directory, and it writes nothing there. A
/// snapshot that cannot be loaded is reported by id and skipped rather than aborting the run.
/// </para>
/// </summary>
public sealed class HistoryCommand
{
    private readonly SnapshotLibrary _snapshots;
    private readonly TextWriter _output;

    public HistoryCommand(IStateStore state, IClock? clock = null, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _snapshots = new SnapshotLibrary(state, clock);
        _output = output ?? Console.Out;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        HistoryFlags flags = HistoryFlags.Parse(args);
        int limit = flags.Limit ?? HistoryReport.DefaultLimit;

        SnapshotHistory history = _snapshots
            .LoadHistoryAsync(limit)
            .GetAwaiter().GetResult();

        _output.WriteLine(flags.Json ? HistoryReport.ToJson(history) : HistoryReport.ToText(history));
        return ExitCodes.Success;
    }
}

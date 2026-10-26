using Coppice.Core.Apply;
using Coppice.Core.Config;
using Coppice.Core.Quarantine;
using Spectre.Console;

namespace Coppice.Cli.Tui;

public sealed record TuiFlags(bool Once, bool Ci, bool NoColor)
{
    /// <summary>Project roots for the TUI's rescan. Empty means everything stays Unknown, as in `scan`.</summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    public static TuiFlags Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool once = false;
        bool ci = false;
        bool noColor = false;
        var projectRoots = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once":
                    once = true;
                    break;
                case "--ci":
                    // FR-21: CI never gets an interactive screen. The dashboard is rendered plain, once.
                    ci = true;
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "--projects":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        throw new ArgumentException("--projects needs a directory path");
                    }

                    projectRoots.Add(args[++i]);
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        return new TuiFlags(once, ci, noColor) { ProjectRoots = projectRoots };
    }
}

/// <summary>
/// <c>coppice tui</c> — the keyboard-driven screens (T-036, FR-20). Dashboard, Roots, Items, Problems,
/// Plan review, Apply progress and Quarantine; Tab order is documented in 07 and pinned by a test.
/// <para>
/// When stdout or stdin is redirected — or under <c>--ci</c>/<c>--once</c> — it renders the Dashboard once
/// as plain text and exits. There is no escape sequence in that path at all (NFR-09).
/// </para>
/// <para>
/// The TUI adds no mutation path. Applying goes through the same <see cref="ApplyWiring"/> as
/// <c>coppice apply</c> (gateway, journal, audit), and restoring goes through <c>QuarantineRestore</c>.
/// </para>
/// </summary>
public sealed class TuiCommand
{
    private readonly ITuiBackend _backend;
    private readonly ITuiTerminal _terminal;
    private readonly Policy? _initialPolicy;

    public TuiCommand(ITuiBackend backend, ITuiTerminal terminal, Policy? initialPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(terminal);

        _backend = backend;
        _terminal = terminal;
        _initialPolicy = initialPolicy;
    }

    /// <summary>The production entry point, wired to the real machine and console.</summary>
    public static int RunSystem(string[] args)
    {
        TuiFlags flags = TuiFlags.Parse(args);

        var fs = new Adapters.PhysicalFileSystem();
        var env = new Adapters.SystemEnvironment();
        var clock = new Adapters.SystemClock();
        var terminal = flags.NoColor ? CreateColorless() : new SystemTuiTerminal();

        ConfigLoadResult loaded = UserConfigService.Load(fs, UserConfigPath.For(env));
        if (!loaded.IsSuccess)
        {
            Console.Error.WriteLine($"coppice: {loaded.Message}");
            foreach (ConfigDiagnostic diagnostic in loaded.Diagnostics)
            {
                Console.Error.WriteLine($"  {diagnostic}");
            }

            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> projectRoots = flags.ProjectRoots.Count > 0
            ? flags.ProjectRoots
            : loaded.Config!.ProjectRoots;

        var backend = new TuiBackend(
            fs,
            new Adapters.SystemProcessRunner(),
            env,
            new Adapters.FileSystemStateStore(fs, env),
            new Adapters.FileAppendLog(env),
            clock,
            projectRoots);

        return new TuiCommand(backend, terminal, UserConfigService.PolicyOf(loaded.Config!))
            .RunAsync(flags)
            .GetAwaiter()
            .GetResult();
    }

    public async Task<int> RunAsync(TuiFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        TuiData data = await _backend.LoadAsync().ConfigureAwait(false);
        var state = new TuiState(data, PlanPreviewService.Preview, _initialPolicy);

        if (!_terminal.IsInteractive || flags.Once || flags.Ci)
        {
            _terminal.WritePlain(TuiPlain.Render(state.Frame()));
            return ExitCodes.Success;
        }

        return await LoopAsync(state).ConfigureAwait(false);
    }

    private async Task<int> LoopAsync(TuiState state)
    {
        while (true)
        {
            _terminal.Render(state.Frame());
            ConsoleKeyInfo key = _terminal.ReadKey();
            TuiIntent intent = state.HandleKey(key);

            switch (intent.Kind)
            {
                case TuiIntentKind.Quit:
                    return ExitCodes.Success;

                case TuiIntentKind.Scan:
                    _terminal.Render(state.Frame());
                    TuiData scanned = await _backend.ScanAsync().ConfigureAwait(false);
                    state.SetData(scanned);
                    state.SetStatus(scanned.Snapshot is null
                        ? "scan finished; no snapshot was written."
                        : $"scan complete: {scanned.Snapshot.SnapshotId}");
                    break;

                case TuiIntentKind.Apply:
                    await RunApplyAsync(state, intent).ConfigureAwait(false);
                    break;

                case TuiIntentKind.Restore:
                    state.SetStatus("restoring… the content is hash-checked before it moves.");
                    _terminal.Render(state.Frame());
                    RestoreResult restored = await _backend.RestoreAsync(intent.EntryId!).ConfigureAwait(false);
                    TuiData reloaded = await _backend.LoadAsync().ConfigureAwait(false);
                    state.SetData(reloaded);
                    state.SetStatus(restored.Restored ? restored.Message : $"restore refused: {restored.Message}");
                    break;
            }
        }
    }

    /// <summary>
    /// Runs one plan with the progress screen live. Keys are only read BETWEEN steps; the one thing a
    /// key can do is set the cancellation flag the runner observes at the next step boundary.
    /// </summary>
    private async Task RunApplyAsync(TuiState state, TuiIntent intent)
    {
        ApplyPreparation prepared = await _backend
            .PrepareApplyAsync(intent.Plan!)
            .ConfigureAwait(false);

        if (prepared.Plan is null || prepared.Error is not null)
        {
            state.SetStatus(prepared.Error ?? "The plan could not be prepared.");
            return;
        }

        state.BeginApply(prepared.Plan.PlanId, prepared.Bound);
        _terminal.Render(state.Frame());

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(state.QueueProgress);
        Task<ApplySummary> running = Task.Run(() =>
            _backend.ApplyAsync(prepared.Plan, prepared.Bound, progress, cancellation.Token));

        while (!running.IsCompleted)
        {
            await Task.Delay(75).ConfigureAwait(false);
            state.DrainProgress();

            if (_terminal.KeyAvailable)
            {
                // The intent is deliberately discarded while in flight: quit would abandon the screen,
                // not the mutation. Escape sets the flag; nothing else acts until the run returns.
                state.HandleKey(_terminal.ReadKey());
                if (state.CancelRequested && !cancellation.IsCancellationRequested)
                {
                    cancellation.Cancel();
                }
            }

            _terminal.Render(state.Frame());
        }

        state.FinishApply(await running.ConfigureAwait(false));
        _terminal.Render(state.Frame());
    }

    private static SystemTuiTerminal CreateColorless()
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Detect,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Detect,
            Out = new AnsiConsoleOutput(Console.Out),
        });

        return new SystemTuiTerminal(console);
    }

    /// <summary>Progress that runs on the reporting thread instead of posting through a context the console lacks.</summary>
    private sealed class SynchronousProgress(Action<ApplyStepProgress> report) : IProgress<ApplyStepProgress>
    {
        public void Report(ApplyStepProgress value) => report(value);
    }
}

using System.Collections.Concurrent;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Retention;
using Coppice.Core.Snapshots;

namespace Coppice.Cli.Tui;

/// <summary>What the TUI loop must DO after a key, as opposed to what the screen shows.</summary>
public enum TuiIntentKind
{
    None,
    Quit,
    Scan,
    Apply,
    Restore,
}

/// <summary>
/// A side effect requested by a key. The state machine never performs one itself, so every screen's key
/// handling is testable without a terminal, a filesystem or a gateway.
/// </summary>
public sealed record TuiIntent(
    TuiIntentKind Kind,
    Plan? Plan = null,
    string? EntryId = null)
{
    public static TuiIntent None { get; } = new(TuiIntentKind.None);

    public static TuiIntent Quit { get; } = new(TuiIntentKind.Quit);

    public static TuiIntent Scan { get; } = new(TuiIntentKind.Scan);

    public static TuiIntent Apply(Plan plan) => new(TuiIntentKind.Apply, plan);

    public static TuiIntent Restore(string entryId) => new(TuiIntentKind.Restore, EntryId: entryId);
}

/// <summary>A health finding the TUI can show without inventing severity rules of its own.</summary>
public sealed record TuiProblem(string Severity, string Source, string Code, string Summary, string? Path, string Detail);

/// <summary>Everything the screens read. Loaded by the backend, never by the state machine.</summary>
public sealed record TuiData(
    DoctorReport Roots,
    Snapshot? Snapshot,
    IReadOnlyList<TuiProblem> Problems,
    IReadOnlyList<QuarantineEntry> Quarantine,
    string? HomeDirectory,
    string ConfigPath);

public enum ItemFilter
{
    All,
    Safe,
    Review,
    Manual,
    Referenced,
    Unreferenced,
    Unknown,
}

public enum ItemSort
{
    Size,
    Name,
    Risk,
}

/// <summary>
/// The whole keyboard surface, as a pure state machine (T-036, FR-20).
/// <para>
/// Tab order is fixed and pinned by a test: Dashboard → Roots → Items → Problems → Plan review →
/// Quarantine → Dashboard. Apply progress is reachable only by pressing <c>a</c> in plan review, and
/// leaving it is explicit, because it is the one screen during which a mutation is in flight.
/// </para>
/// <para>
/// Every screen is built from labeled <see cref="TuiBlock"/>s. That is what makes the screen-reader and
/// piped-output acceptance claims testable: there is no unlabeled text region to miss.
/// </para>
/// </summary>
public sealed class TuiState
{
    public static IReadOnlyList<TuiScreen> TabOrder { get; } =
    [
        TuiScreen.Dashboard,
        TuiScreen.Roots,
        TuiScreen.Items,
        TuiScreen.Problems,
        TuiScreen.PlanReview,
        TuiScreen.Quarantine,
    ];

    private readonly Func<Snapshot, Policy, PlanPreview> _previewFactory;
    private readonly Dictionary<TuiScreen, int> _selections = [];
    private readonly HashSet<string> _included = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ApplyStepProgress> _progressQueue = new();
    private readonly Dictionary<string, ApplyStepReport> _progressDone = new(StringComparer.Ordinal);

    private TuiData _data;
    private Dictionary<string, Item> _itemsById = new(StringComparer.Ordinal);
    private Policy _policy = Policy.Default;
    private PlanPreview _preview = new(
        null,
        "No snapshot.",
        new Dictionary<string, RetentionDecision>(StringComparer.Ordinal),
        PlanPreviewStatus.Failed);
    private string _status = string.Empty;
    private TuiScreen _returnScreen = TuiScreen.Dashboard;
    private ItemFilter _filter = ItemFilter.All;
    private ItemSort _sort = ItemSort.Size;
    private bool _showDetail;
    private bool _confirmApply;
    private bool _confirmRestore;
    private string? _appliedPlanId;

    private IReadOnlyList<BoundStep> _progressSteps = [];
    private string? _runningStepId;
    private ApplySummary? _applySummary;
    private string? _progressPlanId;

    public TuiState(TuiData data, Func<Snapshot, Policy, PlanPreview> previewFactory)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(previewFactory);

        _data = data;
        _previewFactory = previewFactory;
        IndexItems();
        RebuildPreview();
    }

    public TuiScreen Screen { get; private set; } = TuiScreen.Dashboard;

    public Policy Policy => _policy;

    public bool CancelRequested { get; private set; }

    public ApplySummary? ApplySummary => _applySummary;

    /// <summary>The plan id whose journal has been opened by this session, if any.</summary>
    public string? AppliedPlanId => _appliedPlanId;

    public IReadOnlyList<BoundStep> ProgressSteps => _progressSteps;

    /// <summary>Replaces the data after a scan or a reload and rebuilds the preview.</summary>
    public void SetData(TuiData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        IndexItems();
        RebuildPreview();
        _showDetail = false;
        _confirmApply = false;
        ClampSelections();
    }

    public void SetStatus(string status) => _status = status;

    // ---- key handling -----------------------------------------------------------------------------

    public TuiIntent HandleKey(ConsoleKeyInfo key)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
        {
            return Screen == TuiScreen.ApplyProgress && _applySummary is null ? RequestCancel() : TuiIntent.Quit;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            if (Screen == TuiScreen.ApplyProgress)
            {
                return TuiIntent.None;
            }

            Navigate(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
            return TuiIntent.None;
        }

        return Screen switch
        {
            TuiScreen.Dashboard => HandleDashboard(key),
            TuiScreen.Roots => HandleRoots(key),
            TuiScreen.Items => HandleItems(key),
            TuiScreen.Problems => HandleProblems(key),
            TuiScreen.PlanReview => HandlePlanReview(key),
            TuiScreen.ApplyProgress => HandleApplyProgress(key),
            TuiScreen.Quarantine => HandleQuarantine(key),
            _ => TuiIntent.None,
        };
    }

    private TuiIntent HandleDashboard(ConsoleKeyInfo key)
    {
        if (IsQuit(key))
        {
            return TuiIntent.Quit;
        }

        if (IsScan(key))
        {
            return ScanIntent();
        }

        TuiScreen[] drills = [TuiScreen.Roots, TuiScreen.Items, TuiScreen.Problems, TuiScreen.PlanReview, TuiScreen.Quarantine];

        if (Move(key, drills.Length))
        {
            return TuiIntent.None;
        }

        if (key.Key is ConsoleKey.Enter)
        {
            TuiScreen target = drills[SelectionOf(TuiScreen.Dashboard)];
            _returnScreen = TuiScreen.Dashboard;
            Screen = target;
            _showDetail = false;
            _confirmApply = false;
            _confirmRestore = false;
            _status = string.Empty;
            if (target == TuiScreen.PlanReview)
            {
                EnsurePreview();
            }
        }

        return TuiIntent.None;
    }

    private TuiIntent HandleRoots(ConsoleKeyInfo key)
    {
        if (IsQuit(key))
        {
            return TuiIntent.Quit;
        }

        if (IsScan(key))
        {
            return ScanIntent();
        }

        if (Move(key, _data.Roots.Roots.Count))
        {
            _showDetail = false;
            return TuiIntent.None;
        }

        if (key.Key is ConsoleKey.Enter)
        {
            _showDetail = !_showDetail;
            return TuiIntent.None;
        }

        if (IsChar(key, 'p'))
        {
            RootReport? root = SelectedRoot;
            _status = root is null
                ? "No root selected."
                : $"pin: this build does not read config-file pins yet. The line for a future config is "
                    + $"`{root.LocationId} = \"{root.Root.RealPath}\"` under [pins] in {_data.ConfigPath}.";
        }

        return TuiIntent.None;
    }

    private TuiIntent HandleItems(ConsoleKeyInfo key)
    {
        if (IsQuit(key))
        {
            return TuiIntent.Quit;
        }

        if (IsScan(key))
        {
            return ScanIntent();
        }

        IReadOnlyList<Item> rows = FilteredItems();

        if (Move(key, rows.Count))
        {
            _showDetail = false;
            return TuiIntent.None;
        }

        if (key.Key is ConsoleKey.Enter)
        {
            _showDetail = !_showDetail;
            return TuiIntent.None;
        }

        if (IsChar(key, 'f'))
        {
            _filter = Next(_filter);
            SetSelection(TuiScreen.Items, 0);
            _showDetail = false;
            _status = $"filter: {_filter}";
        }
        else if (IsChar(key, 'o'))
        {
            _sort = Next(_sort);
            SetSelection(TuiScreen.Items, 0);
            _showDetail = false;
            _status = $"sort: {_sort}";
        }

        return TuiIntent.None;
    }

    private TuiIntent HandleProblems(ConsoleKeyInfo key)
    {
        if (IsQuit(key))
        {
            return TuiIntent.Quit;
        }

        if (IsScan(key))
        {
            return ScanIntent();
        }

        if (Move(key, _data.Problems.Count))
        {
            _showDetail = false;
            return TuiIntent.None;
        }

        if (key.Key is ConsoleKey.Enter)
        {
            _showDetail = !_showDetail;
        }

        return TuiIntent.None;
    }

    private TuiIntent HandlePlanReview(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Escape)
        {
            if (_confirmApply)
            {
                _confirmApply = false;
                _status = "Apply not confirmed.";
                return TuiIntent.None;
            }

            Screen = _returnScreen;
            _status = string.Empty;
            return TuiIntent.None;
        }

        if (IsQuit(key))
        {
            return TuiIntent.Quit;
        }

        if (IsScan(key))
        {
            return ScanIntent();
        }

        IReadOnlyList<PlanStep> steps = _preview.Plan?.Steps ?? [];

        if (Move(key, steps.Count))
        {
            _confirmApply = false;
            return TuiIntent.None;
        }

        if (key.Key is ConsoleKey.Spacebar)
        {
            if (steps.Count > 0)
            {
                string id = steps[SelectionOf(TuiScreen.PlanReview)].ItemId;
                if (!_included.Add(id))
                {
                    _included.Remove(id);
                }

                _confirmApply = false;
            }

            return TuiIntent.None;
        }

        if (IsChar(key, 'p'))
        {
            _policy = NextPolicy(_policy);
            RebuildPreview();
            _status = $"policy: {_policy.PresetName}";
            return TuiIntent.None;
        }

        if (IsChar(key, 'r'))
        {
            RebuildPreview();
            _status = "plan rebuilt from the current snapshot.";
            return TuiIntent.None;
        }

        if (IsChar(key, 'a'))
        {
            return TryApply();
        }

        return TuiIntent.None;
    }

    private TuiIntent HandleApplyProgress(ConsoleKeyInfo key)
    {
        if (_applySummary is not null)
        {
            if ((key.Key is ConsoleKey.Escape or ConsoleKey.Enter) || IsQuit(key))
            {
                Screen = TuiScreen.Dashboard;
                _showDetail = false;
                return TuiIntent.None;
            }

            return TuiIntent.None;
        }

        return key.Key is ConsoleKey.Escape ? RequestCancel() : TuiIntent.None;
    }

    private TuiIntent HandleQuarantine(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Escape && _confirmRestore)
        {
            _confirmRestore = false;
            _status = "Restore not confirmed.";
            return TuiIntent.None;
        }

        if (IsQuit(key))
        {
            return TuiIntent.Quit;
        }

        if (IsScan(key))
        {
            return ScanIntent();
        }

        if (Move(key, _data.Quarantine.Count))
        {
            _confirmRestore = false;
            _showDetail = false;
            return TuiIntent.None;
        }

        if (IsChar(key, 'r') && _data.Quarantine.Count > 0)
        {
            _confirmRestore = true;
            QuarantineEntry entry = _data.Quarantine[SelectionOf(TuiScreen.Quarantine)];
            _status = $"Press y to restore '{entry.Name} {entry.Version}' to '{entry.OriginalPath}'. The content is hash-checked first.";
            return TuiIntent.None;
        }

        if (IsChar(key, 'y') && _confirmRestore)
        {
            _confirmRestore = false;
            return TuiIntent.Restore(_data.Quarantine[SelectionOf(TuiScreen.Quarantine)].EntryId);
        }

        if (key.Key is ConsoleKey.Enter)
        {
            _showDetail = !_showDetail;
        }

        return TuiIntent.None;
    }

    private TuiIntent RequestCancel()
    {
        CancelRequested = true;
        _status = "cancel requested: stopping after the current step, never inside a move.";
        return TuiIntent.None;
    }

    private TuiIntent TryApply()
    {
        if (_data.Snapshot is null)
        {
            _status = "No snapshot. Press s to scan first.";
            return TuiIntent.None;
        }

        if (_appliedPlanId is not null && string.Equals(_appliedPlanId, _preview.Plan?.PlanId, StringComparison.Ordinal))
        {
            _status = "This plan already ran. Press s to rescan, then review a new plan.";
            return TuiIntent.None;
        }

        List<PlanStep> included = [.. (_preview.Plan?.Steps ?? []).Where(s => _included.Contains(s.ItemId))];
        if (included.Count == 0)
        {
            _status = "No steps selected; nothing to apply.";
            return TuiIntent.None;
        }

        if (!_confirmApply)
        {
            _confirmApply = true;
            _status = $"Press a again to apply {included.Count} step(s), {ReportTotals.FormatBytes(SumBytes(included))}. Space changes the selection.";
            return TuiIntent.None;
        }

        PlanResult built = PlanBuilder.Build(_data.Snapshot.SnapshotId, _policy, included);
        if (!built.IsSuccess)
        {
            _status = built.Message ?? "The plan could not be built.";
            return TuiIntent.None;
        }

        _confirmApply = false;
        return TuiIntent.Apply(built.Plan!);
    }

    private TuiIntent ScanIntent()
    {
        _status = "scanning…";
        return TuiIntent.Scan;
    }

    // ---- apply progress ---------------------------------------------------------------------------

    public void BeginApply(string planId, IReadOnlyList<BoundStep> bound)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentNullException.ThrowIfNull(bound);

        _progressPlanId = planId;
        _progressSteps = bound;
        _progressDone.Clear();
        while (_progressQueue.TryDequeue(out _))
        {
        }

        _runningStepId = null;
        _applySummary = null;
        CancelRequested = false;
        _included.Clear();
        Screen = TuiScreen.ApplyProgress;
        _status = $"applying {bound.Count} step(s). Esc cancels between steps.";
    }

    /// <summary>Called from the apply worker thread; the queue is the only thing that crosses threads.</summary>
    public void QueueProgress(ApplyStepProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _progressQueue.Enqueue(progress);
    }

    public void DrainProgress()
    {
        while (_progressQueue.TryDequeue(out ApplyStepProgress? progress))
        {
            if (progress.Report is null)
            {
                _runningStepId = progress.StepId;
                continue;
            }

            _progressDone[progress.StepId] = progress.Report;
            _runningStepId = null;
        }
    }

    public void FinishApply(ApplySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        DrainProgress();
        _applySummary = summary;
        _runningStepId = null;
        _appliedPlanId = summary.Ran ? _progressPlanId : _appliedPlanId;

        _status = summary.Status switch
        {
            ApplyStatus.Completed =>
                $"applied: {summary.Ok} ok · {summary.Failed} failed · {summary.SkippedUnsafe} skipped-unsafe · "
                + $"{ReportTotals.FormatBytes(summary.ReclaimedBytes)} moved to quarantine.",
            ApplyStatus.Partial =>
                $"partial: {summary.Ok} ok · {summary.Failed} failed · {summary.SkippedUnsafe} skipped-unsafe. See the rows and `coppice audit verify`.",
            ApplyStatus.Cancelled => summary.Message,
            _ => summary.Message,
        };
    }

    // ---- rendering --------------------------------------------------------------------------------

    public TuiFrame Frame()
    {
        DrainProgress();

        return Screen switch
        {
            TuiScreen.Dashboard => DashboardFrame(),
            TuiScreen.Roots => RootsFrame(),
            TuiScreen.Items => ItemsFrame(),
            TuiScreen.Problems => ProblemsFrame(),
            TuiScreen.PlanReview => PlanReviewFrame(),
            TuiScreen.ApplyProgress => ApplyProgressFrame(),
            TuiScreen.Quarantine => QuarantineFrame(),
            _ => DashboardFrame(),
        };
    }

    private TuiFrame DashboardFrame()
    {
        var blocks = new List<TuiBlock>();
        Snapshot? snapshot = _data.Snapshot;
        string title = snapshot is null ? "Dashboard · no snapshot yet" : $"Dashboard · {snapshot.SnapshotId}";

        if (snapshot is null)
        {
            blocks.Add(TuiBlock.Lines("Snapshot", [
                "No snapshot has been saved yet.",
                "Press s to scan; the TUI only reads and stores a snapshot under coppice's state root.",
            ]));
        }
        else
        {
            ulong total = snapshot.Items.Aggregate(0UL, (sum, item) => sum + item.Size);
            int referenced = snapshot.Items.Count(i => ReportTotals.UsageOf(i) == Usage.Referenced);
            int unreferenced = snapshot.Items.Count(i => ReportTotals.UsageOf(i) == Usage.Unreferenced);
            int unknown = snapshot.Items.Count - referenced - unreferenced;

            blocks.Add(TuiBlock.Lines("Snapshot", [
                $"id: {snapshot.SnapshotId}",
                $"created: {snapshot.CreatedAtUtc:yyyy-MM-dd HH:mm:ss'Z'}",
                $"ecosystems: {string.Join(", ", snapshot.Ecosystems)}",
                $"items: {snapshot.Items.Count} · {ReportTotals.FormatBytes(total)}",
                $"usage: {referenced} referenced · {unreferenced} unreferenced · {unknown} unknown",
            ]));

            blocks.Add(new TuiBlock(
                "Reclaimable by tier",
                ["TIER", "BYTES", "ITEMS"],
                [
                    TierRow("Safe", snapshot.Usage.SafeBytes, snapshot.Items.Count(i => i.Risk == Risk.Safe)),
                    TierRow("Review", snapshot.Usage.ReviewBytes, snapshot.Items.Count(i => i.Risk == Risk.Review)),
                    TierRow("Manual", snapshot.Usage.ManualBytes, snapshot.Items.Count(i => i.Risk == Risk.Manual)),
                ]));

            var ecosystems = snapshot.Items
                .GroupBy(i => i.Ecosystem, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new TuiRow([g.Key, g.Count().ToString(), ReportTotals.FormatBytes(g.Aggregate(0UL, (sum, i) => sum + i.Size))]))
                .ToList();

            blocks.Add(new TuiBlock("By ecosystem", ["ECOSYSTEM", "ITEMS", "BYTES"], ecosystems));
        }

        TuiScreen[] drills = [TuiScreen.Roots, TuiScreen.Items, TuiScreen.Problems, TuiScreen.PlanReview, TuiScreen.Quarantine];
        string[] labels = ["Roots", "Items", "Problems", "Plan review", "Quarantine"];
        string[] counts =
        [
            _data.Roots.Roots.Count.ToString(),
            (snapshot?.Items.Count ?? 0).ToString(),
            _data.Problems.Count.ToString(),
            (_preview.Plan?.Steps.Count ?? 0).ToString(),
            _data.Quarantine.Count.ToString(),
        ];

        int selected = SelectionOf(TuiScreen.Dashboard);
        blocks.Add(new TuiBlock(
            "Drill down",
            ["SCREEN", "COUNT"],
            [.. drills.Select((screen, i) => new TuiRow([labels[i], counts[i]], i == selected))],
            "enter opens the selected screen"));

        return new TuiFrame(
            TuiScreen.Dashboard,
            title,
            blocks,
            _status.Length > 0 ? _status : $"policy: {_policy.PresetName}",
            [
                new TuiKeyHelp("enter", "drill down"),
                new TuiKeyHelp("tab", "next screen"),
                new TuiKeyHelp("s", "scan"),
                new TuiKeyHelp("q", "quit"),
            ]);
    }

    private TuiFrame RootsFrame()
    {
        var rows = new List<TuiRow>();
        int selected = SelectionOf(TuiScreen.Roots);

        for (int i = 0; i < _data.Roots.Roots.Count; i++)
        {
            RootReport root = _data.Roots.Roots[i];
            rows.Add(new TuiRow(
                [
                    root.LocationId,
                    TextReport.Role(root.Root.Role),
                    TextReport.Validity(root.Root.Validity),
                    TextReport.Via(root.Root.Via, root.Root),
                    TextReport.DisplayPath(root.Root.RealPath, _data.HomeDirectory),
                    root.Root.Owner ?? string.Empty,
                ],
                i == selected));
        }

        var blocks = new List<TuiBlock>
        {
            new("Locations", ["LOCATION", "ROLE", "VALIDITY", "VIA", "PATH", "OWNER"], rows,
                rows.Count == 0 ? "No locations resolved." : null),
        };

        if (_showDetail && SelectedRoot is { } detail)
        {
            var lines = new List<string>
            {
                $"declared: {detail.Root.DeclaredPath}",
                $"real: {detail.Root.RealPath}",
                $"validity: {TextReport.Validity(detail.Root.Validity)} · role: {TextReport.Role(detail.Root.Role)}",
                $"via: {TextReport.Via(detail.Root.Via, detail.Root)}"
                    + (detail.Root.ViaDetail is null ? string.Empty : $" ({detail.Root.ViaDetail})"),
            };

            if (TextReport.ConfidenceNote(detail.Root) is { } note)
            {
                lines.Add($"confidence: LOW — {note}");
            }

            if (!string.IsNullOrEmpty(detail.Problem))
            {
                lines.Add($"fix: {detail.Problem}");
            }

            blocks.Add(TuiBlock.Lines("Selected root", lines));
        }

        return new TuiFrame(
            TuiScreen.Roots,
            "Roots",
            blocks,
            _status,
            [
                new TuiKeyHelp("↑↓", "select"),
                new TuiKeyHelp("enter", "details"),
                new TuiKeyHelp("p", "pin line"),
                new TuiKeyHelp("tab", "next screen"),
                new TuiKeyHelp("s", "scan"),
                new TuiKeyHelp("q", "quit"),
            ]);
    }

    private TuiFrame ItemsFrame()
    {
        IReadOnlyList<Item> filtered = FilteredItems();
        int selected = SelectionOf(TuiScreen.Items);

        var rows = filtered
            .Select((item, i) => new TuiRow(
                [
                    item.Ecosystem,
                    item.Kind,
                    item.Name,
                    item.Version,
                    ReportTotals.UsageOf(item).ToString(),
                    item.Risk.ToString(),
                    ReportTotals.FormatBytes(item.Size),
                ],
                i == selected))
            .ToList();

        var blocks = new List<TuiBlock>
        {
            new("Items", ["ECOSYSTEM", "KIND", "NAME", "VERSION", "USAGE", "RISK", "SIZE"], rows,
                rows.Count == 0 ? "No items match this filter." : null),
        };

        if (_showDetail && SelectedItem is { } item)
        {
            var lines = new List<string>
            {
                $"path: {item.Path}",
                $"location: {item.LocationId}",
                $"usage: {ReportTotals.UsageOf(item)} · risk: {item.Risk}",
                $"retention: {(PreviewDecisionFor(item) is { } d ? d.Reason : "not evaluated")}",
            };

            lines.AddRange(item.Facts.Select(pair => $"fact {pair.Key}: {pair.Value}"));
            blocks.Add(TuiBlock.Lines("Selected item", lines));
        }

        return new TuiFrame(
            TuiScreen.Items,
            $"Items · {_filter} · sorted by {_sort.ToString().ToLowerInvariant()}",
            blocks,
            _status,
            [
                new TuiKeyHelp("↑↓", "select"),
                new TuiKeyHelp("enter", "inspect"),
                new TuiKeyHelp("f", "filter"),
                new TuiKeyHelp("o", "sort"),
                new TuiKeyHelp("tab", "next screen"),
                new TuiKeyHelp("s", "scan"),
                new TuiKeyHelp("q", "quit"),
            ]);
    }

    private TuiFrame ProblemsFrame()
    {
        int selected = SelectionOf(TuiScreen.Problems);

        var rows = _data.Problems
            .Select((problem, i) => new TuiRow(
                [problem.Severity, problem.Source, problem.Code, problem.Summary],
                i == selected))
            .ToList();

        var blocks = new List<TuiBlock>
        {
            new("Problems", ["SEVERITY", "SOURCE", "CODE", "SUMMARY"], rows,
                rows.Count == 0 ? "No problems found in the snapshot or the resolved roots." : null),
        };

        if (_showDetail && selected < _data.Problems.Count)
        {
            TuiProblem problem = _data.Problems[selected];
            var lines = new List<string> { problem.Summary, problem.Detail };
            if (!string.IsNullOrEmpty(problem.Path))
            {
                lines.Add($"path: {problem.Path}");
            }

            blocks.Add(TuiBlock.Lines("Selected problem", lines));
        }

        return new TuiFrame(
            TuiScreen.Problems,
            "Problems",
            blocks,
            _status,
            [
                new TuiKeyHelp("↑↓", "select"),
                new TuiKeyHelp("enter", "details"),
                new TuiKeyHelp("tab", "next screen"),
                new TuiKeyHelp("s", "scan"),
                new TuiKeyHelp("q", "quit"),
            ]);
    }

    private TuiFrame PlanReviewFrame()
    {
        var blocks = new List<TuiBlock>();
        Plan? plan = _preview.Plan;

        if (plan is null)
        {
            blocks.Add(TuiBlock.Lines("Plan", [_preview.Message.Length > 0 ? _preview.Message : "Nothing to propose."]));
        }
        else
        {
            int selected = SelectionOf(TuiScreen.PlanReview);
            List<PlanStep> steps = [.. plan.Steps];

            var rows = steps
                .Select((step, i) => new TuiRow(
                    [
                        _included.Contains(step.ItemId) ? "[x]" : "[ ]",
                        KindOf(step),
                        step.Risk.ToString(),
                        ReportTotals.FormatBytes(step.ExpectedBytes),
                        LabelOf(step.ItemId),
                        step.Reason,
                    ],
                    i == selected))
                .ToList();

            blocks.Add(new TuiBlock("Steps", ["KEEP", "ACTION", "RISK", "BYTES", "ITEM", "REASON"], rows,
                "space toggles the selected step; a twice applies the kept steps"));

            List<PlanStep> included = [.. steps.Where(s => _included.Contains(s.ItemId))];
            blocks.Add(TuiBlock.Lines("Plan summary", [
                $"plan: {plan.PlanId}",
                $"snapshot: {plan.SnapshotId}",
                $"policy: {_policy.PresetName}",
                $"kept: {included.Count} of {steps.Count} step(s) · {ReportTotals.FormatBytes(SumBytes(included))}",
            ]));
        }

        blocks.Add(TuiBlock.Lines("Policy", [
            $"{_policy.PresetName}: keep latest {_policy.KeepLatestN}, ceiling {_policy.MaximumRisk.ToString().ToLowerInvariant()}",
            "p cycles default → conservative → aggressive and rebuilds the preview",
        ]));

        return new TuiFrame(
            TuiScreen.PlanReview,
            "Plan review",
            blocks,
            _status,
            [
                new TuiKeyHelp("↑↓", "select"),
                new TuiKeyHelp("space", "toggle"),
                new TuiKeyHelp("a", "apply (twice)"),
                new TuiKeyHelp("p", "policy"),
                new TuiKeyHelp("r", "rebuild"),
                new TuiKeyHelp("esc", "back"),
                new TuiKeyHelp("s", "scan"),
            ]);

        static string KindOf(PlanStep step) => step.Action.Kind switch
        {
            RemovalKind.NativeCommand => "native",
            RemovalKind.ReportOnly => "report",
            _ => "path",
        };
    }

    private TuiFrame ApplyProgressFrame()
    {
        var rows = new List<TuiRow>();
        ulong reclaimed = 0;

        for (int i = 0; i < _progressSteps.Count; i++)
        {
            BoundStep step = _progressSteps[i];
            _progressDone.TryGetValue(step.PlanStep.ItemId, out ApplyStepReport? done);

            string result = done?.Result ?? (string.Equals(step.PlanStep.ItemId, _runningStepId, StringComparison.Ordinal) ? "running" : "pending");
            if (done is { Class: ApplyStepClass.Ok } && done.Bytes > 0)
            {
                reclaimed += done.Bytes;
            }

            rows.Add(new TuiRow(
                [
                    (i + 1).ToString(),
                    LabelOf(step.PlanStep.ItemId),
                    result,
                    done is null ? string.Empty : ReportTotals.FormatBytes(done.Bytes),
                    done?.Message ?? string.Empty,
                ]));
        }

        int finished = _progressDone.Count;

        var blocks = new List<TuiBlock>
        {
            new("Steps", ["#", "ITEM", "RESULT", "BYTES", "MESSAGE"], rows,
                _progressSteps.Count == 0 ? "No steps." : null),
            TuiBlock.Lines("Progress", [
                $"finished: {finished} of {_progressSteps.Count}",
                $"reclaimed: {ReportTotals.FormatBytes(reclaimed)}",
                _applySummary is null
                    ? (CancelRequested ? "cancelling after the current step" : "in flight")
                    : $"status: {_applySummary.Status}",
            ]),
        };

        if (_applySummary is not null)
        {
            blocks.Add(TuiBlock.Lines("Outcome", [
                $"{_applySummary.Ok} ok · {_applySummary.Failed} failed · {_applySummary.SkippedUnsafe} skipped-unsafe · {_applySummary.Reported} reported",
                "press esc or enter to return to the dashboard",
            ]));
        }

        string status = _applySummary is not null
            ? _status
            : (CancelRequested
                ? "cancel requested — finishing the step in flight, then stopping. The journal stays open for --resume."
                : _status);

        return new TuiFrame(
            TuiScreen.ApplyProgress,
            "Apply progress",
            blocks,
            status,
            _applySummary is null
                ? [new TuiKeyHelp("esc", "cancel after this step")]
                : [new TuiKeyHelp("esc", "dashboard")]);
    }

    private TuiFrame QuarantineFrame()
    {
        int selected = SelectionOf(TuiScreen.Quarantine);

        var rows = _data.Quarantine
            .Select((entry, i) => new TuiRow(
                [
                    $"{entry.Name} {entry.Version}".Trim(),
                    ReportTotals.FormatBytes(entry.SizeBytes),
                    entry.LocationId,
                    entry.QuarantinedAtUtc.ToString("yyyy-MM-dd HH:mm"),
                    entry.OriginalPath,
                ],
                i == selected))
            .ToList();

        var blocks = new List<TuiBlock>
        {
            new("Quarantined", ["ITEM", "SIZE", "LOCATION", "QUARANTINED", "ORIGINAL PATH"], rows,
                rows.Count == 0 ? "Nothing in quarantine." : null),
        };

        if (_showDetail && _data.Quarantine.Count > selected)
        {
            QuarantineEntry entry = _data.Quarantine[selected];
            blocks.Add(TuiBlock.Lines("Selected entry", [
                $"entry: {entry.EntryId}",
                $"from: {entry.OriginalPath}",
                $"to: {entry.QuarantinePath}",
                $"root: {entry.Root}",
                $"content hash: {entry.ContentHash}",
            ]));
        }

        return new TuiFrame(
            TuiScreen.Quarantine,
            "Quarantine",
            blocks,
            _status,
            [
                new TuiKeyHelp("↑↓", "select"),
                new TuiKeyHelp("enter", "details"),
                new TuiKeyHelp("r", "restore"),
                new TuiKeyHelp("y", "confirm restore"),
                new TuiKeyHelp("tab", "next screen"),
                new TuiKeyHelp("q", "quit"),
            ]);
    }

    // ---- data helpers -----------------------------------------------------------------------------

    public IReadOnlyList<Item> FilteredItems()
    {
        if (_data.Snapshot is null)
        {
            return [];
        }

        IEnumerable<Item> items = _data.Snapshot.Items.Where(item => _filter switch
        {
            ItemFilter.Safe => item.Risk == Risk.Safe,
            ItemFilter.Review => item.Risk == Risk.Review,
            ItemFilter.Manual => item.Risk == Risk.Manual,
            ItemFilter.Referenced => ReportTotals.UsageOf(item) == Usage.Referenced,
            ItemFilter.Unreferenced => ReportTotals.UsageOf(item) == Usage.Unreferenced,
            ItemFilter.Unknown => ReportTotals.UsageOf(item) == Usage.Unknown,
            _ => true,
        });

        items = _sort switch
        {
            ItemSort.Name => items.OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Version, StringComparer.Ordinal),
            ItemSort.Risk => items.OrderBy(i => i.Risk).ThenBy(i => i.Name, StringComparer.Ordinal),
            _ => items.OrderByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.Ordinal),
        };

        return [.. items];
    }

    private Item? SelectedItem
    {
        get
        {
            IReadOnlyList<Item> rows = FilteredItems();
            int selected = SelectionOf(TuiScreen.Items);
            return selected < rows.Count ? rows[selected] : null;
        }
    }

    private RootReport? SelectedRoot
    {
        get
        {
            int selected = SelectionOf(TuiScreen.Roots);
            return selected < _data.Roots.Roots.Count ? _data.Roots.Roots[selected] : null;
        }
    }

    private RetentionDecision? PreviewDecisionFor(Item item) =>
        _preview.Decisions.TryGetValue(item.ItemId, out RetentionDecision? decision) ? decision : null;

    private string LabelOf(string itemId) =>
        _itemsById.TryGetValue(itemId, out Item? item) ? $"{item.Name} {item.Version}".Trim() : itemId;

    private void IndexItems()
    {
        _itemsById = _data.Snapshot is null
            ? new Dictionary<string, Item>(StringComparer.Ordinal)
            : _data.Snapshot.Items.ToDictionary(i => i.ItemId, i => i, StringComparer.Ordinal);
    }

    private PlanPreview PreviewForCurrentSnapshot() =>
        _data.Snapshot is null
            ? new PlanPreview(
                null,
                "No snapshot. Press s to scan first.",
                new Dictionary<string, RetentionDecision>(StringComparer.Ordinal),
                PlanPreviewStatus.Failed)
            : _previewFactory(_data.Snapshot, _policy);

    private void EnsurePreview()
    {
        if (_data.Snapshot is not null && _preview.Plan is null && _preview.Decisions.Count == 0)
        {
            RebuildPreview();
        }
    }

    /// <summary>
    /// Rebuilds the preview and keeps the cursor inside the new step list. Without the clamp, changing to a
    /// policy that proposes fewer steps could leave the cursor past the end and `space` would throw.
    /// </summary>
    private void RebuildPreview()
    {
        _preview = PreviewForCurrentSnapshot();
        IncludeAllSteps();
        _confirmApply = false;
        SetSelection(TuiScreen.PlanReview, Math.Min(SelectionOf(TuiScreen.PlanReview), MaxSelection(TuiScreen.PlanReview)));
    }

    private void IncludeAllSteps()
    {
        _included.Clear();
        foreach (PlanStep step in _preview.Plan?.Steps ?? [])
        {
            _included.Add(step.ItemId);
        }
    }

    private void Navigate(int direction)
    {
        int index = TabIndexOf(Screen);
        if (index < 0)
        {
            Screen = TuiScreen.Dashboard;
            return;
        }

        int next = (index + direction + TabOrder.Count) % TabOrder.Count;
        Screen = TabOrder[next];
        _returnScreen = TuiScreen.Dashboard;
        _status = string.Empty;
        _showDetail = false;
        _confirmApply = false;
        _confirmRestore = false;
        SetSelection(Screen, Math.Min(SelectionOf(Screen), MaxSelection(Screen)));

        if (Screen == TuiScreen.PlanReview)
        {
            EnsurePreview();
        }
    }

    private static int TabIndexOf(TuiScreen screen)
    {
        for (int i = 0; i < TabOrder.Count; i++)
        {
            if (TabOrder[i] == screen)
            {
                return i;
            }
        }

        return -1;
    }

    private int MaxSelection(TuiScreen screen) => screen switch
    {
        TuiScreen.Dashboard => 4,
        TuiScreen.Roots => Math.Max(0, _data.Roots.Roots.Count - 1),
        TuiScreen.Items => Math.Max(0, FilteredItems().Count - 1),
        TuiScreen.Problems => Math.Max(0, _data.Problems.Count - 1),
        TuiScreen.PlanReview => Math.Max(0, (_preview.Plan?.Steps.Count ?? 0) - 1),
        TuiScreen.Quarantine => Math.Max(0, _data.Quarantine.Count - 1),
        _ => 0,
    };

    private bool Move(ConsoleKeyInfo key, int count)
    {
        if (count == 0)
        {
            SetSelection(Screen, 0);
            return key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Home or ConsoleKey.End
                || IsChar(key, 'k') || IsChar(key, 'j');
        }

        int current = SelectionOf(Screen);
        int next = key.Key switch
        {
            ConsoleKey.UpArrow => current - 1,
            ConsoleKey.DownArrow => current + 1,
            ConsoleKey.Home => 0,
            ConsoleKey.End => count - 1,
            _ when IsChar(key, 'k') => current - 1,
            _ when IsChar(key, 'j') => current + 1,
            _ => current,
        };

        if (next == current)
        {
            return false;
        }

        SetSelection(Screen, Math.Clamp(next, 0, Math.Max(0, count - 1)));
        return true;
    }

    private int SelectionOf(TuiScreen screen) => _selections.TryGetValue(screen, out int value) ? value : 0;

    private void SetSelection(TuiScreen screen, int value) => _selections[screen] = value;

    private void ClampSelections()
    {
        foreach (TuiScreen screen in TabOrder)
        {
            SetSelection(screen, Math.Clamp(SelectionOf(screen), 0, MaxSelection(screen)));
        }
    }

    private static bool IsQuit(ConsoleKeyInfo key) => IsChar(key, 'q');

    private static bool IsScan(ConsoleKeyInfo key) => IsChar(key, 's');

    private static bool IsChar(ConsoleKeyInfo key, char expected) =>
        char.ToLowerInvariant(key.KeyChar) == expected;

    private static T Next<T>(T value) where T : struct, Enum
    {
        T[] values = Enum.GetValues<T>();
        int index = Array.IndexOf(values, value);
        return values[(index + 1) % values.Length];
    }

    private static Policy NextPolicy(Policy policy) => policy.PresetName switch
    {
        "default" => Policy.Conservative,
        "conservative" => Policy.Aggressive,
        _ => Policy.Default,
    };

    private static ulong SumBytes(IEnumerable<PlanStep> steps) =>
        steps.Aggregate(0UL, (sum, step) => checked(sum + step.ExpectedBytes));

    private static TuiRow TierRow(string tier, ulong bytes, int count) =>
        new([tier, ReportTotals.FormatBytes(bytes), count.ToString()]);
}

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coppice.Application;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Retention;
using Coppice.Core.Snapshots;
using Coppice.Gui.Services;
using AppUsageBreakdown = Coppice.Application.UsageBreakdown;
using AvaloniaApplication = Avalonia.Application;
using Item = Coppice.Core.Domain.Item;
using Policy = Coppice.Core.Domain.Policy;
using Problem = Coppice.Core.Domain.Problem;

namespace Coppice.Gui.ViewModels;

public enum PageKey
{
    Setup,
    Scan,
    Review,
    Apply,
    Quarantine,
    Items,
    Roots,
    Problems,
    History,
}

public enum StatusTone
{
    Neutral,
    Success,
    Warning,
    Error,
}

/// <summary>
/// The desktop shell's view model. It owns presentation state and the step flow; every question about
/// a cache, a plan or a mutation is still answered by an application service.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly GuiServices _services;
    private readonly GuiPreferences _preferences;

    private Snapshot? _snapshot;
    private PlanPreview? _preview;
    private Dictionary<string, Item> _itemsById = new(StringComparer.Ordinal);
    private bool _itemsLoaded;
    private bool _rootsLoaded;
    private int _itemsGeneration;
    private int _previewGeneration;

    public MainWindowViewModel(GuiServices services, GuiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preferences);

        _services = services;
        _preferences = preferences;
        _preferences.ProjectRoots = [.. _preferences.ProjectRoots];

        Policies = PolicyChoices.All;
        _selectedPolicy = Policies[0];
        _projectRoots = string.Join(Environment.NewLine, _preferences.ProjectRoots);
        _status = "Welcome to Coppice. Add a project root, then run a scan.";
        _startupStatus = "Reading your last scan…";
        _isDarkTheme = AvaloniaApplication.Current?.ActualThemeVariant == ThemeVariant.Dark;
    }

    public IReadOnlyList<PolicyOption> Policies { get; }

    [ObservableProperty]
    private PageKey _currentPage = PageKey.Setup;

    [ObservableProperty]
    private PolicyOption _selectedPolicy;

    [ObservableProperty]
    private int _customKeepLatest = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectRoots))]
    [NotifyPropertyChangedFor(nameof(IsSetupDone))]
    [NotifyPropertyChangedFor(nameof(ProjectRootsSummary))]
    private string _projectRoots;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private StatusTone _statusTone = StatusTone.Neutral;

    [ObservableProperty]
    private string _startupStatus;

    [ObservableProperty]
    private bool _isInitializing = true;

    [ObservableProperty]
    private string _scanSummary = "No scan yet. Coppice has not read this machine.";

    [ObservableProperty]
    private string _planSummary = "Scan first, then review the steps Coppice proposes.";

    [ObservableProperty]
    private string _policySummary = string.Empty;

    [ObservableProperty]
    private string _rootsStatus = "Locations are read when you open this page.";

    [ObservableProperty]
    private string _itemsSummary = "No scan loaded.";

    [ObservableProperty]
    private string? _snapshotId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshRootsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshQuarantineCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private bool _isPreviewBuilding;

    [ObservableProperty]
    private bool _isItemsLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshRootsCommand))]
    private bool _isRootsLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshQuarantineCommand))]
    private bool _isQuarantineLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshHistoryCommand))]
    private bool _isHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryDetail))]
    private bool _hasHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryDetail))]
    private int _historyCount;

    [ObservableProperty]
    private string _historySummary = "No snapshots yet. Run a scan to start a history.";

    [ObservableProperty]
    private string _historyLatest = "Scan once to create a baseline; scan again to see growth.";

    [ObservableProperty]
    private IReadOnlyList<HistoryTrendPoint> _historyPoints = [];

    [ObservableProperty]
    private IReadOnlyList<HistoryTrendPoint> _historyRows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateCommand))]
    [NotifyPropertyChangedFor(nameof(IsScanDone))]
    [NotifyPropertyChangedFor(nameof(EvidenceDetail))]
    [NotifyPropertyChangedFor(nameof(SnapshotLabel))]
    private bool _hasSnapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueToApplyCommand))]
    [NotifyPropertyChangedFor(nameof(CanShowApplyReady))]
    [NotifyPropertyChangedFor(nameof(CanShowApplyEmpty))]
    [NotifyPropertyChangedFor(nameof(ReviewCtaLabel))]
    private bool _hasPlan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateCommand))]
    [NotifyPropertyChangedFor(nameof(IsReviewDone))]
    [NotifyPropertyChangedFor(nameof(IsApplyDone))]
    [NotifyPropertyChangedFor(nameof(CanShowApplyReady))]
    [NotifyPropertyChangedFor(nameof(CanShowApplyEmpty))]
    private bool _hasApplyOutcome;

    [ObservableProperty]
    private bool _wasApplyCancelled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isApplyConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    private bool _isRestoreConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private QuarantineRow? _selectedQuarantine;

    [ObservableProperty]
    private int _selectedStepCount;

    [ObservableProperty]
    private string _selectedStepBytes = "0 B";

    [ObservableProperty]
    private double _applyPercent;

    [ObservableProperty]
    private string _applyPercentLabel = "0%";

    [ObservableProperty]
    private string _applyOutcomeTitle = string.Empty;

    [ObservableProperty]
    private string _applyOutcomeDetail = string.Empty;

    [ObservableProperty]
    private int _lastAppliedCount;

    [ObservableProperty]
    private string _lastAppliedBytes = "0 B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvidenceDetail))]
    private int _itemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvidenceDetail))]
    private int _referencedCount;

    [ObservableProperty]
    private int _unreferencedCount;

    [ObservableProperty]
    private int _unknownCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RootsDetail))]
    [NotifyPropertyChangedFor(nameof(HasRoots))]
    private int _rootsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemsDetail))]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private int _problemsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuarantineDetail))]
    [NotifyPropertyChangedFor(nameof(QuarantineToolbar))]
    [NotifyPropertyChangedFor(nameof(HasQuarantineEntries))]
    private int _quarantineCount;

    [ObservableProperty]
    private bool _hasProjects;

    [ObservableProperty]
    private int _projectCount;

    [ObservableProperty]
    private string _potentialReclaim = "0 B";

    [ObservableProperty]
    private string _quarantineSummary = "Nothing is quarantined yet.";

    [ObservableProperty]
    private bool _isHelpOpen;

    [ObservableProperty]
    private bool _isDarkTheme;

    public ObservableCollection<PlanStepRow> PlanSteps { get; } = [];

    public ObservableCollection<string> ApplyLog { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<ItemRow> _items = [];

    [ObservableProperty]
    private IReadOnlyList<RootRow> _roots = [];

    [ObservableProperty]
    private IReadOnlyList<ProblemRow> _problems = [];

    [ObservableProperty]
    private IReadOnlyList<QuarantineRow> _quarantineEntries = [];

    public bool HasProjectRoots => !string.IsNullOrWhiteSpace(ProjectRoots);

    public bool IsSetupDone => HasProjectRoots;

    public bool IsScanDone => HasSnapshot;

    public bool IsReviewDone => HasApplyOutcome;

    public bool IsApplyDone => HasApplyOutcome;

    public string ProjectRootsSummary
    {
        get
        {
            int count = ParseProjectRoots(ProjectRoots).Length;
            return count switch
            {
                0 => "No roots yet",
                1 => "1 project root",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count} project roots"),
            };
        }
    }

    public string EvidenceDetail => !HasSnapshot
        ? "No scan yet"
        : string.Create(CultureInfo.InvariantCulture, $"{ItemCount} item(s)");

    public string RootsDetail => RootsCount == 0
        ? "Machine locations"
        : string.Create(CultureInfo.InvariantCulture, $"{RootsCount} location(s)");

    public string ProblemsDetail => ProblemsCount == 0
        ? "Health findings"
        : string.Create(CultureInfo.InvariantCulture, $"{ProblemsCount} finding(s)");

    public string QuarantineDetail => QuarantineCount == 0
        ? "Nothing moved"
        : string.Create(CultureInfo.InvariantCulture, $"{QuarantineCount} in quarantine");

    public string QuarantineToolbar => QuarantineCount == 0
        ? "0 item(s) in quarantine"
        : string.Create(CultureInfo.InvariantCulture, $"{QuarantineCount} item(s) in quarantine · restore is hash-checked");

    public string HistoryDetail => !HasHistory
        ? "Growth over time"
        : string.Create(CultureInfo.InvariantCulture, $"{HistoryCount} snapshot(s)");

    public string ReviewCtaLabel => HasPlan ? "Review the plan" : "See what was kept";

    public bool HasQuarantineEntries => QuarantineCount > 0;

    public bool HasSelection => SelectedQuarantine is not null;

    public bool HasRoots => RootsCount > 0;

    public bool HasProblems => ProblemsCount > 0;

    public string SnapshotLabel => HasSnapshot ? "Last scan loaded" : "No snapshot";

    public bool IsCustomPolicy => SelectedPolicy?.Name.StartsWith("Custom", StringComparison.OrdinalIgnoreCase) == true;

    public bool CanShowApplyReady => HasPlan && !HasApplyOutcome && SelectedStepCount > 0;

    public bool CanShowApplyEmpty => !HasPlan && !HasApplyOutcome;

    private Policy CurrentPolicy =>
        IsCustomPolicy
            ? Policy.Default with { PresetName = "custom", KeepLatestN = Math.Clamp(CustomKeepLatest, 0, 1000) }
            : SelectedPolicy?.Policy ?? Policy.Default;

    /// <summary>
    /// Loads the previous snapshot and builds its plan preview off the UI thread, then opens the
    /// walkthrough on a first run. Doctor queries, item rows and quarantine are deliberately deferred.
    /// </summary>
    public async Task InitializeAsync()
    {
        IsInitializing = true;
        StartupStatus = "Reading your last scan…";

        try
        {
            Policy policy = CurrentPolicy;
            Snapshot? snapshot = null;
            Dictionary<string, Item> itemsById = new(StringComparer.Ordinal);
            PlanPreview? preview = null;

            (snapshot, itemsById, preview) = await Task.Run(
                async () =>
                {
                    Snapshot? loaded = await _services.Snapshots.LoadLatestAsync().ConfigureAwait(false);
                    if (loaded is null)
                    {
                        return ((Snapshot?)null, new Dictionary<string, Item>(StringComparer.Ordinal), (PlanPreview?)null);
                    }

                    return (
                        loaded,
                        loaded.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal),
                        (PlanPreview?)PlanPreviewService.Preview(loaded, policy, _services.Registry));
                });

            _snapshot = snapshot;
            _itemsById = itemsById;
            _preview = preview;
            SnapshotId = snapshot?.SnapshotId;
            HasSnapshot = snapshot is not null;
            ApplySnapshotCounts();
            ApplyPreview(preview, clearOutcome: true);

            if (snapshot is not null)
            {
                ScanSummary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{snapshot.Items.Count} item(s) in your last scan · {ReferencedCount} referenced");
                SetStatus("Your last scan is ready. Review the plan or rescan for fresh evidence.", StatusTone.Success);
            }
            else
            {
                ScanSummary = "No stored scan found. Coppice will read your projects during the first scan.";
                SetStatus("Ready. Add a project root, then run a scan.", StatusTone.Neutral);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Startup failed: {ex.Message}", StatusTone.Error);
            StartupStatus = "The last scan could not be loaded.";
        }
        finally
        {
            IsInitializing = false;
        }

        if (!_preferences.HelpSeen)
        {
            IsHelpOpen = true;
        }

        _ = WarmSecondaryStateAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(string? page)
    {
        if (!Enum.TryParse(page, ignoreCase: true, out PageKey key))
        {
            return;
        }

        CurrentPage = key;

        switch (key)
        {
            case PageKey.Items:
                _ = EnsureItemsLoadedAsync();
                break;
            case PageKey.Roots when !_rootsLoaded:
                _ = LoadRootsAsync(showSpinner: true, CancellationToken.None);
                break;
            case PageKey.Quarantine:
                _ = LoadQuarantineAsync(showSpinner: true, CancellationToken.None);
                break;
            case PageKey.History:
                _ = LoadHistoryAsync(showSpinner: true, CancellationToken.None);
                break;
        }
    }

    private bool CanNavigate(string? page)
    {
        if (!Enum.TryParse(page, ignoreCase: true, out PageKey key))
        {
            return false;
        }

        return key switch
        {
            PageKey.Review => HasSnapshot,
            PageKey.Apply => HasPlan || HasApplyOutcome,
            PageKey.Items => HasSnapshot,
            _ => true,
        };
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        IsBusy = true;
        IsApplyConfirmed = false;
        HasApplyOutcome = false;
        WasApplyCancelled = false;
        ApplyLog.Clear();
        ApplyPercent = 0;
        ApplyPercentLabel = "0%";
        SetStatus("Checking your registered locations and reading the caches…", StatusTone.Neutral);
        PersistPreferences();

        try
        {
            string[] projects = ParseProjectRoots(ProjectRoots);
            Policy policy = CurrentPolicy;

            ScanWork work = await Task.Run(
                async () =>
                {
                    DoctorReport doctor = _services.Doctor.Inspect(cancellationToken);
                    ScanReport scan = await _services.Scan
                        .RunAsync(ScanWiring.FromDoctor(doctor, _services.Registry), projects, cancellationToken)
                        .ConfigureAwait(false);

                    Snapshot? snapshot = await _services.Snapshots.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        return new ScanWork(doctor, scan, null, new Dictionary<string, Item>(StringComparer.Ordinal), null);
                    }

                    Dictionary<string, Item> itemsById = snapshot.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
                    PlanPreview preview = PlanPreviewService.Preview(snapshot, policy, _services.Registry);
                    return new ScanWork(doctor, scan, snapshot, itemsById, preview);
                },
                cancellationToken);

            LoadRoots(work.Doctor);
            LoadProblems(work.Scan);

            _snapshot = work.Snapshot;
            _itemsById = work.ItemsById;
            _preview = work.Preview;
            _itemsLoaded = false;
            _itemsGeneration++;
            Items = [];
            SnapshotId = _snapshot?.SnapshotId;
            HasSnapshot = _snapshot is not null;
            ApplySnapshotCounts();
            ApplyPreview(work.Preview, clearOutcome: true);
            ProjectCount = work.Scan.ProjectCount;
            HasProjects = work.Scan.ProjectCount > 0;

            ScanSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"{work.Scan.Items.Count} item(s) · {work.Scan.ProjectCount} project(s) read · {work.Scan.Usage.Referenced} referenced");

            if (_snapshot is null)
            {
                SetStatus("The scan completed but its snapshot could not be loaded.", StatusTone.Warning);
                return;
            }

            SetStatus(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Scan complete — {work.Scan.Items.Count} item(s) evaluated. Nothing was changed."),
                StatusTone.Success);

            await LoadQuarantineAsync(showSpinner: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled. Nothing was changed.", StatusTone.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"Scan failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand]
    private void SelectAllSteps()
    {
        foreach (PlanStepRow step in PlanSteps)
        {
            step.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSteps()
    {
        foreach (PlanStepRow step in PlanSteps)
        {
            step.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanContinueToApply))]
    private void ContinueToApply() => Navigate(nameof(PageKey.Apply));

    private bool CanContinueToApply() => HasPlan && SelectedStepCount > 0;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is null)
        {
            SetStatus("There is no snapshot to apply against.", StatusTone.Warning);
            return;
        }

        List<PlanStep> selected = [.. PlanSteps.Where(step => step.IsSelected).Select(step => step.Step)];
        if (selected.Count == 0)
        {
            SetStatus("Select at least one step on the review page.", StatusTone.Warning);
            return;
        }

        PlanResult built = PlanBuilder.Build(_snapshot.SnapshotId, CurrentPolicy, selected);
        if (!built.IsSuccess || built.Plan is null)
        {
            SetStatus(built.Message ?? "The selected steps could not be built into a plan.", StatusTone.Error);
            return;
        }

        string selectedBytes = SelectedStepBytes;

        IsApplying = true;
        IsBusy = true;
        IsApplyConfirmed = false;
        HasApplyOutcome = false;
        WasApplyCancelled = false;
        ApplyLog.Clear();
        ApplyPercent = 0;
        ApplyPercentLabel = "0%";
        SetStatus("Moving the selected items to quarantine…", StatusTone.Neutral);

        try
        {
            var progress = new Progress<ApplyStepProgress>(step =>
            {
                ApplyPercent = step.Total == 0 ? 0 : (double)step.Index / step.Total;
                ApplyPercentLabel = string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(ApplyPercent * 100)}%");
                ApplyLog.Add(string.Create(CultureInfo.InvariantCulture, $"{step.Index}/{step.Total}  {step.StepId}"));
            });

            PlanApplyOutcome outcome = await Task.Run(
                () => _services.Apply.ApplyAsync(built.Plan!, strict: false, resume: false, confirmed: true, progress, cancellationToken),
                cancellationToken);

            if (!outcome.Prepared || outcome.Summary is null)
            {
                SetStatus(outcome.Message, StatusTone.Error);
                return;
            }

            ApplySummary summary = outcome.Summary;
            ApplyPercent = 1;
            ApplyPercentLabel = "100%";
            ApplyLog.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{summary.Ok} moved · {summary.Failed} failed · {summary.SkippedUnsafe} skipped-unsafe"));

            LastAppliedCount = summary.Ok;
            LastAppliedBytes = selectedBytes;
            ApplyOutcomeTitle = summary.Ok == 0
                ? "Nothing was moved."
                : string.Create(CultureInfo.InvariantCulture, $"{summary.Ok} item(s) moved to quarantine.");
            ApplyOutcomeDetail = string.IsNullOrWhiteSpace(summary.Message)
                ? "Nothing was deleted. Every moved item can be restored from the Quarantine step."
                : summary.Message;

            HasApplyOutcome = true;
            HasPlan = false;
            ClearPlanRows();
            MarkApplied(selected);
            PlanSummary = "These steps were applied. Rescan to build a fresh plan.";
            SetStatus(
                summary.Ok == 0
                    ? "The run moved nothing. Rescan before trying again."
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{summary.Ok} item(s) moved to quarantine — nothing was deleted."),
                summary.Ok == 0 || summary.Failed > 0 || summary.SkippedUnsafe > 0
                    ? StatusTone.Warning
                    : StatusTone.Success);

            await LoadQuarantineAsync(showSpinner: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WasApplyCancelled = true;
            HasPlan = false;
            ClearPlanRows();
            InvalidateItems();
            SetStatus("Apply cancelled between steps. Rescan before applying again.", StatusTone.Warning);
            await LoadQuarantineAsync(showSpinner: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus($"Apply failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            IsApplying = false;
            IsBusy = false;
        }
    }

    private bool CanApply() => !IsBusy && HasPlan && IsApplyConfirmed && SelectedStepCount > 0;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRestore))]
    private async Task RestoreSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedQuarantine is null)
        {
            return;
        }

        string entryId = SelectedQuarantine.Id;
        IsRestoring = true;
        IsBusy = true;
        IsRestoreConfirmed = false;

        try
        {
            RestoreResult result = await Task.Run(
                () => _services.Quarantine.RestoreAsync(entryId, cancellationToken),
                cancellationToken);

            SetStatus(result.Message, result.Restored ? StatusTone.Success : StatusTone.Error);
            SelectedQuarantine = null;
            await LoadQuarantineAsync(showSpinner: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Restore cancelled. Nothing was changed.", StatusTone.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"Restore failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            IsRestoring = false;
            IsBusy = false;
        }
    }

    private bool CanRestore() => !IsBusy && IsRestoreConfirmed && SelectedQuarantine is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshRoots))]
    private Task RefreshRootsAsync() => LoadRootsAsync(showSpinner: true, CancellationToken.None);

    private bool CanRefreshRoots() => !IsRootsLoading && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefreshQuarantine))]
    private Task RefreshQuarantineAsync() => LoadQuarantineAsync(showSpinner: true, CancellationToken.None);

    private bool CanRefreshQuarantine() => !IsQuarantineLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshHistory))]
    private Task RefreshHistoryAsync() => LoadHistoryAsync(showSpinner: true, CancellationToken.None);

    private bool CanRefreshHistory() => !IsHistoryLoading;

    [RelayCommand]
    private void ToggleHelp()
    {
        if (IsHelpOpen)
        {
            CloseHelp();
            return;
        }

        IsHelpOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanCloseHelp))]
    private void CloseHelp()
    {
        IsHelpOpen = false;
        if (!_preferences.HelpSeen)
        {
            _preferences.HelpSeen = true;
            _preferences.Save();
        }
    }

    private bool CanCloseHelp() => IsHelpOpen;

    [RelayCommand]
    private void StartFromHelp()
    {
        CloseHelp();
        Navigate(nameof(PageKey.Setup));
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        if (AvaloniaApplication.Current is not { } application)
        {
            return;
        }

        ThemeVariant next = application.ActualThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        application.RequestedThemeVariant = next;
        IsDarkTheme = next == ThemeVariant.Dark;
        _preferences.Theme = next == ThemeVariant.Dark ? "Dark" : "Light";
        _preferences.Save();
    }

    public void AddProjectRoots(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        List<string> merged = [.. ParseProjectRoots(ProjectRoots)];
        foreach (string path in paths)
        {
            string trimmed = path.Trim();
            if (trimmed.Length > 0 && !merged.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(trimmed);
            }
        }

        ProjectRoots = string.Join(Environment.NewLine, merged);
        PersistPreferences();
    }

    public void ReportFolderPickerError(string message) => SetStatus(message, StatusTone.Error);

    public void PersistPreferences()
    {
        _preferences.ProjectRoots = [.. ParseProjectRoots(ProjectRoots)];
        _preferences.Save();
    }

    private async Task LoadRootsAsync(bool showSpinner, CancellationToken cancellationToken)
    {
        if (showSpinner)
        {
            IsRootsLoading = true;
            RootsStatus = "Reading environment, tool answers and per-OS defaults…";
        }

        try
        {
            DoctorReport report = await Task.Run(() => _services.Doctor.Inspect(), cancellationToken);
            LoadRoots(report);
            _rootsLoaded = true;
            RootsStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"{report.Roots.Count} location(s) resolved · {report.Issues.Count} issue(s)");
        }
        catch (OperationCanceledException)
        {
            // Navigating away or replacing the request is not an error.
        }
        catch (Exception ex)
        {
            RootsStatus = "Locations could not be read.";
            SetStatus($"Location check failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            IsRootsLoading = false;
        }
    }

    private void LoadRoots(DoctorReport report)
    {
        List<RootRow> rows =
        [
            .. report.Roots.Select(root => new RootRow(
                root.LocationId,
                root.Root.RealPath,
                root.Root.Via.ToString(),
                root.Root.Role.ToString(),
                root.Root.Validity.ToString(),
                root.Root.IsCleanable,
                root.Problem ?? string.Empty)),
        ];

        Roots = rows;
        RootsCount = rows.Count;
    }

    private void LoadProblems(ScanReport scan)
    {
        List<ProblemRow> rows =
        [
            .. scan.Problems.Select(problem => new ProblemRow(
                problem.Code,
                problem.Severity.ToString(),
                problem.Summary,
                problem.Path)),
        ];

        Problems = rows;
        ProblemsCount = rows.Count;
    }

    private async Task LoadQuarantineAsync(bool showSpinner, CancellationToken cancellationToken)
    {
        if (showSpinner)
        {
            IsQuarantineLoading = true;
        }

        try
        {
            IReadOnlyList<QuarantineItem> entries = await Task.Run(
                async () => await _services.Quarantine.ListAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken);

            List<QuarantineRow> rows =
            [
                .. entries
                    .OrderBy(item => item.Entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
                    .Select(item =>
                    {
                        QuarantineEntry entry = item.Entry;
                        return new QuarantineRow(
                            entry.EntryId,
                            $"{entry.Name} {entry.Version}",
                            FormatBytes(entry.SizeBytes),
                            entry.OriginalPath,
                            entry.QuarantinedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                            item.ContentPresent);
                    }),
            ];

            QuarantineEntries = rows;
            QuarantineCount = rows.Count;
            QuarantineSummary = rows.Count == 0
                ? "Nothing is quarantined yet. Items you move land here and can be restored."
                : string.Create(CultureInfo.InvariantCulture, $"{rows.Count} item(s) in quarantine · restore is hash-checked");
            if (SelectedQuarantine is not null && !rows.Any(row => row.Id == SelectedQuarantine.Id))
            {
                SelectedQuarantine = null;
            }
        }
        catch (OperationCanceledException)
        {
            // A replaced request is not an error.
        }
        catch (Exception ex)
        {
            QuarantineSummary = "Quarantine could not be read.";
            if (showSpinner)
            {
                SetStatus($"Quarantine list failed: {ex.Message}", StatusTone.Error);
            }
        }
        finally
        {
            IsQuarantineLoading = false;
        }
    }

    /// <summary>How many stored scans the history page loads. One window for the chart and the list.</summary>
    private const int HistoryLimit = 12;

    private async Task LoadHistoryAsync(bool showSpinner, CancellationToken cancellationToken)
    {
        if (showSpinner)
        {
            IsHistoryLoading = true;
        }

        try
        {
            SnapshotHistory history = await Task.Run(
                () => _services.Snapshots.LoadHistoryAsync(HistoryLimit, cancellationToken),
                cancellationToken);

            HistoryTrend trend = HistoryTrend.From(history);
            HistoryPoints = trend.Points;
            HistoryRows = trend.Recent;
            HistoryCount = trend.SnapshotCount;
            HasHistory = !trend.IsEmpty;
            HistorySummary = trend.Summary;
            HistoryLatest = trend.LatestSummary;
        }
        catch (OperationCanceledException)
        {
            // A replaced request is not an error.
        }
        catch (Exception ex)
        {
            HistorySummary = "History could not be read.";
            SetStatus($"History failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    private async Task WarmSecondaryStateAsync()
    {
        try
        {
            await LoadQuarantineAsync(showSpinner: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The quarantine count is a convenience at startup; never turn it into a startup failure.
            SetStatus($"Quarantine count unavailable: {ex.Message}", StatusTone.Warning);
        }
    }

    private async Task EnsureItemsLoadedAsync()
    {
        if (_itemsLoaded || IsItemsLoading || _snapshot is null || _preview is null)
        {
            return;
        }

        int generation = ++_itemsGeneration;
        IsItemsLoading = true;
        Snapshot snapshot = _snapshot;
        PlanPreview preview = _preview;

        try
        {
            List<ItemRow> rows = await Task.Run(() => BuildItemRows(snapshot, preview));

            if (generation != _itemsGeneration)
            {
                return;
            }

            Items = rows;
            _itemsLoaded = true;
            ItemsSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"{rows.Count} item(s) · {CurrentPolicy.PresetName} policy");
        }
        catch (Exception ex)
        {
            SetStatus($"Item list failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            if (generation == _itemsGeneration)
            {
                IsItemsLoading = false;
            }
        }
    }

    private static List<ItemRow> BuildItemRows(Snapshot snapshot, PlanPreview preview)
    {
        HashSet<string> proposed = preview.Plan is null
            ? []
            : [.. preview.Plan.Steps.Select(step => step.ItemId)];

        var rows = new List<ItemRow>(snapshot.Items.Count);
        foreach (Item item in snapshot.Items)
        {
            preview.Decisions.TryGetValue(item.ItemId, out RetentionDecision? decision);
            bool isProposed = proposed.Contains(item.ItemId);
            string verdict = isProposed ? "Propose" : decision?.AdviseOnly == true ? "Report only" : "Keep";

            rows.Add(new ItemRow(
                item.ItemId,
                item.Ecosystem,
                item.Name,
                item.Version,
                ItemUsage.Of(item).ToString(),
                item.Risk.ToString(),
                FormatBytes(item.Size),
                isProposed,
                verdict,
                decision?.Reason ?? "no decision"));
        }

        return rows;
    }

    private async Task RebuildPreviewAsync()
    {
        if (_snapshot is null)
        {
            ApplyPreview(null, clearOutcome: true);
            return;
        }

        int generation = ++_previewGeneration;
        IsPreviewBuilding = true;
        Snapshot snapshot = _snapshot;
        Policy policy = CurrentPolicy;

        try
        {
            PlanPreview preview = await Task.Run(() => PlanPreviewService.Preview(snapshot, policy, _services.Registry));

            if (generation != _previewGeneration)
            {
                return;
            }

            _preview = preview;
            ApplyPreview(preview, clearOutcome: true);
            InvalidateItems();
        }
        catch (Exception ex)
        {
            SetStatus($"Plan preview failed: {ex.Message}", StatusTone.Error);
        }
        finally
        {
            if (generation == _previewGeneration)
            {
                IsPreviewBuilding = false;
            }
        }
    }

    private void ApplyPreview(PlanPreview? preview, bool clearOutcome)
    {
        ClearPlanRows();
        _preview = preview;

        if (clearOutcome)
        {
            HasApplyOutcome = false;
            WasApplyCancelled = false;
        }

        PolicySummary = DescribePolicy(CurrentPolicy);
        IsApplyConfirmed = false;

        if (preview is null)
        {
            HasPlan = false;
            PlanSummary = "Scan first, then review the steps Coppice proposes.";
            PotentialReclaim = "0 B";
            RecalculateSelection();
            return;
        }

        if (preview.Plan is null)
        {
            HasPlan = false;
            PlanSummary = preview.Message;
            PotentialReclaim = "0 B";
            RecalculateSelection();
            return;
        }

        foreach (PlanStep step in preview.Plan.Steps
                     .OrderBy(step => _itemsById.GetValueOrDefault(step.ItemId)?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(step => _itemsById.GetValueOrDefault(step.ItemId)?.Version ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            Item? item = _itemsById.GetValueOrDefault(step.ItemId);
            string label = item is null ? step.ItemId : $"{item.Name} {item.Version}".Trim();
            var row = new PlanStepRow(step, label, FormatBytes(step.ExpectedBytes), step.Reason);
            row.PropertyChanged += OnStepPropertyChanged;
            PlanSteps.Add(row);
        }

        HasPlan = true;
        PotentialReclaim = FormatBytes(preview.Plan.ExpectedReclaimBytes);
        PlanSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{preview.Plan.Steps.Count} step(s) · {FormatBytes(preview.Plan.ExpectedReclaimBytes)} reclaimable · {CurrentPolicy.PresetName}");
        RecalculateSelection();
    }

    private void ClearPlanRows()
    {
        foreach (PlanStepRow row in PlanSteps)
        {
            row.PropertyChanged -= OnStepPropertyChanged;
        }

        PlanSteps.Clear();
        RecalculateSelection();
    }

    private void InvalidateItems()
    {
        _itemsLoaded = false;
        _itemsGeneration++;
        Items = [];
        ItemsSummary = HasSnapshot ? "Open this page to rebuild the list." : "No scan loaded.";

        if (CurrentPage == PageKey.Items)
        {
            _ = EnsureItemsLoadedAsync();
        }
    }

    private void MarkApplied(IReadOnlyList<PlanStep> applied)
    {
        HashSet<string> ids = [.. applied.Select(step => step.ItemId)];

        if (_itemsLoaded)
        {
            Items =
            [
                .. Items.Select(row => ids.Contains(row.ItemId)
                    ? row with
                    {
                        Proposed = false,
                        Decision = "Moved",
                        Reason = "moved to quarantine in this run; restore it from the Quarantine step",
                    }
                    : row),
            ];
        }
    }

    private void ApplySnapshotCounts()
    {
        if (_snapshot is null)
        {
            ItemCount = 0;
            ReferencedCount = 0;
            UnreferencedCount = 0;
            UnknownCount = 0;
            return;
        }

        AppUsageBreakdown usage = AppUsageBreakdown.FromItems(_snapshot.Items);
        ItemCount = usage.Total;
        ReferencedCount = usage.Referenced;
        UnreferencedCount = usage.Unreferenced;
        UnknownCount = usage.Unknown;

        if (!_itemsLoaded)
        {
            ItemsSummary = string.Create(CultureInfo.InvariantCulture, $"{usage.Total} item(s) ready to load.");
        }
    }

    private void OnStepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanStepRow.IsSelected))
        {
            RecalculateSelection();
        }
    }

    private void RecalculateSelection()
    {
        List<PlanStepRow> selected = [.. PlanSteps.Where(step => step.IsSelected)];
        SelectedStepCount = selected.Count;
        SelectedStepBytes = FormatBytes((ulong)selected.Sum(step => (decimal)step.Step.ExpectedBytes));
        ApplyCommand.NotifyCanExecuteChanged();
        ContinueToApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanShowApplyReady));
    }

    partial void OnSelectedPolicyChanged(PolicyOption value)
    {
        OnPropertyChanged(nameof(IsCustomPolicy));
        OnPropertyChanged(nameof(PolicySummary));
        _ = RebuildPreviewAsync();
    }

    partial void OnCustomKeepLatestChanged(int value)
    {
        if (IsCustomPolicy)
        {
            _ = RebuildPreviewAsync();
        }
    }

    partial void OnIsHelpOpenChanged(bool value) => CloseHelpCommand.NotifyCanExecuteChanged();

    private void SetStatus(string message, StatusTone tone)
    {
        Status = message;
        StatusTone = tone;
    }

    private static string DescribePolicy(Policy policy) =>
        policy.KeepLatestN == 0
            ? "Keep none: every eligible version is proposed."
            : string.Create(CultureInfo.InvariantCulture, $"Keep the newest {policy.KeepLatestN} version(s) of each item.");

    private static string[] ParseProjectRoots(string text) =>
        [.. text.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.ToString("F1", CultureInfo.InvariantCulture)} {units[unit]}");
    }

    private static string FormatBytes(decimal bytes) => FormatBytes((ulong)bytes);

    private sealed record ScanWork(
        DoctorReport Doctor,
        ScanReport Scan,
        Snapshot? Snapshot,
        Dictionary<string, Item> ItemsById,
        PlanPreview? Preview);
}

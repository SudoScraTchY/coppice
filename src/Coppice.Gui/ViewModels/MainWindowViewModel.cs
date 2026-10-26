using System.Collections.ObjectModel;
using System.Globalization;
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
using Item = Coppice.Core.Domain.Item;
using Policy = Coppice.Core.Domain.Policy;
using Problem = Coppice.Core.Domain.Problem;

namespace Coppice.Gui.ViewModels;

/// <summary>
/// The shell's single view model. It owns presentation state and commands; every question about a
/// cache, a plan or a mutation is answered by an application service.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly GuiServices _services;
    private Snapshot? _snapshot;

    public MainWindowViewModel(GuiServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;

        Policies = PolicyChoices.All;

        _selectedPolicy = Policies[0];
        _status = "No scan yet. Add project roots and scan, or open an existing snapshot.";
    }

    public ObservableCollection<ItemRow> Items { get; } = [];

    public ObservableCollection<PlanStepRow> PlanSteps { get; } = [];

    public ObservableCollection<RootRow> Roots { get; } = [];

    public ObservableCollection<ProblemRow> Problems { get; } = [];

    public ObservableCollection<QuarantineRow> QuarantineEntries { get; } = [];

    public ObservableCollection<string> ApplyLog { get; } = [];

    public IReadOnlyList<PolicyOption> Policies { get; }

    [ObservableProperty]
    private PolicyOption _selectedPolicy;

    [ObservableProperty]
    private int _customKeepLatest = 1;

    [ObservableProperty]
    private string _projectRoots = string.Empty;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _scanSummary = "No snapshot loaded.";

    [ObservableProperty]
    private string _planSummary = "Build a plan to review its steps.";

    [ObservableProperty]
    private string _policySummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isApplyConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    private bool _isRestoreConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasPlan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    private QuarantineRow? _selectedQuarantine;

    [ObservableProperty]
    private int _selectedStepCount;

    [ObservableProperty]
    private string _selectedStepBytes = "0 B";

    [ObservableProperty]
    private double _applyPercent;

    public bool IsCustomPolicy => SelectedPolicy?.Name.StartsWith("Custom", StringComparison.OrdinalIgnoreCase) == true;

    private Policy CurrentPolicy =>
        IsCustomPolicy
            ? Policy.Default with { PresetName = "custom", KeepLatestN = Math.Clamp(CustomKeepLatest, 0, 1000) }
            : SelectedPolicy?.Policy ?? Policy.Default;

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            _snapshot = await _services.Snapshots.LoadLatestAsync();
            if (_snapshot is null)
            {
                Status = "No stored snapshot found. Add project roots and scan.";
            }
            else
            {
                RebuildPreview();
                Status = $"Loaded snapshot {_snapshot.SnapshotId} with {_snapshot.Items.Count} item(s).";
                ScanSummary = $"{_snapshot.Items.Count} item(s) · {_snapshot.Ecosystems.Count} ecosystem(s) · snapshot {_snapshot.SnapshotId}";
            }

            await RefreshQuarantineAsync(CancellationToken.None);
            await RefreshRootsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"Startup failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        Status = "Scanning...";
        ApplyLog.Clear();
        ApplyPercent = 0;
        try
        {
            string[] projects = ParseProjectRoots(ProjectRoots);

            (DoctorReport doctor, ScanReport scan) = await Task.Run(
                async () =>
                {
                    DoctorReport report = _services.Doctor.Inspect();
                    ScanReport result = await _services.Scan
                        .RunAsync(ScanWiring.FromDoctor(report, _services.Registry), projects, cancellationToken)
                        .ConfigureAwait(false);
                    return (report, result);
                },
                cancellationToken);

            LoadRoots(doctor);
            LoadProblems(scan);
            _snapshot = await _services.Snapshots.LoadLatestAsync(cancellationToken);

            if (_snapshot is null)
            {
                Status = "The scan completed but its snapshot could not be loaded.";
                return;
            }

            RebuildPreview();
            ScanSummary = $"{scan.Items.Count} item(s) · {scan.ProjectCount} project(s) · {scan.Ecosystems.Count} ecosystem(s) · {scan.Usage.Referenced} referenced";
            Status = $"Scan complete. Snapshot {scan.SnapshotId}.";
            await RefreshQuarantineAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled. Nothing was changed.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
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

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is null)
        {
            Status = "There is no snapshot to apply against.";
            return;
        }

        List<PlanStep> selected = [.. PlanSteps.Where(step => step.IsSelected).Select(step => step.Step)];
        if (selected.Count == 0)
        {
            Status = "Select at least one step.";
            return;
        }

        PlanResult built = PlanBuilder.Build(_snapshot.SnapshotId, CurrentPolicy, selected);
        if (!built.IsSuccess)
        {
            Status = built.Message ?? "The selected steps could not be built into a plan.";
            return;
        }

        IsBusy = true;
        IsApplyConfirmed = false;
        ApplyLog.Clear();
        ApplyPercent = 0;
        Status = "Applying selected steps...";

        try
        {
            var progress = new Progress<ApplyStepProgress>(step =>
            {
                ApplyPercent = step.Total == 0 ? 0 : (double)step.Index / step.Total;
                ApplyLog.Add($"{step.Index}/{step.Total}  {step.StepId}");
            });

            PlanApplyOutcome outcome = await Task.Run(
                () => _services.Apply.ApplyAsync(built.Plan!, strict: false, resume: false, confirmed: true, progress, cancellationToken),
                cancellationToken);

            if (!outcome.Prepared)
            {
                Status = outcome.Message;
                return;
            }

            ApplySummary summary = outcome.Summary!;
            ApplyPercent = 1;
            ApplyLog.Add($"{summary.Ok} moved · {summary.Failed} failed · {summary.SkippedUnsafe} skipped-unsafe");
            Status = string.IsNullOrWhiteSpace(summary.Message)
                ? $"{summary.Ok} item(s) moved to quarantine. Nothing was deleted."
                : summary.Message;

            MarkApplied(selected);
            PlanSummary = summary.Ok == 0
                ? "The run moved nothing. Rescan before trying again."
                : $"{summary.Ok} item(s) moved to quarantine. Rescan to build a fresh plan.";
            HasPlan = false;
            PlanSteps.Clear();
            RecalculateSelection();
            await RefreshQuarantineAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "Apply cancelled between steps. The journal is resumable.";
        }
        catch (Exception ex)
        {
            Status = $"Apply failed: {ex.Message}";
        }
        finally
        {
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

        IsBusy = true;
        IsRestoreConfirmed = false;
        try
        {
            RestoreResult result = await Task.Run(
                () => _services.Quarantine.RestoreAsync(SelectedQuarantine.Id, cancellationToken),
                cancellationToken);

            Status = result.Message;
            await RefreshQuarantineAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "Restore cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRestore() => !IsBusy && IsRestoreConfirmed && SelectedQuarantine is not null;

    [RelayCommand]
    private async Task RefreshQuarantineAsync() => await RefreshQuarantineAsync(CancellationToken.None);

    private async Task RefreshQuarantineAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QuarantineItem> entries = await _services.Quarantine.ListAsync(cancellationToken);

        QuarantineEntries.Clear();
        foreach (QuarantineItem item in entries.OrderBy(item => item.Entry.OriginalPath, StringComparer.OrdinalIgnoreCase))
        {
            QuarantineEntry entry = item.Entry;
            QuarantineEntries.Add(new QuarantineRow(
                entry.EntryId,
                $"{entry.Name} {entry.Version}",
                FormatBytes(entry.SizeBytes),
                entry.OriginalPath,
                entry.QuarantinedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                item.ContentPresent));
        }
    }

    private void MarkApplied(IReadOnlyList<PlanStep> applied)
    {
        HashSet<string> ids = [.. applied.Select(step => step.ItemId)];

        for (int i = 0; i < Items.Count; i++)
        {
            if (ids.Contains(Items[i].ItemId))
            {
                Items[i] = Items[i] with
                {
                    Proposed = false,
                    Decision = "Moved",
                    Reason = "moved to quarantine in this run; restore it from the Quarantine tab",
                };
            }
        }
    }

    private async Task RefreshRootsAsync(CancellationToken cancellationToken)
    {
        DoctorReport report = await Task.Run(() => _services.Doctor.Inspect(), cancellationToken);
        LoadRoots(report);
    }

    private void LoadRoots(DoctorReport report)
    {
        Roots.Clear();
        foreach (RootReport root in report.Roots)
        {
            Roots.Add(new RootRow(
                root.LocationId,
                root.Root.RealPath,
                root.Root.Via.ToString(),
                root.Root.Role.ToString(),
                root.Root.Validity.ToString(),
                root.Root.IsCleanable,
                root.Problem ?? string.Empty));
        }
    }

    private void LoadProblems(ScanReport scan)
    {
        Problems.Clear();
        foreach (Problem problem in scan.Problems)
        {
            Problems.Add(new ProblemRow(
                problem.Code,
                problem.Severity.ToString(),
                problem.Summary,
                problem.Path));
        }
    }

    private void RebuildPreview()
    {
        if (_snapshot is null)
        {
            return;
        }

        PlanPreview preview = PlanPreviewService.Preview(_snapshot, CurrentPolicy, _services.Registry);

        Dictionary<string, Item> itemsById = _snapshot.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);

        Items.Clear();
        foreach (Item item in _snapshot.Items)
        {
            preview.Decisions.TryGetValue(item.ItemId, out RetentionDecision? decision);
            bool proposed = preview.Plan?.Steps.Any(step => step.ItemId == item.ItemId) == true;
            string verdict = proposed ? "Propose" : decision?.AdviseOnly == true ? "Report only" : "Keep";

            Items.Add(new ItemRow(
                item.ItemId,
                item.Ecosystem,
                item.Name,
                item.Version,
                ItemUsage.Of(item).ToString(),
                item.Risk.ToString(),
                FormatBytes(item.Size),
                proposed,
                verdict,
                decision?.Reason ?? "no decision"));
        }

        foreach (PlanStepRow row in PlanSteps)
        {
            row.PropertyChanged -= OnStepPropertyChanged;
        }

        PlanSteps.Clear();
        HasPlan = preview.Plan is not null;

        if (preview.Plan is null)
        {
            PlanSummary = preview.Message;
            PolicySummary = DescribePolicy(CurrentPolicy);
            RecalculateSelection();
            return;
        }

        foreach (PlanStep step in preview.Plan.Steps
                     .OrderBy(step => itemsById.GetValueOrDefault(step.ItemId)?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(step => itemsById.GetValueOrDefault(step.ItemId)?.Version ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            Item? item = itemsById.GetValueOrDefault(step.ItemId);
            string label = item is null ? step.ItemId : $"{item.Name} {item.Version}".Trim();
            var row = new PlanStepRow(
                step,
                label,
                FormatBytes(step.ExpectedBytes),
                step.Reason);
            row.PropertyChanged += OnStepPropertyChanged;
            PlanSteps.Add(row);
        }

        PlanSummary = $"{preview.Plan.Steps.Count} step(s) · {FormatBytes(preview.Plan.ExpectedReclaimBytes)} · {CurrentPolicy.PresetName}";
        PolicySummary = DescribePolicy(CurrentPolicy);
        RecalculateSelection();
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
    }

    partial void OnSelectedPolicyChanged(PolicyOption value)
    {
        OnPropertyChanged(nameof(IsCustomPolicy));
        RebuildPreview();
    }

    partial void OnCustomKeepLatestChanged(int value)
    {
        if (IsCustomPolicy)
        {
            RebuildPreview();
        }
    }

    private static string DescribePolicy(Policy policy) =>
        policy.KeepLatestN == 0
            ? "Keep none: every eligible version is proposed."
            : $"Keep the newest {policy.KeepLatestN} version(s) of each package.";

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
}

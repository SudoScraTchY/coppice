using CommunityToolkit.Mvvm.ComponentModel;
using Coppice.Core.Domain;

namespace Coppice.Gui.ViewModels;

/// <summary>One retention choice. The policy carried here is the same object the CLI and TUI use.</summary>
public sealed record PolicyOption(string Name, string Detail, Policy Policy)
{
    public override string ToString() => Name;
}

public sealed record ItemRow(
    string ItemId,
    string Ecosystem,
    string Name,
    string Version,
    string Usage,
    string Risk,
    string Size,
    bool Proposed,
    string Decision,
    string Reason)
{
    public bool IsUnknown => Usage == "Unknown";

    public bool IsReferenced => Usage == "Referenced";

    public override string ToString() => $"{Name} {Version} — {Usage} — {Decision}";
}

/// <summary>One proposed step with a two-way selection state for the review screen.</summary>
public sealed partial class PlanStepRow : ObservableObject
{
    public PlanStepRow(PlanStep step, string label, string size, string reason)
    {
        Step = step;
        Label = label;
        Size = size;
        Reason = reason;
    }

    public PlanStep Step { get; }

    public string Label { get; }

    public string Size { get; }

    public string Reason { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    public override string ToString() => Label;
}

public sealed record RootRow(
    string LocationId,
    string Path,
    string Via,
    string Role,
    string Validity,
    bool Cleanable,
    string Problem)
{
    public override string ToString() => $"{LocationId} — {Validity} — {Path}";
}

public sealed record ProblemRow(string Code, string Severity, string Summary, string? Path)
{
    public bool IsWarning => Severity == "Warning";

    public bool IsError => Severity == "Error";

    public override string ToString() => $"{Code} — {Severity} — {Summary}";
}

public sealed record QuarantineRow(
    string Id,
    string Label,
    string Size,
    string OriginalPath,
    string QuarantinedAt,
    bool ContentPresent)
{
    public string ContentState => ContentPresent ? "present" : "missing";

    public bool IsPresent => ContentPresent;

    public override string ToString() => $"{Label} — {Size} — {ContentState}";
}

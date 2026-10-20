using System.Globalization;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Core.Apply;

/// <summary>
/// A live, re-resolved root an apply may act under, with what the profile says about it.
/// <see cref="AllowlistPattern"/> is the entry shape a path delete must match; null means none may.
/// </summary>
public sealed record BindingRoot(
    string LocationId,
    string RealPath,
    bool Cleanable,
    bool InstallerOwned,
    string? AllowlistPattern);

/// <summary>One plan step after binding: a gateway step to run, or the reason there is none.</summary>
public sealed record BoundStep(PlanStep PlanStep, Item? Item, GatewayStep? GatewayStep, GatewayRefusal Refusal, string Message)
{
    public bool IsBound => GatewayStep is not null;

    public bool IsReportOnly => PlanStep.Action.Kind == RemovalKind.ReportOnly;
}

/// <summary>
/// Turns a validated plan into gateway steps against the machine as it is NOW (T-034, S-1).
/// <para>
/// The plan names items; it does not get to name roots. Each step's root is the live root the resolver
/// found for the item's location this run — so a plan written on another machine, or before a cache moved,
/// binds to nothing rather than to a path it merely asserts. Likewise the path to remove comes from the
/// snapshot item the plan was justified against, and a plan whose action disagrees with that item is
/// refused as invalid instead of trusted.
/// </para>
/// </summary>
public static class PlanBinder
{
    /// <summary>The snapshot fact the drift check compares against.</summary>
    public const string LastWriteFact = "lastWriteUtc";

    public static IReadOnlyList<BoundStep> Bind(Plan plan, Snapshot snapshot, IReadOnlyList<BindingRoot> roots, OperatingSystemKind os, bool strict)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(roots);

        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (Item item in snapshot.Items)
        {
            items.TryAdd(item.ItemId, item);
        }

        return [.. plan.Steps.Select(step => BindOne(step, plan.PlanId, items, roots, os, strict))];
    }

    private static BoundStep BindOne(PlanStep step, string planId, Dictionary<string, Item> items, IReadOnlyList<BindingRoot> roots, OperatingSystemKind os, bool strict)
    {
        if (!items.TryGetValue(step.ItemId, out Item? item))
        {
            return Refuse(step, null, GatewayRefusal.PlanInvalid,
                $"Step '{step.ItemId}' names an item that is not in the plan's snapshot. Re-create the plan.");
        }

        if (step.Action.Kind == RemovalKind.ReportOnly)
        {
            return Refuse(step, item, GatewayRefusal.None, step.Action.RouteNote ?? "Report-only: nothing to do.");
        }

        StringComparison comparison = os == OperatingSystemKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        BindingRoot? root = roots
            .Where(r => string.Equals(r.LocationId, item.LocationId, StringComparison.Ordinal) && IsUnder(item.Path, r.RealPath, comparison))
            .OrderByDescending(r => r.RealPath.Length)
            .FirstOrDefault();

        if (root is null)
        {
            return Refuse(step, item, GatewayRefusal.PlanInvalid,
                $"No root resolved this run for '{item.LocationId}' contains '{item.Path}'. The cache moved or is gone; rescan.");
        }

        if (!root.Cleanable)
        {
            return Refuse(step, item, GatewayRefusal.PlanInvalid,
                $"The root '{root.RealPath}' did not validate this run, so nothing under it is touched.");
        }

        if (step.Action.Kind == RemovalKind.PathDelete)
        {
            if (!string.Equals(step.Action.CanonicalPath, item.Path, comparison))
            {
                return Refuse(step, item, GatewayRefusal.PlanInvalid,
                    $"Step '{step.ItemId}' would remove '{step.Action.CanonicalPath}', but its snapshot item is '{item.Path}'.");
            }

            if (string.IsNullOrEmpty(root.AllowlistPattern))
            {
                return Refuse(step, item, GatewayRefusal.NotAllowlisted,
                    $"'{item.LocationId}' has no allowlist pattern, so no path under it may be removed.");
            }
        }

        DateTimeOffset lastWrite = DateTimeOffset.MinValue;
        bool hasLastWrite = item.Facts.TryGetValue(LastWriteFact, out string? raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastWrite);

        if (strict && step.Action.Kind == RemovalKind.PathDelete && !hasLastWrite)
        {
            return Refuse(step, item, GatewayRefusal.PlanInvalid,
                $"--strict needs the item's recorded mtime, and the snapshot behind this plan has none. Rescan and re-plan.");
        }

        var gatewayStep = new GatewayStep
        {
            ItemId = step.ItemId,
            Path = item.Path,
            Root = root.RealPath,
            AllowlistPattern = root.AllowlistPattern ?? string.Empty,
            Action = step.Action,
            Risk = step.Risk,
            Fingerprint = new SnapshotFingerprint(item.Path, item.Size, hasLastWrite ? lastWrite : DateTimeOffset.MinValue),
            InstallerOwned = root.InstallerOwned,
            Ecosystem = item.Ecosystem,
            Kind = item.Kind,
            Name = item.Name,
            Version = item.Version,
            PlanId = planId,
        };

        return new BoundStep(step, item, gatewayStep, GatewayRefusal.None, string.Empty);
    }

    private static bool IsUnder(string path, string root, StringComparison comparison)
    {
        string trimmed = root.TrimEnd('/', '\\');
        return path.Length > trimmed.Length + 1
            && path.StartsWith(trimmed, comparison)
            && (path[trimmed.Length] == '/' || path[trimmed.Length] == '\\');
    }

    private static BoundStep Refuse(PlanStep step, Item? item, GatewayRefusal refusal, string message) =>
        new(step, item, null, refusal, message);
}

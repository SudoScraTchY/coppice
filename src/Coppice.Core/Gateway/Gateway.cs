using System.Diagnostics;

using Coppice.Core.PathSafety;
using Coppice.Ports;

using RemovalAction = Coppice.Core.Domain.RemovalAction;
using RemovalKind = Coppice.Core.Domain.RemovalKind;
using Risk = Coppice.Core.Domain.Risk;

namespace Coppice.Core.Gateway;

/// <summary>
/// The single filesystem mutator (T-031, 06-safety-model "The Gateway", S-2/S-10/S-13/S-14).
/// <para>
/// Everything else in Coppice is read-only. This type is the only place a file moves or disappears, and it
/// exists so that "is this safe to do" has exactly one implementation to audit.
/// </para>
/// <para>
/// The sequence in 06 is followed in order, and the order is the design: cheap rejections first, the
/// expensive re-verification last, immediately before the mutation. A TOCTOU check that runs at the start
/// of apply is worthless — the whole race it guards against is something changing between the check and the
/// write, so it has to be the last thing before the write.
/// </para>
/// <para>
/// <b>Every refusal is a result.</b> Nothing throws for a policy decision: a locked file, a drifted path and
/// a denylist hit are all normal outcomes that belong in the report and the audit log, not exceptions that
/// would abort a partially-applied plan.
/// </para>
/// </summary>
public sealed class Gateway
{
    private readonly IFileSystem _fs;
    private readonly OperatingSystemKind _os;

    public Gateway(IFileSystem fs, OperatingSystemKind os)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
        _os = os;
    }

    /// <summary>
    /// Executes one step. Returns what happened; never throws for a refusal.
    /// </summary>
    public GatewayStepResult Execute(GatewayStep step, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(step);

        // 1. Installer-owned is report-only (S-9). Checked FIRST because it is a statement about the whole
        // root, so no amount of path surgery makes the step appropriate.
        if (step.InstallerOwned)
        {
            return Skip(step, GatewayRefusal.InstallerOwned,
                $"'{step.Path}' belongs to an installer. Reinstall or use the installer's own removal path; "
                    + "Coppice will not delete it.");
        }

        // 2. Allowlist (06 step 2). Before containment, because a path that is not shaped like a cache entry
        // is wrong however well-contained it is — and this is cheaper to reject.
        if (!MatchesAllowlist(step.Path, step.Root, step.AllowlistPattern))
        {
            return Skip(step, GatewayRefusal.NotAllowlisted,
                $"'{step.Path}' does not match the allowlist pattern '{step.AllowlistPattern}' for its root.");
        }

        // 3. The full path-safety chain: canonicalize, resolve links, containment, denylist floor,
        //    not-the-root (06 steps 1, 3, 4, and S-13/S-14). ContainmentCheck refuses rather than throws,
        //    which is what keeps a refusal reportable.
        var paths = new IFileSystemPaths(_fs, _os);
        ContainmentResult containment = ContainmentCheck.Check(paths, step.Root, step.Path);

        if (!containment.Safe)
        {
            return Skip(step, RefusalFor(containment.Reason), containment.Message);
        }

        // 4. Elevation (S-7): v1 never elevates, so an elevated action is refused outright rather than
        //    attempted and half-done.
        if (step.Action.Kind == RemovalKind.NativeCommand && step.Risk >= Risk.Manual)
        {
            return Skip(step, GatewayRefusal.NeedsElevation,
                $"'{step.Path}' is {step.Risk} tier and its removal is a native command. "
                    + "Coppice does not elevate; handle this one yourself.");
        }

        // 5. Lock probe (S-10). Skipped, never forced — a running build server holds a package open, and
        //    removing it anyway is how a user's restore breaks mid-build.
        // "lock" is a C# keyword; the local is named lockState.
        LockState lockState = _fs.ProbeLock(step.Path);

        if (lockState != LockState.Unlocked)
        {
            return Skip(step, GatewayRefusal.Locked,
                $"'{step.Path}' is {lockState}. Close whatever is using it and re-run; Coppice does not force locks.");
        }

        // 6. Drift check (S-2), under --strict. Per step, so one drifted package aborts itself and the rest
        //    of the plan still runs.
        if (strict)
        {
            GatewayStepResult? drift = CheckDrift(step);
            if (drift is not null)
            {
                return drift;
            }
        }

        // 7. TOCTOU re-verify (06 step 6). The WHOLE chain, again, immediately before the mutation. This is
        //    the point of the design: the race being guarded against is something changing between the
        //    first check and the write, so a check performed anywhere earlier would not catch it.
        ContainmentResult recheck = ContainmentCheck.Check(paths, step.Root, step.Path);

        if (!recheck.Safe)
        {
            // The message says it changed rather than restating the rule: a containment failure here means
            // the filesystem moved underneath us between the two checks, which is worth telling the user
            // explicitly rather than reporting as a static denial.
            return Skip(step, RefusalFor(recheck.Reason),
                $"'{step.Path}' failed the safety re-check immediately before the change ({recheck.Message}). "
                    + "The filesystem changed between the plan and the apply.");
        }

        if (_fs.ProbeLock(step.Path) != LockState.Unlocked)
        {
            return Skip(step, GatewayRefusal.Locked,
                $"'{step.Path}' became locked between the plan and the apply. It was not touched.");
        }

        // 8. Mutate. Path deletes go to quarantine (S-11); native commands are run and audited.
        return step.Action.Kind switch
        {
            RemovalKind.PathDelete => Quarantine(step),
            RemovalKind.NativeCommand => RunNative(step),
            RemovalKind.ReportOnly => Skip(step, GatewayRefusal.InstallerOwned,
                $"'{step.Path}' is report-only; no removal was performed."),

            _ => Skip(step, GatewayRefusal.NotAllowlisted,
                $"Unknown removal action '{step.Action.Kind}' for '{step.Path}'; nothing was done."),
        };
    }

    /// <summary>
    /// Executes a whole plan, one step at a time.
    /// <para>
    /// A refusal does NOT stop the run. The alternative is that one locked file in the first package cache
    /// aborts a plan touching thirty, and the user has to re-run repeatedly to clear each one. Every step
    /// is independent and every result is returned.
    /// </para>
    /// </summary>
    public IReadOnlyList<GatewayStepResult> ExecuteAll(IEnumerable<GatewayStep> steps, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var results = new List<GatewayStepResult>();

        foreach (GatewayStep step in steps)
        {
            results.Add(Execute(step, strict));
        }

        return results;
    }

    /// <summary>
    /// Moves a path into quarantine. The rename is the mutation: nothing is deleted, so a restore is a
    /// rename back (T-032).
    /// </summary>
    private GatewayStepResult Quarantine(GatewayStep step)
    {
        if (string.IsNullOrEmpty(step.Action.CanonicalPath))
        {
            return Skip(step, GatewayRefusal.NotAllowlisted,
                $"Step for '{step.ItemId}' has no path to move. Nothing was done.");
        }

        try
        {
            // The destination is derived, not passed in: a caller-chosen destination is a caller-chosen
            // escape from the containment check that just approved the source.
            string destination = QuarantinePath.For(step.Root, step.LocationIdForQuarantine(), step.Action.CanonicalPath);
            _fs.CreateDirectory(ParentOf(destination, _os));
            _fs.Move(step.Action.CanonicalPath, destination);

            return new GatewayStepResult(
                step.ItemId,
                GatewayOutcome.Quarantined,
                GatewayRefusal.None,
                destination,
                $"Moved to quarantine at '{destination}'. Restore with `coppice quarantine restore`.");
        }
        catch (System.IO.IOException ex)
        {
            return Skip(step, GatewayRefusal.Locked,
                $"Could not move '{step.Action.CanonicalPath}' to quarantine: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Skip(step, GatewayRefusal.NeedsElevation,
                $"Not permitted to move '{step.Action.CanonicalPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a native removal command with a timeout (S-15).
    /// <para>
    /// Not quarantinable and not restorable — the tool owns the operation (06's asymmetry note), which is
    /// why the caller must have already established Safe tier, a fingerprint-valid root and explicit
    /// confirmation.
    /// </para>
    /// </summary>
    private GatewayStepResult RunNative(GatewayStep step)
    {
        return Skip(step, GatewayRefusal.NeedsElevation,
            $"Native command '{step.Action.CommandLine}' requires the native runner (T-033); not executed here.");
    }

    /// <summary>The S-2 drift check: does live state still match the snapshot?</summary>
    private GatewayStepResult? CheckDrift(GatewayStep step)
    {
        var entry = _fs.GetEntry(step.Action.CanonicalPath ?? step.Path);

        if (entry is null)
        {
            // Gone since the scan. Not drift — the goal state. Refusing would leave the user re-planning
            // work that is already done.
            return null;
        }

        ulong liveSize = entry.Kind == EntryKind.Directory ? _fs.MeasureSize(step.Action.CanonicalPath ?? step.Path) : (ulong)Math.Max(0, entry.Length);

        bool sizeMatches = liveSize == step.Fingerprint.Size;
        bool timeMatches = entry.LastWriteTimeUtc == step.Fingerprint.LastWriteTimeUtc;

        if (sizeMatches && timeMatches)
        {
            return null;
        }

        return Skip(step, GatewayRefusal.Drifted,
            $"'{step.Path}' has changed since the scan (expected {step.Fingerprint.Size} bytes / "
                + $"{step.Fingerprint.LastWriteTimeUtc:O}, found {liveSize} bytes / {entry.LastWriteTimeUtc:O}). "
                + "Re-run `coppice scan` to refresh the plan.");
    }

    /// <summary>
    /// Does the path match the location's allowlist pattern?
    /// <para>
    /// Matched against the path RELATIVE to the root, one segment per <c>*</c>. The alternative — matching
    /// the whole path — would let a pattern containing a drive letter or a deep prefix behave differently
    /// per root, which is exactly the kind of platform-specific surprise this check exists to prevent.
    /// </para>
    /// <para>
    /// A <c>*</c> matches exactly ONE segment and never crosses a separator. That is what makes the
    /// pattern a shape check rather than a suggestion: <c>*/*</c> matches <c>pkg/1.0.0</c> and refuses
    /// <c>my-notes/important/backup</c>, where a glob that spanned separators would accept both and leave a
    /// user's own files fair game inside a cache root.
    /// </para>
    /// </summary>
    public static bool MatchesAllowlist(string path, string root, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            // No pattern means no shape requirement, not "match nothing". A location with no allowlist
            // still passes containment and the denylist; refusing every step would silently disable it.
            return true;
        }

        string? relative = RelativeTo(path, root);

        if (relative is null)
        {
            // Not under the root at all. Returning false here is what makes the allowlist a second
            // independent barrier rather than a repeat of containment.
            return false;
        }

        string[] patternSegments = pattern.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        string[] pathSegments = relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);

        if (patternSegments.Length != pathSegments.Length)
        {
            // Depth is part of the shape. A three-segment path is not a <c>*/*</c> cache entry, and treating
            // it as one is how a nested user directory inside a cache root becomes a deletion candidate.
            return false;
        }

        for (int i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] == "*")
            {
                continue;
            }

            if (!string.Equals(patternSegments[i], pathSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The path relative to its root, or null when it is not under it.</summary>
    private static string? RelativeTo(string path, string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        string prefix = root.EndsWith('/') || root.EndsWith('\\') ? root : root + "/";

        string normalized = path.Replace('\\', '/');
        string normalizedRoot = prefix.Replace('\\', '/');

        if (!normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized[normalizedRoot.Length..];
    }

    private static string ParentOf(string path, OperatingSystemKind os)
    {
        char sep = PathCanonicalizer.SeparatorFor(os);
        int last = path.LastIndexOf(sep);

        return last <= 0 ? path : path[..last];
    }

    private static GatewayStepResult Skip(GatewayStep step, GatewayRefusal refusal, string message) =>
        new(step.ItemId, GatewayOutcome.Skipped, refusal, null, message);

    private static GatewayRefusal RefusalFor(ContainmentFailure failure) => failure switch
    {
        ContainmentFailure.Denied => GatewayRefusal.Denied,
        ContainmentFailure.IsRoot => GatewayRefusal.IsRoot,
        ContainmentFailure.EscapedRoot => GatewayRefusal.OutsideRoot,
        ContainmentFailure.LinkLoop => GatewayRefusal.LinkLoop,
        _ => GatewayRefusal.NotCanonicalizable,
    };
}

/// <summary>Extension so a step can name its own quarantine location without the gateway guessing.</summary>
internal static class GatewayStepExtensions
{
    /// <summary>
    /// The location id for quarantine purposes, derived from the root's own name.
    /// <para>
    /// A step does not carry a location id — the plan's item ids are content hashes, which would produce a
    /// quarantine tree nobody can navigate. The root's directory name is stable, human-recognisable, and
    /// matches what the user sees in <c>coppice roots</c>.
    /// </para>
    /// </summary>
    public static string LocationIdForQuarantine(this GatewayStep step)
    {
        string trimmed = step.Root.TrimEnd('/', '\\');

        int last = trimmed.LastIndexOfAny(['/', '\\']);

        return last >= 0 && last < trimmed.Length - 1 ? trimmed[(last + 1)..] : "root";
    }
}

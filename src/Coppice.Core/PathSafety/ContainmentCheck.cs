using Coppice.Ports;

namespace Coppice.Core.PathSafety;

/// <summary>
/// Reason why the gateway refused a path during the containment/check step.
/// </summary>
public enum ContainmentFailure
{
    /// <summary>No failure — path is safe.</summary>
    None = 0,

    /// <summary>Path could not be canonicalized.</summary>
    CanonicalizationFailed,

    /// <summary>Path is denied by the denylist.</summary>
    Denied,

    /// <summary>Resolved path escapes the validated root (symlink/junction escape).</summary>
    EscapedRoot,

    /// <summary>Path is the root itself (children only are allowed).</summary>
    IsRoot,

    /// <summary>Path still contains unresolved relative segments.</summary>
    HasRelativeSegments,

    /// <summary>Root path is invalid or empty.</summary>
    InvalidRoot,
}

/// <summary>
/// Result of a gateway containment check (steps 1+2+4 of the safety model).
/// </summary>
public sealed record ContainmentResult(bool Safe, ContainmentFailure Reason, string Message)
{
    public static ContainmentResult Ok() => new(true, ContainmentFailure.None, string.Empty);

    public static ContainmentResult Fail(ContainmentFailure reason, string message) => new(false, reason, message);
}

/// <summary>
/// Gateway steps 1+2+4: canonicalize, resolve links, verify containment within a validated root,
/// and refuse a path equal to the root itself. Returns a rich result explaining any refusal.
/// </summary>
public static class ContainmentCheck
{
    /// <summary>
    /// Runs the full containment check pipeline.
    /// </summary>
    /// <param name="paths">Wrapper over the filesystem providing canonicalization and resolution.</param>
    /// <param name="root">The validated root directory. Must be an absolute, canonical path.</param>
    /// <param name="candidate">The path to check, as provided by an untrusted source (e.g., a plugin).</param>
    public static ContainmentResult Check(IFileSystemPaths paths, string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return ContainmentResult.Fail(ContainmentFailure.InvalidRoot, "Root is empty or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return ContainmentResult.Fail(ContainmentFailure.CanonicalizationFailed, "Candidate is empty or whitespace.");
        }

        // Step 1: Canonicalize the candidate.
        var canonical = paths.Canonicalize(candidate);
        if (!canonical.Ok)
        {
            return ContainmentResult.Fail(ContainmentFailure.CanonicalizationFailed, canonical.Message);
        }

        // Step 2: Check the denylist floor.
        if (Denylist.IsDenied(canonical.Path, paths.OS))
        {
            var deniedBy = Denylist.WhichDenied(canonical.Path, paths.OS);
            return ContainmentResult.Fail(ContainmentFailure.Denied,
                $"Path is on the denylist{(deniedBy is not null ? $" (matched '{deniedBy}')" : string.Empty)}.");
        }

        // Step 3a: Refuse the root itself BEFORE the containment test. IsWithin deliberately reports
        // false for a path equal to the root, so checking containment first would report the same
        // refusal as an escape and the caller could never tell "you named the root" from "you tried
        // to leave it" — two very different diagnoses for a report to show the user.
        if (PathCanonicalizer.PathsEqual(canonical.Path, root, paths.OS))
        {
            return ContainmentResult.Fail(ContainmentFailure.IsRoot,
                $"Path equals the root '{root}'; only children are allowed.");
        }

        // Step 3b: Resolve any symlinks/junctions.
        var resolved = paths.ResolveRealPath(canonical.Path);

        // Step 3c: The same root check again, on the RESOLVED path: a link inside the root may
        // point at the root itself.
        if (PathCanonicalizer.PathsEqual(resolved, root, paths.OS))
        {
            return ContainmentResult.Fail(ContainmentFailure.IsRoot,
                $"Path resolves to the root '{root}'; only children are allowed.");
        }

        // Step 4: Verify containment within root.
        if (!paths.IsWithin(root, resolved))
        {
            return ContainmentResult.Fail(ContainmentFailure.EscapedRoot,
                $"Resolved path '{resolved}' is outside root '{root}'.");
        }

        // Step 4d: Reject any remaining relative segments.
        if (paths.HasRelativeSegments(resolved))
        {
            return ContainmentResult.Fail(ContainmentFailure.HasRelativeSegments,
                $"Resolved path still contains relative segments.");
        }

        return ContainmentResult.Ok();
    }

    /// <summary>
    /// Convenience overload that takes an <see cref="IFileSystem"/> directly.
    /// </summary>
    public static ContainmentResult Check(IFileSystem fs, OperatingSystemKind os, string root, string candidate) =>
        Check(new IFileSystemPaths(fs, os), root, candidate);
}

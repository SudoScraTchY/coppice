using Coppice.Core.Domain;
using Coppice.Core.PathSafety;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;

namespace Coppice.Core.Resolution;

/// <summary>Why a resolved root is not cleanable, beyond the validity gates (05).</summary>
public sealed record ResolutionIssue(string LocationId, string Code, string Summary, string? RawValue);

/// <summary>
/// The result of resolving one location: every candidate that survived canonicalization, in a
/// deterministic order, plus provenance and any problems worth showing the user.
/// </summary>
public sealed record ResolutionOutcome(
    string LocationId,
    IReadOnlyList<ResolvedRoot> Roots,
    IReadOnlyList<ResolutionIssue> Issues)
{
    /// <summary>The root a first-match location resolved to, or null when nothing resolved.</summary>
    public ResolvedRoot? Active => Roots.FirstOrDefault(r => r.Role == CoreDomain.RootRole.Active);
}

/// <summary>
/// The resolution engine (05, T-005).
/// <para>
/// Every path a source returns is UNTRUSTED INPUT. A poisoned tool can answer <c>/</c> or the home
/// directory, so nothing is ever taken at face value: candidates are canonicalized, de-duplicated by
/// real path, and gated. A root that fails a gate is reported and never cleaned (LR-3) — this engine
/// does not decide that, it only decides <em>which</em> path and <em>why</em>.
/// </para>
/// <para>
/// It reads through the ports and mutates nothing: there is no file-writing code path here at all,
/// which is what the C-1 zero-writes assertion checks.
/// </para>
/// </summary>
public sealed class ResolutionEngine
{
    private readonly IFileSystem _fs;
    private readonly IFileSystemPaths _paths;
    private readonly string? _home;

    public ResolutionEngine(IFileSystem fs, OperatingSystemKind os, string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
        _paths = new IFileSystemPaths(fs, os);
        _home = homeDirectory;
    }

    /// <summary>
    /// Resolves one location. <paramref name="fingerprint"/> is the optional gate from T-006; when
    /// supplied and it fails, every root is marked <see cref="CoreDomain.RootValidity.FailsFingerprint"/>.
    /// </summary>
    public ResolutionOutcome Resolve(LocationSpec spec, Func<string, bool>? fingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var issues = new List<ResolutionIssue>();
        var ranked = RankSources(spec, issues);

        var roots = new List<ResolvedRoot>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (Candidate candidate in ranked)
        {
            CanonicalResult canonical = _paths.Canonicalize(candidate.Path);
            if (!canonical.Ok)
            {
                issues.Add(new ResolutionIssue(
                    spec.Id, "E-INVALID-PATH",
                    $"Source {Describe(candidate)} returned a path that cannot be canonicalized: {canonical.Message}",
                    candidate.RawValue));
                continue;
            }

            string realPath = _paths.ResolveRealPath(canonical.Path);

            // LR-2: de-duplicate by REAL path, so `C:\cache` and `\\?\C:\cache` are one root.
            if (!seenPaths.Add(realPath))
            {
                issues.Add(new ResolutionIssue(
                    spec.Id, "E-DUPLICATE",
                    $"{Describe(candidate)} resolves to '{realPath}', already covered by a higher-precedence source.",
                    candidate.RawValue));
                continue;
            }

            CoreDomain.RootValidity validity = Validate(root: canonical.Path, realPath, spec, issues, candidate);

            if (validity == CoreDomain.RootValidity.Ok && fingerprint is not null && !fingerprint(realPath))
            {
                validity = CoreDomain.RootValidity.FailsFingerprint;
                issues.Add(new ResolutionIssue(
                    spec.Id, "FINGERPRINT-FAIL",
                    $"'{realPath}' does not match the expected layout for '{spec.Id}'. It will be reported but never cleaned.",
                    candidate.RawValue));
            }

            roots.Add(new ResolvedRoot(
                DeclaredPath: candidate.Path,
                RealPath: realPath,
                Role: DecideRole(ranked, candidate),
                Via: candidate.Via,
                ViaDetail: candidate.Detail,
                Validity: validity,
                Owner: null));
        }

        // LR-4: two live sources disagreeing about a first-match location and no pin means we do not
        // actually know which is right, so the location is Ambiguous and is never cleaned.
        if (string.IsNullOrEmpty(spec.Pin) && TryFindAmbiguity(ranked, out string? ambigDetail))
        {
            issues.Add(new ResolutionIssue(
                spec.Id, "AMBIGUOUS",
                $"Two live sources disagree about '{spec.Id}' ({ambigDetail}) and there is no pin. It will not be cleaned until pinned.",
                null));

            roots = [.. roots.Select(r => r with { Validity = r.Validity == CoreDomain.RootValidity.Ok ? CoreDomain.RootValidity.Ambiguous : r.Validity })];
        }

        return new ResolutionOutcome(spec.Id, Deterministic.OrderRoots(roots), issues);
    }

    /// <summary>
    /// Sources in precedence order (LR-1). The numeric enum values ARE the precedence, so the order
    /// cannot drift from the table in the spec.
    /// </summary>
    private static List<Candidate> RankSources(LocationSpec spec, List<ResolutionIssue> issues)
    {
        var ranked = new List<Candidate>();

        if (!string.IsNullOrWhiteSpace(spec.Pin))
        {
            ranked.Add(new Candidate
            {
                Path = spec.Pin,
                Via = ResolvedVia.Pin,
                Detail = "--root / config pin",
                RawValue = spec.Pin,
            });

            // A pin is authoritative for the path but does NOT suppress the other sources: they are
            // still reported, because a user who pinned the wrong path needs to see what else
            // claimed it. The disagreement surfaces as an ambiguity note rather than a silent win.
            foreach (ResolvedVia via in LocationSpec.PrecedenceOrder)
            {
                if (via != ResolvedVia.Pin && spec.Sources.TryGetValue(via, out SourceResult? result) && result.Candidate is { } c)
                {
                    ranked.Add(c);
                }
            }

            return ranked;
        }

        foreach (ResolvedVia via in LocationSpec.PrecedenceOrder)
        {
            if (via == ResolvedVia.OsFile || via == ResolvedVia.Default)
            {
                continue;
            }

            if (spec.Sources.TryGetValue(via, out SourceResult? result) && result.Candidate is { } candidate)
            {
                ranked.Add(candidate);
            }
        }

        // The OS default is a LAST resort, not a corroborating source (05). Once any live source has
        // answered, the default must not be added: it is a different path in the general case, so
        // adding it would invent a phantom second root and — because two live sources would then
        // disagree — report false ambiguity on nearly every machine that configures a variable.
        if (!ranked.Any(c => c.IsLive) && spec.OsDefaults.TryGetValue(spec.OS, out string? osDefault))
        {
            ranked.Add(new Candidate
            {
                Path = osDefault,
                Via = ResolvedVia.Default,
                Detail = $"{spec.OS} default",
                RawValue = osDefault,
            });
        }

        return ranked;
    }

    private CoreDomain.RootValidity Validate(
        string root,
        string realPath,
        LocationSpec spec,
        List<ResolutionIssue> issues,
        Candidate candidate)
    {
        // Gate 3 of 05: the denylist floor. A denylist hit is reported, never cleaned, whatever the
        // other gates say.
        string? deniedBy = Denylist.WhichDenied(realPath, spec.OS, _home);
        if (deniedBy is not null)
        {
            issues.Add(new ResolutionIssue(
                spec.Id, "DENIED",
                $"'{realPath}' is on the safety denylist (matched '{deniedBy}'). It will be reported but never cleaned.",
                candidate.RawValue));
            return CoreDomain.RootValidity.Denied;
        }

        // Gate 1: exists and is a directory.
        if (!_fs.DirectoryExists(root))
        {
            return CoreDomain.RootValidity.NotFound;
        }

        return CoreDomain.RootValidity.Ok;
    }

    /// <summary>
    /// The highest-precedence live candidate is <see cref="CoreDomain.RootRole.Active"/>. The rest are
    /// <see cref="CoreDomain.RootRole.Additional"/> in both modes — the difference between First and All is
    /// whether the lower-precedence sources are consulted at all, not how the winner is labelled.
    /// A source that reports itself not live yields <see cref="CoreDomain.RootRole.Inactive"/>, which the spec
    /// caps at Review tier (LR-5): an old folder nobody points at is often the safest, biggest win.
    /// </summary>
    private static CoreDomain.RootRole DecideRole(List<Candidate> ranked, Candidate candidate)
    {
        if (!candidate.IsLive)
        {
            return CoreDomain.RootRole.Inactive;
        }

        Candidate? winner = ranked.FirstOrDefault(c => c.IsLive);
        bool isWinner = winner is not null
            && string.Equals(winner.RawValue ?? winner.Path, candidate.RawValue ?? candidate.Path, StringComparison.Ordinal);

        return isWinner ? CoreDomain.RootRole.Active : CoreDomain.RootRole.Additional;
    }

    private static IEnumerable<Candidate> RankedLive(List<Candidate> ranked) =>
        ranked.Where(c => c.IsLive);

    /// <summary>
    /// Two DIFFERENT live paths from two live sources, with no pin, means we do not know which is
    /// in use. Same path from two sources is not ambiguity — that is just corroboration.
    /// </summary>
    private static bool TryFindAmbiguity(List<Candidate> ranked, out string? detail)
    {
        detail = null;

        List<Candidate> live = [.. ranked.Where(c => c.IsLive)];
        var byPath = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        foreach (Candidate candidate in live)
        {
            string key = candidate.RawValue ?? candidate.Path;
            if (byPath.TryGetValue(key, out Candidate? first))
            {
                // The FIRST source already answers this question; a lower-precedence source naming
                // the same path adds confidence, not ambiguity.
                continue;
            }

            if (candidate.Via == ResolvedVia.Pin)
            {
                return false;
            }

            byPath[key] = candidate;
        }

        if (byPath.Count < 2)
        {
            return false;
        }

        Candidate[] entries = [.. byPath.Values];
        detail = string.Join(" vs ", entries.Select(c => $"{Describe(c)}='{c.Path}'"));
        return true;
    }

    private static string Describe(Candidate candidate) =>
        candidate.Detail is null ? candidate.Via.ToString().ToLowerInvariant() : $"{candidate.Via.ToString().ToLowerInvariant()} {candidate.Detail}";
}

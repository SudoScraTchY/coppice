using Coppice.Ports;

namespace Coppice.Core.PathSafety;

/// <summary>
/// Result of a canonicalization attempt. Use <see cref="Ok"/> to discriminate success vs. refusal.
/// Every failure carries a <see cref="Reason"/> and a human-readable <see cref="Message"/>.
/// </summary>
public sealed record CanonicalResult(bool Ok, string Path, CanonicalFailure Reason, string Message)
{
    public static CanonicalResult OkResult(string path) => new(true, path, CanonicalFailure.None, string.Empty);

    public static CanonicalResult Fail(CanonicalFailure reason, string message) => new(false, string.Empty, reason, message);
}

/// <summary>Reason why canonicalization refused a path.</summary>
public enum CanonicalFailure
{
    /// <summary>No failure — the path is valid and canonicalized.</summary>
    None = 0,

    /// <summary>Parent traversal would escape the filesystem root.</summary>
    EscapedRoot,

    /// <summary>Windows ADS-style colon separator detected in a path segment (not the drive letter).</summary>
    AdsNotation,

    /// <summary>Trailing dot or space found in a Windows path segment (stripped by kernel but rejected here).</summary>
    TrailingDotOrSpace,

    /// <summary>Unresolvable or empty input path.</summary>
    EmptyPath,

    /// <summary>Path contains a null byte.</summary>
    NullByte,

    /// <summary>The input is not absolute for the requested OS (relative, drive-relative or UNC).</summary>
    NotAbsolute,
}

/// <summary>
/// Pure per-OS path canonicalization. All inputs are explicit strings and an <see cref="OperatingSystemKind"/>;
/// no OS detection, no state. Designed so every refusal is self-documenting (a deleter must be safe).
/// </summary>
public static class PathCanonicalizer
{
    /// <summary>
    /// Canonicalizes <paramref name="path"/> for the given OS. Rejects unsafe inputs via <see cref="CanonicalResult"/>
    /// rather than throwing — the caller is building a deletion plan and must report refusals explicitly.
    /// </summary>
    public static CanonicalResult Canonicalize(string path, OperatingSystemKind os)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return CanonicalResult.Fail(CanonicalFailure.EmptyPath, "Path is empty or whitespace.");
        }

        if (path.Contains('\0'))
        {
            return CanonicalResult.Fail(CanonicalFailure.NullByte, "Path contains a null byte.");
        }

        // A deletion root must be drive-qualified on Windows and slash-rooted on POSIX. A single
        // leading separator on Windows is drive-relative (`\Windows` means the current drive's
        // Windows), and UNC paths are not local cache roots; treating either as absolute would let
        // it slip past the drive-qualified denylist rules.
        if (!IsRooted(path, os))
        {
            return CanonicalResult.Fail(
                CanonicalFailure.NotAbsolute,
                $"Path '{path}' is not an absolute local path for {os}.");
        }

        // The canonical form is always prefix-free. Two spellings of the same path must produce
        // byte-identical canonical output or every containment and equality check between a
        // `\\?\` path and a plain one would spuriously fail. Re-adding the prefix is an explicit
        // I/O concern, handled by ToLongPathForm below.
        var work = path;
        if (os == OperatingSystemKind.Windows && work.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            work = work[4..];
        }

        // Normalize separators.
        work = work.Replace('/', '\\');

        // Split into segments (handles consecutive separators, etc.).
        var rawSegments = work.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);

        // Drive-letter handling on Windows.
        var drive = string.Empty;
        var segmentStart = 0;
        if (os == OperatingSystemKind.Windows && rawSegments.Length > 0 && IsDriveLetter(rawSegments[0]))
        {
            // rawSegments[0] is already "C:" — re-appending a colon would yield "C::".
            drive = rawSegments[0].ToUpperInvariant();
            segmentStart = 1;
        }

        // Process each segment, folding . and rejecting escaped ..
        var folded = new List<string>();
        foreach (var seg in rawSegments.AsSpan()[segmentStart..])
        {
            if (seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                if (folded.Count == 0)
                {
                    return CanonicalResult.Fail(CanonicalFailure.EscapedRoot,
                        "Path traverses above the root (..) which is not allowed.");
                }

                folded.RemoveAt(folded.Count - 1);
                continue;
            }

            // Validate the segment itself.
            if (ValidateSegment(seg, os) is { } invalid)
            {
                return invalid;
            }

            folded.Add(seg);
        }

        // Reconstruct the path.
        // A canonical path is spelled with the NATIVE separator for its OS: a POSIX canonical form
        // using backslashes would be wrong on disk, and a Windows one using slashes would break the
        // ADS check and the drive-letter rules that follow.
        char sep = os == OperatingSystemKind.Windows ? '\\' : '/';
        var rest = string.Join(sep, folded);
        string result;
        if (!string.IsNullOrEmpty(drive))
        {
            result = drive + (string.IsNullOrEmpty(rest) ? sep.ToString() : sep + rest);
        }
        else
        {
            // POSIX: ensure rooted.
            result = sep + rest;
        }

        return CanonicalResult.OkResult(result);
    }

    /// <summary>
    /// Converts a canonical path back into the extended-length (<c>\\?\</c>) form for passing to
    /// Win32 I/O. This is the inverse of the prefix stripping done by <see cref="Canonicalize"/>, and
    /// is only ever applied immediately before a filesystem call, never for comparison.
    /// </summary>
    public static string ToLongPathForm(string canonicalPath, OperatingSystemKind os)
    {
        if (os != OperatingSystemKind.Windows || canonicalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return canonicalPath;
        }

        return canonicalPath.StartsWith(@"\\", StringComparison.Ordinal) && !canonicalPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonicalPath[2..]
            : @"\\?\" + canonicalPath;
    }

    /// <summary>True when a path is in the extended-length (<c>\\?\</c>) form.</summary>
    public static bool HasLongPathPrefix(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal);

    /// <summary>The native directory separator for an OS.</summary>
    public static char SeparatorFor(OperatingSystemKind os) =>
        os == OperatingSystemKind.Windows ? '\\' : '/';

    /// <summary>The string comparison mode a path prefix/suffix check must use on an OS.</summary>
    public static StringComparison ComparisonFor(OperatingSystemKind os) =>
        os == OperatingSystemKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True when a path is absolute on the given OS (no reliance on the running host).</summary>
    public static bool IsRooted(string path, OperatingSystemKind os)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (os == OperatingSystemKind.Windows)
        {
            // Drive-qualified only. A single leading separator is drive-relative, and UNC paths are
            // not local cache roots, so neither is "rooted" for a deletion decision.
            var probe = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            return probe.Length >= 3
                && char.IsAsciiLetter(probe[0])
                && probe[1] == ':'
                && (probe[2] == '\\' || probe[2] == '/');
        }

        // POSIX: a backslash is an ordinary filename character, not a separator.
        return path[0] == '/';
    }

    /// <summary>
    /// Root-anchored combine: a rooted <paramref name="relative"/> segment discards
    /// <paramref name="root"/> rather than escaping it, and a <c>..</c> can never climb above
    /// <paramref name="root"/>. Untrusted input must never be combined this way.
    /// </summary>
    public static string Combine(string root, string relative, OperatingSystemKind os)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return root;
        }

        if (IsRooted(relative, os))
        {
            return relative;
        }

        var separator = SeparatorFor(os);
        var canonical = Canonicalize(relative, os);
        var cleaned = canonical.Ok ? canonical.Path : relative.Replace('/', separator).Replace('\\', separator);

        var segments = new List<string>();
        foreach (var segment in cleaned.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        var prefix = IsRooted(root, os) ? root.TrimEnd(separator) + separator : string.Empty;
        return prefix + string.Join(separator, segments);
    }

    /// <summary>
    /// Folds a symlink/junction target against the link's parent with true OS semantics.
    /// <para>
    /// This is deliberately NOT <see cref="Combine"/>: that method is root-anchored and clamps <c>..</c>
    /// at the root, which is right for untrusted plan paths but wrong for a link target. A symlink at
    /// <c>/cache/pkg</c> pointing at <c>../../outside</c> really means <c>/outside</c>; clamping would
    /// return <c>/cache/outside</c> and report a containment escape as safe. Windows root-relative
    /// targets (<c>\Windows</c>) are resolved against the link parent's drive, and a drive-relative
    /// target (<c>C:foo</c>) is refused because its meaning depends on per-drive process state.
    /// </para>
    /// </summary>
    public static CanonicalResult CombineLinkTarget(string parent, string target, OperatingSystemKind os)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return CanonicalResult.Fail(CanonicalFailure.EmptyPath, "Link target is empty.");
        }

        char sep = SeparatorFor(os);
        string work = target.Replace('/', sep);

        string prefix;
        var segments = new List<string>();

        if (os == OperatingSystemKind.Windows)
        {
            if (work.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return CanonicalResult.Fail(CanonicalFailure.NotAbsolute,
                    $"UNC link target '{target}' is not a local path.");
            }

            if (work.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                work = work[4..];
            }

            if (work.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return CanonicalResult.Fail(CanonicalFailure.NotAbsolute,
                    $"UNC link target '{target}' is not a local path.");
            }

            if (work.Length >= 3 && char.IsAsciiLetter(work[0]) && work[1] == ':' && work[2] == '\\')
            {
                prefix = char.ToUpperInvariant(work[0]) + ":\\";
                work = work[3..];
            }
            else if (work.StartsWith('\\'))
            {
                // Root-relative: the drive is the link parent's drive, not the process cwd's.
                if (parent.Length < 2 || parent[1] != ':')
                {
                    return CanonicalResult.Fail(CanonicalFailure.NotAbsolute,
                        $"Root-relative link target '{target}' has no drive to anchor to.");
                }

                prefix = char.ToUpperInvariant(parent[0]) + ":\\";
                work = work.TrimStart('\\');
            }
            else if (work.Length >= 2 && char.IsAsciiLetter(work[0]) && work[1] == ':')
            {
                return CanonicalResult.Fail(CanonicalFailure.NotAbsolute,
                    $"Drive-relative link target '{target}' is not resolvable without process state.");
            }
            else
            {
                prefix = parent[..3];
                segments.AddRange(parent[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        else
        {
            prefix = "/";
            if (work.StartsWith('/'))
            {
                work = work.TrimStart('/');
            }
            else
            {
                segments.AddRange(parent.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        foreach (string segment in work.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Clamp at the filesystem root, exactly as the kernel does for a link target.
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            if (ValidateSegment(segment, os) is { } invalid)
            {
                return invalid;
            }

            segments.Add(segment);
        }

        var rest = string.Join(sep, segments);
        string result = prefix.EndsWith(sep)
            ? prefix + rest
            : prefix + sep + rest;

        return CanonicalResult.OkResult(result);
    }

    /// <summary>
    /// Case-folds a path for comparison according to the OS rules:
    /// OrdinalIgnoreCase on Windows and macOS, Ordinal on Linux.
    /// </summary>
    public static string FoldCase(string path, OperatingSystemKind os) =>
        os switch
        {
            OperatingSystemKind.Windows => path.ToUpperInvariant(),
            OperatingSystemKind.MacOS => path.ToUpperInvariant(),
            OperatingSystemKind.Linux => path,
            _ => path,
        };

    /// <summary>
    /// Compares two paths using the OS-appropriate case semantics.
    /// </summary>
    public static bool PathsEqual(string a, string b, OperatingSystemKind os) =>
        string.Equals(FoldCase(a, os), FoldCase(b, os), StringComparison.Ordinal);

    /// <summary>
    /// Detects NFC/NFD duplicate names on macOS/Linux by comparing their NFC forms.
    /// Returns true if <paramref name="candidate"/> and any of <paramref name="existing"/>
    /// are NFC-equivalent but not string-equal.
    /// </summary>
    public static bool HasNfcNfdCollision(string candidate, IReadOnlyList<string> existing, OperatingSystemKind os)
    {
        if (os != OperatingSystemKind.MacOS && os != OperatingSystemKind.Linux)
        {
            return false;
        }

        var nfcCand = NormalizeToNfc(candidate);
        foreach (var ex in existing)
        {
            if (string.Equals(nfcCand, NormalizeToNfc(ex), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeToNfc(string s)
    {
        try
        {
            return s.Normalize(System.Text.NormalizationForm.FormC);
        }
        catch
        {
            return s;
        }
    }

    /// <summary>
    /// Rejects segment names Windows cannot represent unambiguously. Returns a failure rather than
    /// throwing: an untrusted path from a plugin must be REFUSED with a reason, never turn into an
    /// exception that aborts a whole scan.
    /// </summary>
    private static CanonicalResult? ValidateSegment(string seg, OperatingSystemKind os)
    {
        if (os != OperatingSystemKind.Windows)
        {
            return null;
        }

        // ADS-style colons: "name:stream" is a hidden data stream, not a filename (E-6).
        // The drive-letter colon is handled before this call and never reaches here.
        if (seg.Contains(':'))
        {
            return CanonicalResult.Fail(
                CanonicalFailure.AdsNotation,
                $"ADS-style notation detected in segment '{seg}'.");
        }

        // Trailing dots and spaces are silently stripped by Win32, so two different names would
        // resolve to the same directory (E-5).
        if (seg.Length > 0 && (seg[^1] == '.' || seg[^1] == ' '))
        {
            return CanonicalResult.Fail(
                CanonicalFailure.TrailingDotOrSpace,
                $"Trailing dot or space in segment '{seg}'.");
        }

        // A segment made only of dots is a traversal token, not a name.
        if (seg.All(c => c == '.'))
        {
            return CanonicalResult.Fail(
                CanonicalFailure.EscapedRoot,
                $"Segment '{seg}' consists only of dots.");
        }

        return null;
    }

    private static bool IsDriveLetter(string segment) =>
        segment.Length == 2 && segment[1] == ':' && char.IsAsciiLetter(segment[0]);
}

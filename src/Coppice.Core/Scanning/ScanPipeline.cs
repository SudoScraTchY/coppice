using System.Collections.Concurrent;
using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Scanning;

/// <summary>One root handed to the pipeline, already validated by resolution + fingerprint.</summary>
public sealed record ScanRoot(string LocationId, string Path);

/// <summary>The result of scanning every root.</summary>
public sealed record ScanResult(
    IReadOnlyList<Item> Items,
    IReadOnlyList<ScanIssue> Issues,
    UsageBreakdown Usage,
    IReadOnlySet<string> LocationsWithZeroItems)
{
    /// <summary>The canonical serialization, used for the NFR-06 golden comparison.</summary>
    public string ToJson() => DomainJson.Serialize(this);
}

/// <summary>
/// The scan pipeline (T-007, FR-04/FR-05, NFR-04, NFR-06).
/// <para>
/// Roots are scanned in parallel because a developer's caches are spread across several slow
/// directories (FR-04's 30k-item/60s budget is not reachable serially over a spinning disk). The
/// parallelism is bounded, cancellable, and reports progress — but the OUTPUT is always sorted
/// before it is returned, so two runs over the same tree produce byte-identical JSON (NFR-06)
/// regardless of which root finished first.
/// </para>
/// <para>
/// Read-only by construction: nothing in this type calls a mutator on <see cref="IFileSystem"/>,
/// and the C-1 test asserts the fake VFS records no writes.
/// </para>
/// </summary>
public sealed class ScanPipeline
{
    private readonly IFileSystem _fs;
    private readonly OperatingSystemKind _os;
    private readonly IReadOnlyList<IEntryEnumerator> _enumerators;
    private readonly string? _home;
    private readonly int _maxConcurrency;

    public ScanPipeline(
        IFileSystem fs,
        OperatingSystemKind os,
        IEnumerable<IEntryEnumerator>? enumerators = null,
        string? homeDirectory = null,
        int maxConcurrency = 0)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
        _os = os;
        _enumerators = enumerators is null ? [new DirectoryEntryEnumerator(fs)] : [.. enumerators];
        _home = homeDirectory;

        // Bounded so a machine with 32 cores does not open 32 directory handles per root and starve
        // the disk it is trying to read. 0 means "pick a sensible default".
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    public async Task<ScanResult> RunAsync(
        IReadOnlyList<ScanRoot> roots,
        IScanProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var items = new ConcurrentBag<Item>();
        var issues = new ConcurrentBag<ScanIssue>();
        var zeroItemLocations = new ConcurrentBag<string>();

        using var throttled = new SemaphoreSlim(_maxConcurrency);
        var tasks = new List<Task>(roots.Count);

        foreach (ScanRoot root in roots)
        {
            tasks.Add(Task.Run(
                async () =>
                {
                    await throttled.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await ScanRootAsync(root, items, issues, zeroItemLocations, progress, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        throttled.Release();
                    }
                },
                cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Order FIRST, then de-duplicate — never the other way round. A ConcurrentBag enumerates in
        // completion order, so de-duplicating before sorting would make "first writer wins" depend
        // on which root happened to finish first, and NFR-06 would be quietly false.
        Item[] sorted = [.. Deterministic.OrderItems(items)];

        // Two providers can legitimately cover the same entry (a built-in enumerator and a plugin,
        // say). Counting it twice would double the reported reclaim and put the same path in the
        // plan twice. With the input already ordered, first-wins is deterministic.
        var unique = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (Item item in sorted)
        {
            unique.TryAdd(item.ItemId, item);
        }

        Item[] ordered = [.. Deterministic.OrderItems(unique.Values)];
        IReadOnlyList<ScanIssue> orderedIssues = Deterministic.OrderIssues(issues);

        return new ScanResult(
            ordered,
            orderedIssues,
            UsageBreakdown.FromItems(ordered),
            new HashSet<string>(zeroItemLocations, StringComparer.Ordinal));
    }

    private async Task ScanRootAsync(
        ScanRoot root,
        ConcurrentBag<Item> items,
        ConcurrentBag<ScanIssue> issues,
        ConcurrentBag<string> zeroItemLocations,
        IScanProgress? progress,
        CancellationToken cancellationToken)
    {
        if (!_fs.DirectoryExists(root.Path))
        {
            // A root can vanish between resolution and scanning (a user clearing a cache in another
            // terminal). That is information, not a failure: report it and carry on.
            issues.Add(new ScanIssue(root.LocationId, "ROOT-VANISHED", $"Root '{root.Path}' no longer exists.", root.Path));
            return;
        }

        int produced = 0;

        foreach (IEntryEnumerator enumerator in _enumerators)
        {
            var context = new ScanContext
            {
                FileSystem = _fs,
                OS = _os,
                RootPath = root.Path,
                LocationId = root.LocationId,
                HomeDirectory = _home,
            };

            try
            {
                await foreach (Item item in enumerator.EnumerateAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    // A provider is untrusted: an item must stay inside the root it claims to come
                    // from, or it is dropped with an issue rather than carried into the plan.
                    if (!IsInside(root.Path, item.Path))
                    {
                        issues.Add(new ScanIssue(
                            root.LocationId,
                            "ITEM-ESCAPES-ROOT",
                            $"Enumerator '{enumerator.Id}' produced '{item.Path}', which is outside '{root.Path}'. It was dropped.",
                            item.Path));
                        continue;
                    }

                    items.Add(item);
                    produced++;

                    progress?.Report(enumerator.Id, produced, item.Size);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // One broken enumerator or one unreadable subtree must not abort the whole scan.
                issues.Add(new ScanIssue(
                    root.LocationId,
                    "ENUMERATOR-FAILED",
                    $"Enumerator '{enumerator.Id}' failed on '{root.Path}': {ex.Message}",
                    root.Path));
            }
        }

        if (produced == 0)
        {
            zeroItemLocations.Add(root.LocationId);
        }
    }

    /// <summary>
    /// Containment check for item paths, using the real canonical form on both sides. Implemented
    /// here with the port rather than a string prefix so a symlinked root is handled the same way
    /// the gateway will handle it later.
    /// </summary>
    private bool IsInside(string root, string candidate)
    {
        var paths = new PathSafety.IFileSystemPaths(_fs, _os);
        return paths.IsWithin(root, candidate);
    }
}

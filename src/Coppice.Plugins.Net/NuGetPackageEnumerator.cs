using System.Globalization;
using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Ports;
using CoreDomain = Coppice.Core.Domain;
using CoreScanContext = Coppice.Core.Scanning.ScanContext;

namespace Coppice.Plugins.Net;

/// <summary>
/// Enumerates the NuGet global-packages cache: one item per package id + version (FR-04).
/// <para>
/// The layout is <c>{id}/{version}/</c>, so the enumerator walks two levels and no deeper. Each
/// item's <c>Facts</c> carry the integrity signals 09's health check needs — is the <c>.nupkg</c>
/// present, is there a <c>.nupkg.metadata</c> — which is what lets a later pass distinguish a
/// complete package from a half-written one.
/// </para>
/// <para>
/// Every item is <see cref="CoreDomain.Risk.Safe"/>: the global-packages cache is re-downloadable, which is
/// what makes it the only tier a default policy may act on.
/// </para>
/// </summary>
public sealed class NuGetPackageEnumerator : IEntryEnumerator
{
    private readonly IFileSystem _fs;
    private readonly char _separator;

    public NuGetPackageEnumerator(IFileSystem fs, OperatingSystemKind os)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
        _separator = os == OperatingSystemKind.Windows ? '\\' : '/';
    }

    public string Id => "nuget-packages";

    public async IAsyncEnumerable<CoreDomain.Item> EnumerateAsync(
        CoreScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // This enumerator knows ONE layout. The pipeline offers every root to every enumerator, so
        // declining the roots that are not this location's is what stops a dotnet-root being read as
        // a package cache — which reports host/fxr as a package named "host", version "fxr".
        if (!string.Equals(context.LocationId, Id, StringComparison.Ordinal))
        {
            yield break;
        }

        // The version directory name is lowercased by NuGet on a case-sensitive filesystem, so
        // ordering must be ordinal, not a locale-aware compare.
        foreach (FileEntry package in Ordered(context.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (package.Kind != EntryKind.Directory)
            {
                continue;
            }

            foreach (FileEntry version in Ordered(package.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (version.Kind != EntryKind.Directory)
                {
                    continue;
                }

                // Two kinds of directory live here that are NOT versions, and reporting either as
                // one would invent a cleanup target that does not exist:
                //
                //  - '.tools' and '.metadata': NuGet's own bookkeeping, side by side the versions.
                //  - 'lib', 'ref', 'build', 'runtimes' and friends: the EXTRACTED package. The
                //    extracted tree has its own nesting (lib/net8.0/…), so without this filter a
                //    package contributes phantom "versions" named after its target frameworks.
                //
                // NuGet writes versions as dotted numerics (13.0.3, 1.0.0-preview.1). Anything else
                // is a directory this cache owns for a different purpose.
                if (!NetEcosystemLocations.LooksLikeVersion(version.Name))
                {
                    continue;
                }

                Facts facts = BuildFacts(package.Name, version);
                yield return new CoreDomain.Item(
                    Ecosystem: NetProfile.EcosystemId,
                    Kind: "package",
                    Name: package.Name,
                    Version: version.Name,
                    LocationId: context.LocationId,
                    Path: version.Path,
                    Size: _fs.MeasureSize(version.Path),
                    Risk: CoreDomain.Risk.Safe,
                    Facts: facts);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>Integrity signals for one package version, recorded as facts for the health check.</summary>
    private Facts BuildFacts(string packageId, FileEntry versionDirectory)
    {
        Dictionary<string, string> integrity = IntegrityFacts(_fs, _separator, packageId, versionDirectory);

        return Facts.From(
        [
            new("packageId", packageId),
            .. integrity.Select(fact => new KeyValuePair<string, string>(fact.Key, fact.Value)),

            // The mtime the drift check compares against. Recorded in round-trip form so a plan written on
            // one machine binds the same instant on another; an item without it cannot be drift-checked, and
            // --strict refuses rather than treating an unknown mtime as "unchanged".
            new("lastWriteUtc", versionDirectory.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
        ]);
    }

    /// <summary>
    /// The four presence facts the health check relies on. Shared with the ecosystem plugin (T-048) so
    /// that the orchestrator path and the standalone enumerator report the same integrity signals.
    /// </summary>
    internal static Dictionary<string, string> IntegrityFacts(
        IFileSystem fs,
        char separator,
        string packageId,
        FileEntry versionDirectory)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(versionDirectory);

        string version = versionDirectory.Name;
        string nupkgName = $"{packageId}.{version}.nupkg";

        // NuGet writes the archive flat in the version directory, alongside .nuspec and the
        // extracted lib/ref folders.
        bool nupkgPresent = fs.FileExists(Combine(versionDirectory.Path, nupkgName, separator));
        bool metadataPresent = fs.FileExists(Combine(versionDirectory.Path, $"{nupkgName}.metadata", separator));
        bool signaturePresent = fs.FileExists(Combine(versionDirectory.Path, $"{nupkgName}.signature.p7s", separator));
        bool nuspecPresent = fs.DirectoryExists(Combine(versionDirectory.Path, packageId, separator));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nupkgPresent"] = nupkgPresent ? "true" : "false",
            ["metadataPresent"] = metadataPresent ? "true" : "false",
            ["signaturePresent"] = signaturePresent ? "true" : "false",
            ["nuspecPresent"] = nuspecPresent ? "true" : "false",
        };
    }

    /// <summary>
    /// Joins using the TARGET OS's separator, not the running host: the plugin walks trees on the
    /// machine being scanned, and on a case-sensitive target a backslash is a filename character,
    /// not a separator.
    /// </summary>
    private static string Combine(string root, string relative, char separator)
    {
        string trimmed = root.TrimEnd('/', '\\');
        string tail = relative.Replace('\\', separator).Replace('/', separator);
        return trimmed + separator + tail.TrimStart(separator);
    }

    private IEnumerable<FileEntry> Ordered(string path) =>
        _fs.EnumerateEntries(path, new EnumerationRequest
        {
            FollowLinks = false,
            IncludeLinks = false,
            MaxDepth = 1,
        }).OrderBy(e => e.Name, StringComparer.Ordinal);
}

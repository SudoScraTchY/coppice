using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Core.Scanning;

/// <summary>
/// Context handed to every scanner and inventory provider. Carries only ports and read-only
/// inputs — no services, no configuration objects, nothing a provider could use to reach the
/// disk outside the VFS (04-plugin-contract, DIP).
/// </summary>
public sealed class ScanContext
{
    public required IFileSystem FileSystem { get; init; }

    public required OperatingSystemKind OS { get; init; }

    /// <summary>Root being scanned, already validated by the resolution and fingerprint gates.</summary>
    public required string RootPath { get; init; }

    /// <summary>Location id the root belongs to, carried onto every item for provenance.</summary>
    public required string LocationId { get; init; }

    /// <summary>Project roots discovered for this scan, when discovery has run (FR-06).</summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    public string? HomeDirectory { get; init; }
}

/// <summary>Progress reporting for a long scan; implementations must not block.</summary>
public interface IScanProgress
{
    void Report(string stage, int itemsSeen, ulong bytesSeen);

    static IScanProgress None { get; } = new NullScanProgress();

    private sealed class NullScanProgress : IScanProgress
    {
        public void Report(string stage, int itemsSeen, ulong bytesSeen)
        {
        }
    }
}

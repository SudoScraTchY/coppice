using Coppice.Core.Domain;
using Coppice.Core.Scanning;
using Coppice.Ports;

namespace Coppice.Tests.Scanning;

/// <summary>
/// Deliberately misbehaving enumerators. The pipeline's contract is that a provider is UNTRUSTED
/// (04-plugin-contract): it may fail, yield nonsense, or try to escape its root, and the kernel must
/// survive all three without ever losing track of where it is.
/// </summary>
internal sealed class StubEnumerator : IEntryEnumerator
{
    private readonly string _name;
    private readonly string _path;
    private readonly ulong _size;

    public StubEnumerator(string id, string path, string name, ulong size)
    {
        Id = id;
        _name = name;
        _path = path;
        _size = size;
    }

    public string Id { get; }

    public async IAsyncEnumerable<Item> EnumerateAsync(
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new Item(
            Ecosystem: "stub",
            Kind: "entry",
            Name: _name,
            Version: string.Empty,
            LocationId: context.LocationId,
            Path: _path,
            Size: _size,
            Risk: Risk.Review,
            Facts: Facts.Empty);

        await Task.CompletedTask;
    }
}

internal sealed class ThrowingEnumerator : IEntryEnumerator
{
    public ThrowingEnumerator(string id) => Id = id;

    public string Id { get; }

    public async IAsyncEnumerable<Item> EnumerateAsync(
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new IOException($"enumerator {Id} could not read {context.RootPath}");
#pragma warning disable CS0162 // Unreachable, but required for the async iterator signature.
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Assigns risk tiers per entry, so the usage split can be measured without a second enumerator
/// describing the same entry (which would double-count the total).
/// </summary>
internal sealed class TieringEnumerator : IEntryEnumerator
{
    private readonly DirectoryEntryEnumerator _inner;
    private readonly (string PathPrefix, Risk Risk)[] _tiers;

    public TieringEnumerator(IFileSystem fs, params (string PathPrefix, Risk Risk)[] tiers)
    {
        _inner = new DirectoryEntryEnumerator(fs);
        _tiers = tiers;
    }

    public string Id => "tiering";

    public async IAsyncEnumerable<Item> EnumerateAsync(
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (Item item in _inner.EnumerateAsync(context, cancellationToken))
        {
            Risk risk = Risk.Review;
            foreach ((string prefix, Risk tier) in _tiers)
            {
                if (item.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    risk = tier;
                    break;
                }
            }

            yield return item with { Risk = risk };
        }
    }
}

internal sealed class RecordingProgress : IScanProgress
{
    public List<(string Stage, int Items, ulong Bytes)> Events { get; } = [];

    public void Report(string stage, int itemsSeen, ulong bytesSeen) =>
        Events.Add((stage, itemsSeen, bytesSeen));
}

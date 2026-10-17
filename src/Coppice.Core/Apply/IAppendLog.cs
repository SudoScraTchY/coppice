namespace Coppice.Core.Apply;

/// <summary>
/// An append-only line store under the state root (T-034's journal; T-035's audit log).
/// <para>
/// Separate from <see cref="Ports.IStateStore"/> on purpose. A state document is rewritten whole; a
/// journal is appended to once per step, and rewriting thousands of steps per append is quadratic. More
/// importantly, the durability contract differs: <see cref="AppendLineAsync"/> must not return until the
/// line is on disk, because the whole point of a write-ahead record is that it exists BEFORE the mutation
/// it describes.
/// </para>
/// <para>
/// Defined in Core, not Ports, because nothing outside the kernel writes a journal, and Ports is frozen
/// (ADR-011).
/// </para>
/// </summary>
public interface IAppendLog
{
    /// <summary>Appends one line (a trailing newline is added) and flushes it to stable storage.</summary>
    Task AppendLineAsync(string key, string line);

    /// <summary>The whole log, or null when it does not exist.</summary>
    Task<string?> ReadAllTextAsync(string key);
}

using Coppice.Core.Apply;
using Coppice.Core.Audit;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Audit;

/// <summary>
/// The writer's contract (T-035, S-12): every append is hash-chained to the line before it, the chain is
/// recovered from the log itself when a new writer takes over, and a kill mid-append leaves a torn line the
/// next writer terminates rather than a chain it cannot continue.
/// </summary>
public sealed class AuditLogTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static AuditLog NewLog(FakeAppendLog log) => new(log, new FakeClock(Stamp));

    private static async Task<AuditLogContents> ReadAsync(FakeAppendLog log) =>
        AuditLines.Parse(await log.ReadAllTextAsync(AuditLog.Key));

    private static AuditRecord Move(string from) => new()
    {
        Action = AuditRecord.QuarantineAction,
        From = from,
        Result = AuditRecord.OkResult,
    };

    [Fact]
    public async Task Appends_one_hash_chained_line_per_mutation()
    {
        var log = new FakeAppendLog();
        var audit = NewLog(log);

        await audit.AppendAsync(Move(@"C:\a"));
        await audit.AppendAsync(new AuditRecord { Action = AuditRecord.NativeAction, From = @"C:\b", Result = AuditRecord.OkResult });
        await audit.AppendAsync(new AuditRecord { Action = AuditRecord.RestoreAction, From = @"C:\q\a", To = @"C:\a", Result = AuditRecord.OkResult });

        AuditLogContents contents = await ReadAsync(log);

        Assert.Equal(3, contents.Records.Count);
        Assert.Equal([1L, 2L, 3L], contents.Records.Select(record => record.Seq));
        Assert.Equal(AuditRecord.Genesis, contents.Records[0].Prev);
        Assert.Equal(contents.Records[0].Hash, contents.Records[1].Prev);
        Assert.Equal(contents.Records[1].Hash, contents.Records[2].Prev);
        Assert.All(contents.Records, record => Assert.Equal(record.ComputeHash(), record.Hash));

        // The writer owns the timestamp and the sequence; the caller describes only the mutation.
        Assert.All(contents.Records, record => Assert.Equal(Stamp, record.Ts));
    }

    [Fact]
    public async Task A_new_writer_recovers_the_chain_from_the_log_itself()
    {
        var log = new FakeAppendLog();
        await NewLog(log).AppendAsync(Move(@"C:\a"));

        // A different process, a different writer instance: whatever is on disk is what the next line
        // links to, so a writer's death can never fork the chain.
        await NewLog(log).AppendAsync(new AuditRecord
        {
            Action = AuditRecord.NativeAction,
            From = @"C:\b",
            Result = AuditRecord.FailedResult,
            ExitCode = 3,
        });

        AuditLogContents contents = await ReadAsync(log);

        Assert.Equal(2, contents.Records.Count);
        Assert.Equal(2, contents.Records[1].Seq);
        Assert.Equal(contents.Records[0].Hash, contents.Records[1].Prev);
        Assert.All(contents.Records, record => Assert.Equal(record.ComputeHash(), record.Hash));
    }

    [Fact]
    public async Task A_torn_tail_is_terminated_and_the_chain_continues()
    {
        var log = new FakeAppendLog();
        await NewLog(log).AppendAsync(Move(@"C:\a"));
        log.TearTail(AuditLog.Key);

        await NewLog(log).AppendAsync(new AuditRecord
        {
            Action = AuditRecord.RestoreAction,
            From = @"C:\q\a",
            To = @"C:\a",
            Result = AuditRecord.OkResult,
        });

        AuditLogContents contents = await ReadAsync(log);

        // The torn line survives as the tolerated gap the recovery separator marks; the restore record
        // after it parses and links to the last record the log actually holds.
        Assert.True(contents.TornTail);
        Assert.Single(contents.Records);
        Assert.Equal(AuditRecord.RestoreAction, contents.Records[0].Action);
    }

    [Fact]
    public async Task The_record_round_trips_through_its_line()
    {
        var log = new FakeAppendLog();
        var audit = NewLog(log);

        var written = new AuditRecord
        {
            Action = AuditRecord.NativeAction,
            PlanId = "pln_abc123",
            StepId = "go-mod-cache",
            Eco = "go",
            Kind = "modcache",
            From = @"C:\go",
            Result = AuditRecord.FailedResult,
            ExitCode = 1,
            DurationMs = 42_000,
            Command = "go clean -modcache",
        };
        await audit.AppendAsync(written);

        AuditLogContents contents = await ReadAsync(log);
        AuditRecord read = Assert.Single(contents.Records);

        Assert.Equal("pln_abc123", read.PlanId);
        Assert.Equal("go-mod-cache", read.StepId);
        Assert.Equal("go", read.Eco);
        Assert.Equal("modcache", read.Kind);
        Assert.Equal(@"C:\go", read.From);
        Assert.Equal(AuditRecord.FailedResult, read.Result);
        Assert.Equal(1, read.ExitCode);
        Assert.Equal(42_000, read.DurationMs);
        Assert.Equal("go clean -modcache", read.Command);
        Assert.Null(read.To);
    }
}

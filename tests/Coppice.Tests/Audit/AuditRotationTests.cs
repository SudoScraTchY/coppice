using Coppice.Adapters;
using Coppice.Core.Audit;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Audit;

/// <summary>
/// T-035's rotation acceptance, against the REAL file adapter: the size cap rolls a segment aside without
/// discarding anything, reads come back oldest-first across segments, and the hash chain verifies end to
/// end — including through the verifier, which must never see only the newest segment.
/// </summary>
public sealed class AuditRotationTests
{
    [Fact]
    public async Task Rotation_preserves_every_line_and_the_chain_across_segments()
    {
        string root = Path.Combine(Path.GetTempPath(), "coppice-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Small enough that eight realistic lines roll at least once.
            var log = new FileAppendLog(root, maxSegmentBytes: 512);
            var audit = new AuditLog(log, new SystemClock());

            for (int i = 1; i <= 8; i++)
            {
                await audit.AppendAsync(new AuditRecord
                {
                    Action = AuditRecord.QuarantineAction,
                    PlanId = "pln_rotate",
                    StepId = $"nuget:pkg{i}/1.0.0",
                    Eco = "dotnet",
                    Kind = "package",
                    From = $@"C:\cache\pkg{i}\1.0.0",
                    To = $@"C:\cache\.coppice\quarantine\nuget-packages\pkg{i}-1.0.0",
                    Bytes = (ulong)(1200 * i),
                    Result = AuditRecord.OkResult,
                });
            }

            string auditDirectory = Path.Combine(root, "audit");
            Assert.True(Directory.GetFiles(auditDirectory, "audit.jsonl.*").Length >= 1, "the log never rolled");

            AuditLogContents contents = AuditLines.Parse(await log.ReadAllTextAsync(AuditLog.Key));
            Assert.Equal(8, contents.Records.Count);

            for (int i = 0; i < contents.Records.Count; i++)
            {
                Assert.Equal(i + 1, contents.Records[i].Seq);
                Assert.Equal(contents.Records[i].ComputeHash(), contents.Records[i].Hash);
                Assert.Equal(
                    i == 0 ? AuditRecord.Genesis : contents.Records[i - 1].Hash,
                    contents.Records[i].Prev);
            }

            // The verifier replays the whole chain across the roll. The quarantine lines have no index
            // entries here — this test is about the CHAIN, so only chain errors would be failures.
            AuditVerifyReport report = await new AuditVerifier(log, null, new FakeFileSystem(OperatingSystemKind.Windows)).VerifyAsync();
            Assert.DoesNotContain(report.Findings, finding => finding.Severity == AuditSeverity.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_journal_and_the_audit_share_one_append_log_without_colliding()
    {
        string root = Path.Combine(Path.GetTempPath(), "coppice-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileAppendLog(root, maxSegmentBytes: 256);
            var audit = new AuditLog(log, new SystemClock());

            await log.AppendLineAsync(Coppice.Core.Apply.ApplyJournal.KeyFor("pln_1"), "{\"phase\":\"begin\"}");
            await audit.AppendAsync(new AuditRecord
            {
                Action = AuditRecord.QuarantineAction,
                From = @"C:\cache\x",
                To = @"C:\cache\.coppice\quarantine\x",
                Result = AuditRecord.OkResult,
            });
            await log.AppendLineAsync(Coppice.Core.Apply.ApplyJournal.KeyFor("pln_1"), "{\"phase\":\"outcome\"}");

            // Each key reads back as its own stream, rotation included.
            Assert.Single(AuditLines.Parse(await audit.ReadAllTextAsync()).Records);
            string? journal = await log.ReadAllTextAsync(Coppice.Core.Apply.ApplyJournal.KeyFor("pln_1"));
            Assert.Contains("\"phase\":\"begin\"", journal, StringComparison.Ordinal);
            Assert.Contains("\"phase\":\"outcome\"", journal, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

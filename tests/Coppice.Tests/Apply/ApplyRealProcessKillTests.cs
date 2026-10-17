using System.Diagnostics;
using System.Globalization;
using System.Text;
using Coppice.Adapters;
using Coppice.Core.Apply;
using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Core.Plans;
using Coppice.Core.Quarantine;
using Coppice.Core.Snapshots;
using Coppice.Ports;
using Xunit;
using Policy = Coppice.Core.Domain.Policy;
using RemovalAction = Coppice.Core.Domain.RemovalAction;

namespace Coppice.Tests.Apply;

/// <summary>
/// T-034, the real kill -9: an actual <c>coppice apply</c> child process, terminated with
/// <c>Kill(entireProcessTree: true)</c> once its journal shows a mutation in flight, then resumed.
/// <para>
/// The in-process crash matrix models a kill by throwing where nothing catches. This is the real thing: a
/// separate OS process, a real filesystem, a real fsync-per-line journal, and the operating system tearing
/// it down at an arbitrary instruction. It is the only evidence that the durability the journal claims on
/// paper survives an actual abrupt termination, which is why the acceptance box is gated on it.
/// </para>
/// <para>
/// Tagged <c>RealProcess</c>: it spawns processes and writes real temp files, so it is slower and can be
/// excluded from a fast inner loop, but it runs in the normal gate. If a machine is fast enough that the run
/// finishes before the first intent can be observed, the tree is enlarged and the attempt repeated; only a
/// genuine inability to catch a mid-run kill fails, and it fails loudly rather than passing vacuously.
/// </para>
/// </summary>
[Trait("Category", "RealProcess")]
public sealed class ApplyRealProcessKillTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_killed_apply_leaves_no_partial_item_and_resume_finishes_it()
    {
        string? failure = null;

        // Escalate the tree until the run is slow enough to catch mid-flight. A machine that finishes 400
        // items before we can poll still has to be caught, so the retry grows the work rather than giving up.
        foreach (int packageCount in new[] { 400, 1600, 6400 })
        {
            (bool caught, string? error) = await AttemptAsync(packageCount);
            if (caught)
            {
                return;
            }

            failure = error;
        }

        Assert.Fail($"Could not catch a mid-run kill after escalating the tree: {failure}");
    }

    private static async Task<(bool Caught, string? Error)> AttemptAsync(int packageCount)
    {
        string temp = Path.Combine(Path.GetTempPath(), "coppice-kill-" + Guid.NewGuid().ToString("N"));
        string stateParent = Path.Combine(temp, "state");
        string stateDir = Path.Combine(stateParent, "coppice");
        string cacheDir = Path.Combine(temp, "cache");
        string planPath = Path.Combine(temp, "plan.json");

        try
        {
            Directory.CreateDirectory(stateDir);
            Directory.CreateDirectory(cacheDir);

            var fs = new PhysicalFileSystem();
            var items = new List<Item>();

            for (int i = 0; i < packageCount; i++)
            {
                string id = string.Create(CultureInfo.InvariantCulture, $"pkg{i:D5}");
                string versionDir = Path.Combine(cacheDir, id, "1.0.0");
                Directory.CreateDirectory(versionDir);

                // Enough bytes that HashTree does real work per item, widening the kill window honestly
                // rather than by adding a sleep.
                await File.WriteAllBytesAsync(Path.Combine(versionDir, "payload.bin"), new byte[8192]);

                string itemPath = versionDir;
                ulong size = fs.MeasureSize(itemPath);
                DateTimeOffset mtime = fs.GetEntry(itemPath)?.LastWriteTimeUtc ?? Stamp;

                items.Add(new Item(
                    "dotnet", "package", id, "1.0.0", "nuget-packages", itemPath, size, Risk.Safe,
                    Facts.From([new KeyValuePair<string, string>("lastWriteUtc", mtime.ToString("O", CultureInfo.InvariantCulture))])));
            }

            Snapshot snapshot = Snapshot.Create("snap_1", Stamp, items, [], ["dotnet"]);
            Plan plan = PlanBuilder.Build(
                snapshot.SnapshotId,
                Policy.Default,
                items.Select(i => new PlanStep(i.ItemId, Risk.Safe, "unused", RemovalAction.PathDelete(i.Path), i.Size))).Plan!;

            var state = new FileSystemStateStore(stateDir);
            await new SnapshotStore(state, new FixedClock(Stamp)).SaveAsync(snapshot);
            await new PlanStore(state).SaveAsync(plan);
            await File.WriteAllTextAsync(planPath, DomainJson.Serialize(plan, indented: true), Encoding.UTF8);

            string journalPath = Path.Combine(stateDir, "journal", plan.PlanId + ".jsonl");

            // ---- first run: kill it mid-apply ----
            using Process child = Launch(planPath, stateParent, cacheDir, resume: false, redirect: false);
            child.Start();

            if (!WaitForIntent(journalPath, child))
            {
                // It finished (or never started writing) before we could catch it. Not a failure of the
                // product; a failure of THIS attempt to create the race. Escalate.
                child.WaitForExit(60_000);
                return (false, $"run of {packageCount} items completed before an intent was observed");
            }

            child.Kill(entireProcessTree: true);
            child.WaitForExit(30_000);

            // Pre-resume invariant: at most one intent may lack an outcome — the single step that was in
            // flight when the process died. Two orphans would mean the journal under-reported.
            JournalState killed = ApplyJournal.Parse(await File.ReadAllTextAsync(journalPath));
            Assert.False(killed.Corrupt);
            int orphans = killed.Intents.Keys.Count(id => !killed.Outcomes.ContainsKey(id));
            Assert.InRange(orphans, 0, 1);

            // ---- resume: a fresh child finishes the plan ----
            using Process resume = Launch(planPath, stateParent, cacheDir, resume: true, redirect: true);
            resume.Start();
            string output = await resume.StandardOutput.ReadToEndAsync();
            await resume.WaitForExitAsync();

            Assert.True(resume.ExitCode is 0 or 4, $"resume exited {resume.ExitCode}: {output}");

            // Post-resume invariants, on the real disk.
            JournalState final = ApplyJournal.Parse(await File.ReadAllTextAsync(journalPath));
            Assert.False(final.Corrupt);
            Assert.True(final.Ended);
            Assert.Equal(final.Intents.Keys, final.Outcomes.Keys);

            var realState = new FileSystemStateStore(stateDir);
            var index = new QuarantineIndex(realState);
            IReadOnlyList<QuarantineEntry> entries = await index.ListAsync();

            string rootLeaf = new DirectoryInfo(cacheDir).Name;
            int atSource = 0;
            int inQuarantine = 0;

            foreach (Item item in items)
            {
                bool source = Directory.Exists(item.Path);
                string dest = QuarantinePath.For(cacheDir, rootLeaf, item.Path);
                bool quarantined = Directory.Exists(dest);

                Assert.NotEqual(source, quarantined);

                if (source)
                {
                    atSource++;
                    continue;
                }

                inQuarantine++;
                QuarantineEntry entry = Assert.Single(
                    entries,
                    e => string.Equals(e.QuarantinePath, dest, StringComparison.Ordinal));

                Assert.Equal(item.Path, entry.OriginalPath);
                Assert.Equal(QuarantineIndex.HashTree(fs, dest), entry.ContentHash);
            }

            Assert.Equal(items.Count, atSource + inQuarantine);
            Assert.True(inQuarantine > 0, "nothing was quarantined; the run did no work to recover");

            return (true, null);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static Process Launch(string planPath, string stateParent, string cacheDir, bool resume, bool redirect)
    {
        string host = ResolveHost();
        string cli = Path.Combine(AppContext.BaseDirectory, "coppice.dll");
        Assert.True(File.Exists(cli), $"the CLI assembly is not beside the test host at {cli}");

        var psi = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = redirect,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(cli);
        psi.ArgumentList.Add("apply");
        psi.ArgumentList.Add("--plan");
        psi.ArgumentList.Add(planPath);
        psi.ArgumentList.Add("--yes");
        if (resume)
        {
            psi.ArgumentList.Add("--resume");
        }

        // Point the child's whole world at the temp tree: its state root, and the cache it will resolve via
        // NUGET_PACKAGES (which the child's own `dotnet nuget locals` also honours, so tool and env agree).
        psi.Environment["LOCALAPPDATA"] = stateParent;
        psi.Environment["XDG_DATA_HOME"] = stateParent;
        psi.Environment["NUGET_PACKAGES"] = cacheDir;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        return new Process { StartInfo = psi };
    }

    /// <summary>
    /// A host that can run the net10.0 CLI. <c>DOTNET_HOST_PATH</c> is what <c>dotnet test</c> sets and is
    /// the right answer; the running process's own path is the fallback when it is the shared host.
    /// </summary>
    private static string ResolveHost()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath)
            && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    /// <summary>Polls the journal for the first intent line, so the kill lands with a mutation in flight.</summary>
    private static bool WaitForIntent(string journalPath, Process child)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);

        while (DateTime.UtcNow < deadline)
        {
            if (child.HasExited)
            {
                return false;
            }

            if (File.Exists(journalPath))
            {
                try
                {
                    foreach (string line in File.ReadLines(journalPath))
                    {
                        if (line.Contains("\"phase\":\"intent\"", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
                catch (IOException)
                {
                    // The child holds the file open in append mode; a sharing violation here just means
                    // "not yet", and the next poll retries.
                }
            }

            Thread.Sleep(2);
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp tree is not worth failing the test over; the OS reclaims it eventually.
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }
}

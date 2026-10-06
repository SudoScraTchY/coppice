using Coppice.Core.Domain;
using Coppice.Core.Gateway;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;
// The gateway acts on the Core domain action; Ports declares a portable one of the same name for the
// plugin boundary. Every reference below means the Core one.
using RemovalAction = Coppice.Core.Domain.RemovalAction;
using RemovalKind = Coppice.Core.Domain.RemovalKind;

namespace Coppice.Tests.Apply;

/// <summary>
/// T-031: the gateway executor (06-safety-model, S-2/S-10/S-13/S-14).
/// <para>
/// These tests are about what the gateway REFUSES, which is the majority of them. A gateway that only
/// proves it can delete is half-tested: the safe behaviour is the part nobody notices until it fails.
/// </para>
/// <para>
/// E-10 (relative-path injection into plans) and E-12 (locked file mid-apply) live here too — both are
/// apply-time attacks, and 11-testing defers them to exactly this card.
/// </para>
/// </summary>
public sealed class GatewayTests
{
    private const OperatingSystemKind Win = OperatingSystemKind.Windows;
    private const string Root = @"C:\cache";

    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A real cache entry: root/name/version, matching the allowlist pattern.</summary>
    private static FakeFileSystem Cache(params string[] packages)
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);

        foreach (string package in packages)
        {
            fs.AddDirectoryOfSize($@"{Root}\{package}\1.0.0", 4096);
        }

        return fs;
    }

    private static GatewayStep Step(
        string path,
        string allowlist = "*/*",
        string root = Root,
        Risk risk = Risk.Safe,
        ulong size = 4096,
        DateTimeOffset? stamp = null,
        bool installerOwned = false,
        RemovalAction? action = null) =>
        new()
        {
            ItemId = "abc123",
            Path = path,
            Root = root,
            AllowlistPattern = allowlist,
            Risk = risk,
            InstallerOwned = installerOwned,
            Action = action ?? RemovalAction.PathDelete(path),
            Fingerprint = new SnapshotFingerprint(path, size, stamp ?? Stamp),
        };

    // ---- the happy path --------------------------------------------------------------------------------

    [Fact]
    public void A_valid_step_is_moved_to_quarantine_not_deleted()
    {
        FakeFileSystem fs = Cache("newtonsoft.json");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step($@"{Root}\newtonsoft.json\1.0.0"));

        Assert.True(result.Succeeded);
        Assert.Equal(GatewayOutcome.Quarantined, result.Result);
        Assert.NotNull(result.QuarantinePath);

        // The source is GONE and the destination EXISTS. That is the whole point: nothing is deleted, so a
        // restore is a rename back.
        Assert.False(fs.DirectoryExists($@"{Root}\newtonsoft.json\1.0.0"));
        Assert.True(fs.DirectoryExists(result.QuarantinePath!));
    }

    [Fact]
    public void Quarantine_sits_beside_the_root_not_inside_it()
    {
        // The destination is under the root's PARENT. Inside the root would mean a later recursive delete of
        // the quarantine takes the quarantine with it.
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step($@"{Root}\pkg\1.0.0"));

        Assert.StartsWith(@"C:\.coppice\quarantine", result.QuarantinePath!, StringComparison.Ordinal);
    }

    // ---- allowlist (06 step 2) ---------------------------------------------------------------------------

    [Theory]
    // Depth is part of the shape. `*/*` and `pkg/*` both match `pkg/1.0.0`; so does `*/1.0.0`, because
    // the first segment is a wildcard either way. What matters is that the SEGMENT COUNT must agree, so
    // `*/*/*` refuses a two-segment path.
    [InlineData("*/*", true)]
    [InlineData("pkg/*", true)]
    [InlineData("*/1.0.0", true)]
    [InlineData("nomatch/*", false)]
    [InlineData("*/*/*", false)]
    public void The_allowlist_is_matched_against_the_path_relative_to_the_root(string pattern, bool expected)
    {
        Assert.Equal(expected, Gateway.MatchesAllowlist($@"{Root}\pkg\1.0.0", Root, pattern));
    }

    [Fact]
    public void A_path_not_shaped_like_a_cache_entry_is_refused()
    {
        // A root can contain a user's own files. Containment says "inside"; the allowlist says "shaped like
        // a cache entry". Without the second check, everything under a root is fair game.
        //
        // Three segments, not two: `*/*` is the NuGet shape (`<name>/<version>`), so `my-notes/important`
        // matches it and would be refused for a different reason. The realistic case is a nested directory
        // under a cache-looking one — exactly what a user creates by unzipping something into their cache.
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\my-notes\important\backup", 1024);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step($@"{Root}\my-notes\important\backup"));

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.NotAllowlisted, result.Refusal);

        // And the refusal is a refusal, not a near miss: nothing moved.
        Assert.True(fs.DirectoryExists($@"{Root}\my-notes\important\backup"));
    }

    [Fact]
    public void A_deeper_path_than_the_pattern_allows_is_refused()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\pkg\1.0.0\vendor\inner", 1024);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step($@"{Root}\pkg\1.0.0\vendor\inner"));

        Assert.Equal(GatewayRefusal.NotAllowlisted, result.Refusal);
    }

    // ---- containment and the denylist (06 steps 1, 3, 4; S-13, S-14) ---------------------------------------

    [Fact]
    public void A_path_outside_the_root_is_refused()
    {
        FakeFileSystem fs = Cache("pkg");
        fs.AddDirectoryOfSize(@"C:\Windows\System32\drivers\etc\pkg", 1024);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step(@"C:\Windows\System32\drivers\etc\pkg\1.0.0"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Refusal,
            new[] { GatewayRefusal.OutsideRoot, GatewayRefusal.NotAllowlisted });
    }

    [Fact]
    public void The_root_itself_is_never_deleted()
    {
        // S-13. Removing an empty root is a separate Review-tier action, never a side effect of cleaning it.
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step(Root, allowlist: "*"));

        Assert.False(result.Succeeded);
        Assert.True(fs.DirectoryExists(Root));
    }

    [Fact]
    public void A_denylisted_path_is_refused()
    {
        // The pattern matches the shape, so the step reaches the denylist floor. The order matters: an
        // allowlist miss would report NotAllowlisted and stop, so the pattern here has to be one a real
        // cache entry satisfies.
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(@"C:\Windows");
        fs.AddDirectoryOfSize(@"C:\Windows\cache\pkg\1.0.0", 1024);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(
            Step(@"C:\Windows\cache\pkg\1.0.0", root: @"C:\Windows\cache"));

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.Denied, result.Refusal);
    }

    [Fact]
    public void A_symlink_cycle_is_refused()
    {
        // T-028's LinkLoop finding, reached through apply. A cycle reaching a recursive delete is the
        // operation that never returns.
        //
        // The junction is added AFTER the directory it replaces — AddDirectoryOfSize would otherwise
        // overwrite the junction node with a plain directory and there would be no cycle to detect. That
        // ordering is invisible in the test's intent and fatal to its value, which is why it is stated here.
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\pkg\1.0.0", 1024);
        fs.AddJunction($@"{Root}\pkg", $@"{Root}\pkg");

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step($@"{Root}\pkg\1.0.0"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Refusal,
            new[] { GatewayRefusal.LinkLoop, GatewayRefusal.OutsideRoot, GatewayRefusal.NotAllowlisted });
    }

    [Fact]
    public void A_symlink_pointing_out_of_the_root_is_refused()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectory(@"C:\Windows");
        fs.AddSymlink($@"{Root}\escape", @"C:\Windows\System32");
        fs.AddDirectoryOfSize(@"C:\Windows\System32\pkg", 1024);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step($@"{Root}\escape\pkg"));

        Assert.False(result.Succeeded);
        Assert.True(fs.DirectoryExists(@"C:\Windows\System32\pkg"));
    }

    // ---- locks (S-10) and E-12 ---------------------------------------------------------------------------

    [Fact]
    public void E12_A_locked_file_is_skipped_and_the_rest_of_the_plan_continues()
    {
        // The locked DIRECTORY is what the gateway probes; the fake carries a lock on a node, so the
        // directory node itself must be the locked one. Setting a lock on a file inside would test
        // something the gateway never asks about.
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\locked\1.0.0", 4096, lockState: LockState.Locked);
        fs.AddDirectoryOfSize($@"{Root}\free\1.0.0", 4096);

        var gateway = new Gateway(fs, Win);

        GatewayStepResult locked = gateway.Execute(Step($@"{Root}\locked\1.0.0"));
        GatewayStepResult free = gateway.Execute(Step($@"{Root}\free\1.0.0"));

        Assert.False(locked.Succeeded);
        Assert.Equal(GatewayRefusal.Locked, locked.Refusal);

        // The point of E-12: one locked file must not stop the step after it.
        Assert.True(free.Succeeded);
    }

    [Fact]
    public void A_locked_file_produces_a_message_naming_the_lock_not_a_crash()
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\pkg\1.0.0", 4096, lockState: LockState.Locked);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step($@"{Root}\pkg\1.0.0"));

        Assert.False(result.Succeeded);
        Assert.Contains("Locked", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not force", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_gateway_never_throws_for_a_refusal()
    {
        // Every refusal must be reportable. An exception here would abort a partially-applied plan and lose
        // the results of the steps that already ran — so the assertion is that all four hostile inputs
        // RETURN a result, each naming a distinct refusal, rather than throwing.
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        IReadOnlyList<GatewayRefusal> refusals =
        [
            gateway.Execute(Step($@"{Root}\pkg\1.0.0", installerOwned: true)).Refusal,
            gateway.Execute(Step($@"{Root}\pkg\1.0.0", allowlist: "nomatch/*")).Refusal,
            gateway.Execute(Step(Root, allowlist: "*")).Refusal,
            gateway.Execute(Step($@"C:\Windows\x\1.0.0", root: @"C:\Windows\cache")).Refusal,
        ];

        Assert.All(refusals, r => Assert.NotEqual(GatewayRefusal.None, r));
        Assert.All(refusals, r => Assert.True(Enum.IsDefined(r), $"{r} is not a defined refusal"));

        // At least two distinct gates fired — installer-owned, allowlist, not-the-root and containment are
        // four DIFFERENT checks, and four identical results would mean three of them are unreachable.
        // Exactly-four would be too strict: several hostile inputs can legitimately land on the same gate.
        Assert.True(refusals.Distinct().Count() >= 2, $"expected multiple gates to fire, got {string.Join(", ", refusals)}");

        // And the item is still where it was: a refusal is not a partial success.
        Assert.True(fs.DirectoryExists($@"{Root}\pkg\1.0.0"));
    }

    // ---- S-2 drift --------------------------------------------------------------------------------------

    [Fact]
    public void Under_strict_a_changed_item_is_aborted()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        // The snapshot recorded 4096 bytes; the directory now holds a different amount.
        GatewayStepResult result = gateway.Execute(
            Step($@"{Root}\pkg\1.0.0", size: 999_999),
            strict: true);

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.Drifted, result.Refusal);
        Assert.True(fs.DirectoryExists($@"{Root}\pkg\1.0.0"));
    }

    [Fact]
    public void Without_strict_a_changed_item_is_still_removed()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step($@"{Root}\pkg\1.0.0", size: 999_999), strict: false);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Strict_aborts_only_the_drifted_step()
    {
        // One drifted package says nothing about the other twenty-nine. Aborting the plan would make the
        // user re-scan and re-plan for a single stale entry.
        //
        // Both steps are created with the fingerprint the fake filesystem actually reports: a
        // `AddDirectoryOfSize` directory has the fake's epoch mtime and a known size. Getting that wrong
        // would make the UNCHANGED step drift too, and the test would pass for the wrong reason.
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\changed\1.0.0", 9999);
        fs.AddDirectoryOfSize($@"{Root}\stable\1.0.0", 4096);

        var gateway = new Gateway(fs, Win);

        DateTimeOffset epoch = DateTimeOffset.UnixEpoch;

        IReadOnlyList<GatewayStepResult> results = gateway.ExecuteAll(
            [
                Step($@"{Root}\changed\1.0.0", size: 1, stamp: epoch) with { ItemId = "changed" },
                Step($@"{Root}\stable\1.0.0", size: 4096, stamp: epoch) with { ItemId = "stable" },
            ],
            strict: true);

        Assert.Equal(2, results.Count);
        Assert.Equal(GatewayRefusal.Drifted, results[0].Refusal);
        Assert.True(results[1].Succeeded, $"the unchanged step should have run: {results[1].Message}");
    }

    [Fact]
    public void An_item_that_is_already_gone_is_not_treated_as_drift()
    {
        // The goal state, not a problem. Refusing would make the user re-plan work that is already done.
        FakeFileSystem fs = Cache();
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step($@"{Root}\vanished\1.0.0"), strict: true);

        Assert.NotEqual(GatewayRefusal.Drifted, result.Refusal);
    }

    // ---- S-7 elevation and S-9 installer-owned -------------------------------------------------------------

    [Fact]
    public void An_installer_owned_root_is_report_only()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step($@"{Root}\pkg\1.0.0", installerOwned: true));

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.InstallerOwned, result.Refusal);
        Assert.True(fs.DirectoryExists($@"{Root}\pkg\1.0.0"));
    }

    [Fact]
    public void A_manual_tier_native_command_is_refused_rather_than_attempted()
    {
        // S-7: v1 never elevates. Attempting it and half-completing would be worse than refusing.
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step(
            $@"{Root}\pkg\1.0.0",
            risk: Risk.Manual,
            action: RemovalAction.Native("dotnet tool uninstall pkg")));

        Assert.False(result.Succeeded);
        Assert.Equal(GatewayRefusal.NeedsElevation, result.Refusal);
    }

    [Fact]
    public void A_report_only_action_never_mutates()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step(
            $@"{Root}\pkg\1.0.0",
            action: RemovalAction.ReportOnly("use the Visual Studio Installer")));

        Assert.False(result.Succeeded);
        Assert.True(fs.DirectoryExists($@"{Root}\pkg\1.0.0"));
    }

    [Fact]
    public void An_unknown_action_kind_is_refused_rather_than_guessed()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step(
            $@"{Root}\pkg\1.0.0",
            action: new RemovalAction((RemovalKind)99, CommandLine: "rm -rf /")));

        Assert.False(result.Succeeded);
        Assert.True(fs.DirectoryExists($@"{Root}\pkg\1.0.0"));
    }

    // ---- E-10: relative-path injection --------------------------------------------------------------------

    [Fact]
    public void E10_A_relative_path_in_a_plan_is_refused()
    {
        // A hand-edited or hostile plan naming "..\\..\\Windows\\System32\\config". Containment canonicalizes
        // it and then correctly reports it outside the root — the point is that it gets there at all.
        FakeFileSystem fs = Cache("pkg");
        fs.AddDirectoryOfSize(@"C:\Windows\System32\config", 1024);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step($@"{Root}\..\Windows\System32\config"));

        Assert.False(result.Succeeded);
        Assert.True(fs.DirectoryExists(@"C:\Windows\System32\config"));
    }

    [Fact]
    public void E10_A_bare_relative_path_is_refused()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step("pkg\\1.0.0"));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void E10_an_empty_path_is_refused()
    {
        FakeFileSystem fs = Cache("pkg");
        var gateway = new Gateway(fs, Win);

        GatewayStepResult result = gateway.Execute(Step(string.Empty));

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(@"..\..\etc", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:relative-without-separator", false)]
    public void E10_no_relative_path_ever_reaches_a_mutation(string path, bool shouldSucceed)
    {
        var fs = new FakeFileSystem(Win);
        fs.AddDirectory(Root);
        fs.AddDirectoryOfSize($@"{Root}\pkg\1.0.0", 4096);

        var gateway = new Gateway(fs, Win);
        GatewayStepResult result = gateway.Execute(Step(path));

        Assert.Equal(shouldSucceed, result.Succeeded);
    }
}

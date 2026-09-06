namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The seven findings of the 2026-09-06 FOLLOW-UP review, as permanent regressions. The reviewer
/// shipped six of these as a throwaway diagnostic project outside the solution; they belong in the
/// suite, because every one of them is a way to lose a file the user cares about.
/// </summary>
public sealed class FollowupReviewTests
{
    private static Task<ArtifactRef> Write(IArtifactStore store, string path, string text)
        => store.CreateAsync(path, ArtifactKind.FileSet, path,
            async stream => await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text)),
            CancellationToken.None);

    // ── F1: a link inside the workspace pointing at the workspace's own state ──────────

    // The reserved-folder check read the path the caller typed. A junction named `alias` pointing at
    // `.enactive` contains no reserved segment, so `alias/state.txt` passed — and the write landed in
    // the state folder, undo backups included.
    [Fact]
    public async Task A_link_cannot_be_used_to_reach_the_reserved_state_folder()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/state.txt", "trusted state");

        var alias = fx.PathOf("alias");
        if (!TryJunction(alias, fx.PathOf(".enactive")))
        {
            Assert.False(OperatingSystem.IsWindows(),
                "could not create a directory junction — this test asserted nothing");
            return;
        }

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => Write(fx.Artifacts, "alias/state.txt", "untrusted replacement"));

            Assert.Equal("trusted state", File.ReadAllText(fx.PathOf(".enactive/state.txt")));
        }
        finally
        {
            try { Directory.Delete(alias); } catch { }
        }
    }

    // ── F2: Apply used a fixed temp name and overwrote whatever was there ──────────────

    [Fact]
    public async Task Applying_a_change_leaves_a_neighbouring_file_alone()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.txt.enactive-tmp", "user data");

        var staging = new StagingArtifactStore(fx.Root);
        var change = await Write(staging, "doc.txt", "proposal");

        Assert.True(staging.Apply(change.Id).Applied);
        Assert.True(fx.Exists("doc.txt.enactive-tmp"), "Apply deleted the pre-existing neighbour");
        Assert.Equal("user data", fx.Read("doc.txt.enactive-tmp"));
        Assert.Equal("proposal", fx.Read("doc.txt"));
    }

    // Nothing of ours may be left behind either — a temp file that survives its own write is just
    // litter in the user's folder.
    [Fact]
    public async Task Applying_a_change_leaves_no_temp_file_behind()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);
        var change = await Write(staging, "doc.txt", "proposal");

        Assert.True(staging.Apply(change.Id).Applied);
        Assert.Empty(Directory.GetFiles(fx.Root, "*.enactive-tmp", SearchOption.AllDirectories));
    }

    // ── F3: reverting one step must not undo another step's accepted work ─────────────

    // Two steps share one store. A takes a checkpoint, B takes a later one, both write the same
    // path, B's work is accepted — and then A is rejected. Reverting A used to select every write
    // after A's checkpoint, B's included, and put the file back to A's "before". The hash check did
    // not catch it: the file matched B's write exactly, so it looked untouched.
    [Fact]
    public async Task Reverting_a_rejected_step_does_not_undo_a_later_steps_write()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.txt", "original");

        var stepA = fx.Artifacts.Checkpoint();
        var stepB = fx.Artifacts.Checkpoint();

        await Write(fx.Artifacts, "doc.txt", "step A");
        await Write(fx.Artifacts, "doc.txt", "step B accepted");

        var report = await fx.Artifacts.RevertToAsync(stepA, new[] { "doc.txt" }, default);

        Assert.Equal("step B accepted", fx.Read("doc.txt"));
        Assert.Contains("doc.txt", report.Kept);
        Assert.DoesNotContain("doc.txt", report.Reverted);
        Assert.True(stepB > stepA, "each checkpoint must be its own scope");
    }

    // The ordinary case still has to work, or the fix above would have bought safety by breaking
    // the feature: one step, its own writes, put back.
    [Fact]
    public async Task Reverting_a_step_still_undoes_that_steps_own_writes()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.txt", "original");

        var step = fx.Artifacts.Checkpoint();
        await Write(fx.Artifacts, "doc.txt", "first attempt");
        await Write(fx.Artifacts, "doc.txt", "second attempt");

        var report = await fx.Artifacts.RevertToAsync(step, new[] { "doc.txt" }, default);

        Assert.Equal("original", fx.Read("doc.txt"));
        Assert.Contains("doc.txt", report.Reverted);
    }

    // ── F4: a write that throws must not leave a truncated file ───────────────────────

    // FileMode.Create emptied the target before the first byte arrived, and the journal entry was
    // only added on success — so the file was destroyed AND undo did not know it had been touched.
    [Fact]
    public async Task A_write_that_fails_halfway_leaves_the_original_untouched()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.txt", "original");

        await Assert.ThrowsAsync<IOException>(() => fx.Artifacts.CreateAsync(
            "doc.txt", ArtifactKind.FileSet, "doc",
            async stream =>
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("partial"));
                throw new IOException("simulated write failure");
            },
            default));

        Assert.Equal("original", fx.Read("doc.txt"));
    }

    [Fact]
    public async Task A_failed_write_creates_no_file_where_there_was_none()
    {
        using var fx = new EngineFixture();

        await Assert.ThrowsAsync<IOException>(() => fx.Artifacts.CreateAsync(
            "new.txt", ArtifactKind.FileSet, "new",
            async stream =>
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("partial"));
                throw new IOException("simulated write failure");
            },
            default));

        Assert.False(fx.Exists("new.txt"));
        Assert.Empty(Directory.GetFiles(fx.Root, "*.enactive-tmp", SearchOption.AllDirectories));
    }

    // write_file's success line is what the reviewer is handed as ground truth, so it may only
    // promise a recoverable previous version when there actually is one.
    [Fact]
    public async Task Replacing_a_file_reports_whether_the_old_version_is_recoverable()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "the original");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"replace it"}"""),
            Turn.Calls1("write_file", """{"path":"doc.md","content":"the replacement"}""", "c1"),
            Turn.Says("Replaced it."));

        var events = await fx.RunAsync(fx.Build(provider), "replace doc.md");

        var result = Assert.Single(events.OfKind(EventKind.ToolResult));
        Assert.Contains("REPLACED", result.Summary, StringComparison.Ordinal);
        Assert.Contains("can be restored", result.Summary, StringComparison.Ordinal);
        Assert.True(fx.Artifacts.CanRestore("doc.md"));
    }

    // ── F5: an unrecovered tool failure must not report Completed ─────────────────────

    [Fact]
    public async Task A_tool_failure_the_model_talked_past_does_not_complete()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read required input"}"""),
            Turn.Calls1("read_file", """{"path":"missing.txt"}""", "c1"),
            Turn.Says("Done"));

        var events = await fx.RunAsync(fx.Build(provider), "Read required input missing.txt");

        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains("failed"));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.TaskCompleted);

        var terminal = events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);
        Assert.Equal(RunOutcomeKind.Incomplete, terminal.Outcome());
    }

    // Retrying the same call and getting it to work IS recovery — that must still be a clean run,
    // or every agent that recovers from a transient failure would be reported as having failed.
    [Fact]
    public async Task The_same_call_succeeding_later_clears_the_failure()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read the input"}"""),
            Turn.Calls1("read_file", """{"path":"data.txt"}""", "c1"),
            // The file appears between the two attempts, standing in for whatever made the first fail.
            Turn.Calls1("write_file", """{"path":"data.txt","content":"now it exists"}""", "c2"),
            Turn.Calls1("read_file", """{"path":"data.txt"}""", "c3"),
            Turn.Says("Read it."));

        var events = await fx.RunAsync(fx.Build(provider), "read data.txt");

        var terminal = events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);
        Assert.Equal(RunOutcomeKind.Completed, terminal.Outcome());
    }

    [Fact]
    public async Task A_run_with_no_tool_failures_still_completes()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a note"}"""),
            Turn.Calls1("write_file", """{"path":"note.md","content":"hello"}""", "c1"),
            Turn.Says("Wrote it."));

        var events = await fx.RunAsync(fx.Build(provider), "write a note");

        var terminal = events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);
        Assert.Equal(RunOutcomeKind.Completed, terminal.Outcome());
    }

    // ── F6: one file, one identity ────────────────────────────────────────────────────

    [Theory]
    [InlineData("./doc.txt", "doc.txt")]
    [InlineData("doc.txt", "./doc.txt")]
    [InlineData("sub/../doc.txt", "doc.txt")]
    public async Task A_staged_write_is_readable_under_an_equivalent_path(string wrote, string read)
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        await Write(staging, wrote, "proposal");

        Assert.Equal("proposal", await staging.TryReadPendingAsync(read, default));
    }

    // Two proposals for one file under different spellings are one chain, so the second one's diff
    // is against the first — and applying them out of order is refused.
    [Fact]
    public async Task Equivalent_paths_are_one_chain_of_proposals()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        var first = await Write(staging, "doc.txt", "first");
        var second = await Write(staging, "./doc.txt", "second");

        Assert.Equal("An earlier change to this file is still pending — apply or reject that one first.",
            staging.Apply(second.Id).Conflict);
        Assert.Single(staging.PendingPaths);

        Assert.True(staging.Apply(first.Id).Applied);
        Assert.True(staging.Apply(second.Id).Applied);
        Assert.Equal("second", fx.Read("doc.txt"));
    }

    // ── plumbing ──────────────────────────────────────────────────────────────────────

    /// <summary>A directory junction on Windows, a symlink elsewhere. False when the OS refuses.</summary>
    private static bool TryJunction(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }

            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            process.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}

namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Run 7f3435, 2026-10-09: a step asked to delete a test file the run had found - 24,733 bytes of older tests - and the
/// person was asked "Approve tool 'delete_file'? {"path":...}" and nothing more. They allowed it; the step rewrote the
/// file as 954 bytes. And the change limit, which had allowed an edit of the same file earlier in the step, did not ask
/// about the deletion at all. A removal now says what it takes, and an edit allowed is not a removal allowed.
/// </summary>
public sealed class ARemovalSaysWhatItTakesTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"files"}""";

    private static string Content(int lines) => string.Concat(Enumerable.Range(1, lines).Select(i => $"test {i}\n"));

    // ── the question put to the person ────────────────────────────────────

    [Fact]
    public async Task Deleting_a_file_the_run_found_says_so_with_its_size()
    {
        using var fx = new EngineFixture();
        fx.Write("Tests/GameTests.cs", Content(40));
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("edit_file", """{"path":"Tests/GameTests.cs","old_string":"test 1\n","new_string":"test one\n"}""", "e1"),
            Turn.Calls1("delete_file", """{"path":"Tests/GameTests.cs"}""", "d1"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "tidy the tests");

        var asked = Assert.Single(fx.Decisions.Requests, r => r.Topic.Contains("delete_file", StringComparison.Ordinal));
        Assert.Contains("'Tests/GameTests.cs' existed before this run (", asked.FullText, StringComparison.Ordinal);
        Assert.Contains("this run has changed it since - now", asked.FullText, StringComparison.Ordinal);
        Assert.Contains("40 lines", asked.FullText, StringComparison.Ordinal);
        Assert.Contains("existed before this run", asked.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_file_the_run_made_says_that()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"probe.txt","content":"a\nb\n"}""", "w1"),
            Turn.Calls1("delete_file", """{"path":"probe.txt"}""", "d1"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "make and remove a probe");

        var asked = Assert.Single(fx.Decisions.Requests, r => r.Topic.Contains("delete_file", StringComparison.Ordinal));
        Assert.Contains("'probe.txt' was made by this run (", asked.FullText, StringComparison.Ordinal);
    }

    /// <summary>A file the run has not touched yet is said to be as the run found it.</summary>
    [Fact]
    public async Task Deleting_an_untouched_file_says_the_run_has_not_changed_it()
    {
        using var fx = new EngineFixture();
        fx.Write("old.txt", Content(3));
        var worker = new FakeChatProvider(Turn.Says(QuickAction), Turn.Calls1("delete_file", """{"path":"old.txt"}""", "d1"), Turn.Says("Done."));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "remove old.txt");

        Assert.Contains("existed before this run (", Assert.Single(fx.Decisions.Requests).FullText, StringComparison.Ordinal);
        Assert.Contains("this run has not changed it", fx.Decisions.Requests[0].FullText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing written in the scratch is recorded, so whether a file there was there before the run is not known: it is
    /// said to be in the scratch, not to have "existed before this run" (run f08f1e, 2026-10-09).
    /// </summary>
    [Fact]
    public async Task Deleting_a_file_in_the_scratch_says_where_it_is_and_nothing_it_cannot_know()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":".enactive/scratch/probe.cs","content":"a\nb\n"}""", "w1"),
            Turn.Calls1("delete_file", """{"path":".enactive/scratch/probe.cs"}""", "d1"), Turn.Says("Done."));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "probe and tidy");

        var asked = Assert.Single(fx.Decisions.Requests, r => r.Topic.Contains("delete_file", StringComparison.Ordinal));
        Assert.Contains("'.enactive/scratch/probe.cs' is in the scratch, the steps' own working space (", asked.FullText, StringComparison.Ordinal);
        Assert.DoesNotContain("existed before this run", asked.FullText, StringComparison.Ordinal);
    }

    // ── which paths a call takes away ─────────────────────────────────────

    [Fact]
    public void A_move_takes_away_where_it_moves_from_and_a_write_takes_nothing()
    {
        var move = new MoveFileTool().Definition;
        Assert.Equal(["a.txt"], ChangeLimitGuard.RemovedPaths(new ToolCall("m", "move_file", """{"from":"a.txt","to":"b.txt"}"""), move));
        Assert.Equal(["a.txt"], ChangeLimitGuard.RemovedPaths(new ToolCall("d", "delete_file", """{"path":"a.txt"}"""), new DeleteFileTool().Definition));
        Assert.Empty(ChangeLimitGuard.RemovedPaths(new ToolCall("w", "write_file", """{"path":"a.txt","content":"x"}"""), new WriteFileTool().Definition));
    }

    // ── the change limit ──────────────────────────────────────────────────

    /// <summary>An edit of a file allowed in a step does not allow taking the file away: the removal is asked about.</summary>
    [Fact]
    public async Task An_edit_allowed_is_not_a_removal_allowed()
    {
        using var fx = new EngineFixture();
        fx.Write("Tests/GameTests.cs", Content(5));
        var planner = new FakeChatProvider { WhenExhausted = Turn.Says("""{"allow":true,"reason":"fine"}""") };
        var guard = new ChangeLimitGuard("Add tests. Do not change the game code.", ["Do not change the game code."], planner,
            new ModelRef("p", "m"), new RunBudget(null, DateTimeOffset.UtcNow), 1000);
        var store = new DiskArtifactStore(fx.Workspace).BeginStep();
        Task<ChangeLimitGuard.Decision> Check(string tool, ToolDefinition definition, object args)
            => guard.CheckAsync(1, "Add tests", null, new ToolCall("c", tool, JsonSerializer.Serialize(args)), definition, store, fx.Workspace.RootPath, default);

        await Check("edit_file", new EditFileTool().Definition, new { path = "Tests/GameTests.cs", old_string = "test 1", new_string = "x" });
        await Check("edit_file", new EditFileTool().Definition, new { path = "Tests/GameTests.cs", old_string = "test 2", new_string = "y" });
        Assert.Single(planner.Requests);

        await Check("delete_file", new DeleteFileTool().Definition, new { path = "Tests/GameTests.cs" });

        Assert.Equal(2, planner.Requests.Count);
        Assert.Contains("It takes Tests/GameTests.cs away", string.Join("\n", planner.Requests[1].Messages.Select(m => m.Content)), StringComparison.Ordinal);
    }
}

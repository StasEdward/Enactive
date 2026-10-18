namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Run da4b3b, 2026-10-19, step 3: asked to check that each new test fails when what it tests is broken - "by breaking it
/// temporarily and putting it back" - the worker broke <c>GetWinningMove</c>, ran the tests, put it back with restore_file,
/// ran them again; rewrote its tests; and broke the same line again. The stall guard counted that second break a repeat
/// (a write was identified by the call alone, though another file had changed since), the test run after it a repeat
/// (it shared a generation with the run before the break), and stopped the step on its next call - the restore that
/// would have undone the break. The step ended Incomplete, nothing put the break back, and the final checks failed on it.
/// </summary>
public sealed class PuttingBackItsOwnChangeIsNotStuckTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"check the tests catch a break"}""";
    private const string Original = "int Winning() => FindWin();\n";

    private static Worker Tester()
        => EngineFixture.WorkerWith("write_file", "edit_file", "read_file", "restore_file", "run_command");

    private static Turn Break(string id) => Turn.Calls1("edit_file",
        """{"path":"Game.cs","old_string":"FindWin()","new_string":"null"}""", id);

    private static Turn PutBack(string id) => Turn.Calls1("restore_file", """{"path":"Game.cs"}""", id);

    private static Turn RunTests(string id) => Turn.Calls1("run_command", """{"command":"echo tests ran"}""", id);

    private static Turn WriteTests(string content, string id)
        => Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = "GameTests.cs", content }), id);

    private static EngineFixture WithGame()
    {
        var fx = new EngineFixture();
        fx.Write("Game.cs", Original);
        return fx;
    }

    private static bool Stuck(List<WorkEvent> events)
        => events.Any(e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("repeating tool calls", StringComparison.Ordinal));

    /// <summary>The run's sequence, call for call: it reaches its second restore, and the file ends as it began.</summary>
    [Fact]
    public async Task The_runs_break_and_put_back_twice_ends_with_the_file_as_it_began()
    {
        using var fx = WithGame();
        var provider = new FakeChatProvider(Turn.Says(QuickPlan),
            WriteTests("first version", "w1"), RunTests("t1"),
            Break("b1"), RunTests("t2"), PutBack("p1"), RunTests("t3"),
            WriteTests("second version", "w2"), RunTests("t4"),
            Break("b2"), RunTests("t5"), PutBack("p2"),
            Turn.Says("Each test fails when what it tests is broken; the code is back as it was."));

        var events = await fx.RunAsync(fx.Build(provider, Tester()), "write tests and check each fails when broken");

        Assert.False(Stuck(events), events.Text());
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.ToolInvoked && e.Summary.StartsWith("restore_file", StringComparison.Ordinal)));
        Assert.Equal(Original, fx.Read("Game.cs").Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The same edit made after another write is applied to a workspace it has not been applied to - not a repeat, and
    /// the run after it looks at a tree nobody has looked at. Under the old identity the second break and the run after
    /// it were repeats one and two, and one more look stopped the step with the break in place.
    /// </summary>
    [Fact]
    public async Task The_same_edit_after_another_write_is_not_a_repeat_nor_the_command_after_it()
    {
        using var fx = WithGame();
        var provider = new FakeChatProvider(Turn.Says(QuickPlan),
            Break("b1"), RunTests("t1"), PutBack("p1"), RunTests("t2"),
            WriteTests("another test", "w1"), RunTests("t3"),
            Break("b2"), RunTests("t4"), RunTests("t5"), PutBack("p2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, Tester()), "check the test fails when broken");

        Assert.False(Stuck(events), events.Text());
        Assert.Equal(Original, fx.Read("Game.cs").Replace("\r\n", "\n"));
        // Told of no repeat until the run made twice in a row with nothing between (t5).
        var told = provider.Requests.Last().Messages
            .Where(m => m.Role == Enactive.Core.Chat.ChatRole.Tool && (m.Content ?? "").Contains("already made in this step", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(told);
    }

    /// <summary>
    /// A turn that puts back a change the step itself made is run, even with the stall count at its limit: undoing its own
    /// change is progress, and stopping before it leaves the workspace as the step broke it. Here the only repeats are of
    /// the same run, until the restore - made before, to the same state - would be the third.
    /// </summary>
    [Fact]
    public async Task A_turn_that_puts_back_the_steps_own_change_is_not_stopped_at_the_limit()
    {
        using var fx = WithGame();
        var provider = new FakeChatProvider(Turn.Says(QuickPlan),
            Break("b1"), RunTests("t1"), PutBack("p1"), RunTests("t2"),
            Break("b2"), RunTests("t3"), RunTests("t4"), RunTests("t5"),
            PutBack("p2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, Tester()), "check the test fails when broken");

        Assert.False(Stuck(events), events.Text());
        Assert.Equal(Original, fx.Read("Game.cs").Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A step that says it is done and repeats a call is ended on its message, the repeat not run - but a put-back of its
    /// own change is not a call "with nothing changed since": it changes the file back. Not run, the break stayed.
    /// </summary>
    [Fact]
    public async Task A_put_back_said_with_a_message_is_run_not_taken_for_a_finished_repeat()
    {
        using var fx = WithGame();
        var provider = new FakeChatProvider(Turn.Says(QuickPlan),
            Break("b1"), PutBack("p1"), Break("b2"),
            new Turn("The test caught it; putting the code back.", new[] { new ToolCall("p2", "restore_file", """{"path":"Game.cs"}""") }),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, Tester()), "check the test fails when broken");

        Assert.Equal(2, events.Count(e => e.Kind == EventKind.ToolInvoked && e.Summary.StartsWith("restore_file", StringComparison.Ordinal)));
        Assert.Equal(Original, fx.Read("Game.cs").Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The guard still guards: break, run, put back, run, round and round with nothing else changing comes back to states
    /// it has seen, and stalls. And it stops on a break, not before a put-back - the file is left as it began.
    /// </summary>
    [Fact]
    public async Task Breaking_and_putting_back_round_and_round_still_stalls()
    {
        using var fx = WithGame();
        var cycle = new List<Turn> { Turn.Says(QuickPlan) };
        for (var i = 0; i < 6; i++)
            cycle.AddRange([Break($"b{i}"), RunTests($"t{i}"), PutBack($"p{i}"), RunTests($"u{i}")]);
        var provider = new FakeChatProvider(cycle.ToArray()) { WhenExhausted = Break("bx") };

        var events = await fx.RunAsync(fx.Build(provider, Tester()), "make up your mind");

        Assert.True(Stuck(events), events.Text());
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(Original, fx.Read("Game.cs").Replace("\r\n", "\n"));
        Assert.InRange(events.Count(e => e.Kind == EventKind.ToolInvoked), 4, 12);
    }

    /// <summary>
    /// Letting a put-back through cannot become the way out of the guard: a turn that puts back and breaks again, over
    /// and over, renews nothing - the break made in the turn let through does not license the next.
    /// </summary>
    [Fact]
    public async Task Putting_back_and_breaking_again_in_one_turn_forever_still_stalls()
    {
        using var fx = WithGame();
        var both = new Turn(Calls: new[]
        {
            new ToolCall("p", "restore_file", """{"path":"Game.cs"}"""),
            new ToolCall("b", "edit_file", """{"path":"Game.cs","old_string":"FindWin()","new_string":"null"}""")
        });
        var provider = new FakeChatProvider(Turn.Says(QuickPlan), Break("b0")) { WhenExhausted = both };

        var events = await fx.RunAsync(fx.Build(provider, Tester()), "make up your mind");

        Assert.True(Stuck(events), events.Text());
        Assert.InRange(events.Count(e => e.Kind == EventKind.ToolInvoked), 3, 20);
    }

    // ── StepProgress itself ────────────────────────────────────────────────────

    private static readonly IReadOnlyList<ToolDefinition> Definitions =
        [new EditFileTool().Definition, new WriteFileTool().Definition, new RestoreFileTool().Definition, new RunCommandTool().Definition];

    private static ToolCall Edit(string path, string to) => new("e", "edit_file",
        JsonSerializer.Serialize(new { path, old_string = "x", new_string = to }));

    private static ToolCall Write(string path, string content) => new("w", "write_file", JsonSerializer.Serialize(new { path, content }));

    private static ToolCall Restore(string path) => new("r", "restore_file", JsonSerializer.Serialize(new { path }));

    private static readonly ToolCall Command = new("c", "run_command", """{"command":"build"}""");

    private static void Ran(StepProgress progress, ToolCall call, out bool advanced, bool changes = true)
    {
        progress.BeginTurn();
        advanced = progress.Advanced([call]);
        progress.Resulted(call, changes
            ? ToolResults.Ok("ok") with { WorkspaceEffect = WorkspaceEffect.Changed }
            : ToolResults.Ok("ok") with { WorkspaceEffect = WorkspaceEffect.Unknown }, null, null);
    }

    [Fact]
    public void An_edit_applied_after_another_file_changed_is_new_and_so_is_the_look_after_it()
    {
        var progress = new StepProgress(Definitions);
        Ran(progress, Edit("Game.cs", "broken"), out _);
        Ran(progress, Command, out _, changes: false);
        Ran(progress, Restore("Game.cs"), out _);
        Ran(progress, Command, out _, changes: false);
        Ran(progress, Write("GameTests.cs", "v2"), out _);
        Ran(progress, Command, out _, changes: false);

        Ran(progress, Edit("Game.cs", "broken"), out var edited);
        Ran(progress, Command, out var looked, changes: false);

        Assert.True(edited);
        Assert.True(looked);
        Assert.Equal(0, progress.Stalled);
    }

    [Fact]
    public void The_same_edits_alternating_with_nothing_else_changing_come_back_to_a_seen_state_and_stall()
    {
        var progress = new StepProgress(Definitions);
        var advancedAny = new List<bool>();
        for (var i = 0; i < 4; i++)
        {
            Ran(progress, Edit("Game.cs", "broken"), out var a);
            Ran(progress, Restore("Game.cs"), out var b);
            advancedAny.AddRange([a, b]);
        }

        Assert.Equal(new[] { true, true, true, false, false, false, false, false }, advancedAny);
    }

    [Fact]
    public void A_put_back_of_the_steps_own_change_is_known_and_once_let_through_is_not_renewed_by_its_own_turn()
    {
        var progress = new StepProgress(Definitions);
        // Broken, put back, broken again: the put-back now would be made again to a state it was made in - a repeat by
        // identity, and still not "nothing changed since".
        Ran(progress, Edit("Game.cs", "broken"), out _);
        Ran(progress, Restore("Game.cs"), out _);
        Ran(progress, Edit("Game.cs", "broken"), out _);

        Assert.True(progress.PutsBackOwnChange([Restore("Game.cs")]));
        Assert.False(progress.PutsBackOwnChange([Restore("Other.cs")]));
        Assert.False(progress.OnlyRepeats([Restore("Game.cs")]));

        // Let through, and broken again within the same turn: that break does not license the next put-back.
        progress.BeginTurn();
        progress.Advanced([Restore("Game.cs"), Edit("Game.cs", "broken")]);
        Assert.True(progress.LetThroughToPutBack([Restore("Game.cs"), Edit("Game.cs", "broken")]));
        progress.Resulted(Restore("Game.cs"), ToolResults.Ok("ok") with { WorkspaceEffect = WorkspaceEffect.Changed }, null, null);
        progress.Resulted(Edit("Game.cs", "broken"), ToolResults.Ok("ok") with { WorkspaceEffect = WorkspaceEffect.Changed }, null, null);

        Assert.False(progress.PutsBackOwnChange([Restore("Game.cs")]));
    }

    [Fact]
    public void A_put_back_that_could_not_put_anything_back_is_not_let_through_again()
    {
        var progress = new StepProgress(Definitions);
        Ran(progress, Write("New.cs", "made here"), out _);
        Assert.True(progress.PutsBackOwnChange([Restore("New.cs")]));

        progress.BeginTurn();
        progress.Advanced([Restore("New.cs")]);
        progress.Resulted(Restore("New.cs"), ToolResults.Fail("'New.cs' was made by this run"), null, null);

        Assert.False(progress.PutsBackOwnChange([Restore("New.cs")]));
    }

    [Fact]
    public void One_write_counts_as_new_in_a_bounded_number_of_states()
    {
        var progress = new StepProgress(Definitions);
        var counted = 0;
        for (var i = 0; i < 20; i++)
        {
            Ran(progress, Write($"other{i}.cs", "x"), out _);
            Ran(progress, Edit("Game.cs", "broken"), out var edited);
            if (edited) counted++;
        }

        Assert.Equal(StepProgress.StatesPerWrite, counted);
    }
}

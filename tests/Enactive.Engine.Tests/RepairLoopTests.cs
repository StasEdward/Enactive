namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Workers;
using Xunit;

/// <summary>
/// A step that breaks something and then fixes it, without being stopped for the fixing.
///
/// <para>On 2026-09-07 at 23:18 a step wrote a new test into <c>Program.cs</c>, got the braces
/// wrong, and left the project un-compilable. It then did precisely what it should: read the file
/// back, ran the build, saw <c>CS8803</c>, read the file again, edited it again, ran the build
/// again. On the eleventh call — a re-read, two turns into its SECOND repair attempt — the stall
/// detector stopped the step for "repeating tool calls it had already made", the dependent step was
/// skipped, and the run failed with the file still broken on disk.</para>
///
/// <para>Nothing had gone wrong except the counting. A repair loop is repeated calls BY
/// CONSTRUCTION — read, edit, build, read, edit, build — and only the edits differ. The reads and
/// the builds are not the calls that came before them: the file has been rewritten and the tree
/// rebuilt in between. So a successful write advances a generation, and a call that only OBSERVES
/// is identified together with the generation it observed.</para>
///
/// <para>The writes themselves carry no generation, and that is what these tests are mostly for:
/// the escape from a stall has to cost a write nobody has made before, or the guard is gone.</para>
/// </summary>
public sealed class RepairLoopTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"do the thing"}""";

    private static Turn Reads(string path, string id)
        => Turn.Calls1("read_file", $$"""{"path":"{{path}}"}""", id);

    private static Turn ReadsOn(string path, int offset, string id)
        => Turn.Calls1("read_file", $$"""{"offset":{{offset}},"path":"{{path}}"}""", id);

    /// <summary>
    /// The default fixture role does not carry <c>edit_file</c>, and a refused call never reaches
    /// the workspace — which would make every one of these tests pass or fail for the wrong reason.
    /// </summary>
    private static Worker Editor()
        => EngineFixture.WorkerWith("write_file", "edit_file", "read_file", "list_dir", "run_command");

    private static Turn Edits(string path, string from, string to, string id)
        => Turn.Calls1(
            "edit_file",
            $$"""{"path":"{{path}}","old_string":"{{from}}","new_string":"{{to}}"}""",
            id);

    /// <summary>
    /// The shape of the run that failed, turn for turn: two reads, two edits, an observation, and
    /// then the same two reads and another edit and another observation. Eleven calls. With the
    /// generation removed from the identity this stops on the eleventh.
    /// </summary>
    [Fact]
    public async Task A_step_that_edits_between_looking_is_not_called_stuck()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "one\ntwo\nthree\nfour\n");
        fx.Write("build.txt", "pretend this is a build log");

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            // First attempt: look, look again, change it twice, check.
            Reads("Program.cs", "r1"),
            ReadsOn("Program.cs", 3, "r2"),
            Edits("Program.cs", "one", "ONE", "e1"),
            Edits("Program.cs", "two", "TWO", "e2"),
            Reads("build.txt", "c1"),
            // Second attempt: the same two reads and the same check — of a file that has changed
            // twice since, which is the entire point.
            Reads("Program.cs", "r3"),
            ReadsOn("Program.cs", 3, "r4"),
            Edits("Program.cs", "three", "THREE", "e3"),
            Reads("build.txt", "c2"),
            // Third attempt begins. This is the call the engine used to kill the step on.
            Reads("Program.cs", "r5"),
            ReadsOn("Program.cs", 3, "r6"),
            Turn.Says("Fixed and building."));

        var events = await fx.RunAsync(fx.Build(provider, Editor()), "fix the test until it builds");

        Assert.Empty(events.OfKind(EventKind.ErrorObserved));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());

        // All eleven ran — the eleventh above all.
        Assert.Equal(11, events.Count(e => e.Kind == EventKind.ToolInvoked));
        Assert.Contains("THREE", fx.Read("Program.cs"));
    }

    /// <summary>
    /// The guard still guards. Nothing is written, so nothing has changed, so looking again is
    /// looking at the same thing — and three turns of it stops the step exactly as before.
    /// </summary>
    [Fact]
    public async Task Looking_again_at_something_nobody_touched_still_stalls()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "some notes");

        var repeat = Reads("notes.txt", "r");
        var provider = new FakeChatProvider(Turn.Says(QuickPlan), repeat) { WhenExhausted = repeat };

        var events = await fx.RunAsync(fx.Build(provider), "read the notes");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("repeating tool calls", stopped.Summary);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    /// <summary>
    /// The hole a generation could have opened, closed. If a write reset the ledger outright, two
    /// edits undoing each other forever would each look new again every time round. They do not:
    /// a write is identified by itself alone, so the second time it is made it is the same write.
    /// </summary>
    [Fact]
    public async Task Two_edits_undoing_each_other_are_still_stuck()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "alpha\n");

        var there = Edits("Program.cs", "alpha", "beta", "a");
        var back = Edits("Program.cs", "beta", "alpha", "b");

        var provider = new FakeChatProvider(Turn.Says(QuickPlan), there, back, there, back, there, back)
        {
            WhenExhausted = there
        };

        var events = await fx.RunAsync(fx.Build(provider, Editor()), "make up your mind");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("repeating tool calls", stopped.Summary);
        Assert.Contains("edit_file", stopped.Summary);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    /// <summary>
    /// A write that did NOT go through changed nothing, and must not buy another look. The engine
    /// advances the generation on the RESULT, not on the attempt.
    /// </summary>
    [Fact]
    public async Task An_edit_that_failed_does_not_buy_another_look()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "alpha\n");

        // 'old_string' is not in the file: edit_file fails and the file is untouched.
        var doomed = Edits("Program.cs", "nowhere-in-this-file", "x", "e");
        var look = Reads("Program.cs", "r");

        var provider = new FakeChatProvider(Turn.Says(QuickPlan), look, doomed, look, doomed, look, doomed)
        {
            WhenExhausted = look
        };

        var events = await fx.RunAsync(fx.Build(provider, Editor()), "try to edit it");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("repeating tool calls", stopped.Summary);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal("alpha\n", fx.Read("Program.cs").Replace("\r\n", "\n"));
    }

    /// <summary>
    /// One write buys one new look at each thing, not an unlimited licence: after the write, the
    /// same read three turns running stalls again from the new generation.
    /// </summary>
    [Fact]
    public async Task One_write_does_not_licence_endless_rereading()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "alpha\n");

        var look = Reads("Program.cs", "r");
        var write = Turn.Calls1("write_file", """{"path":"new.txt","content":"hello"}""", "w");

        var provider = new FakeChatProvider(Turn.Says(QuickPlan), look, write, look)
        {
            // The read, forever, from here on — all in one generation.
            WhenExhausted = look
        };

        var events = await fx.RunAsync(fx.Build(provider, Editor()), "read it, write once, then read forever");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("repeating tool calls", stopped.Summary);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());

        // read, write, read (new generation), then two repeats; the third is recognised and not run.
        Assert.Equal(5, events.Count(e => e.Kind == EventKind.ToolInvoked));
    }
}

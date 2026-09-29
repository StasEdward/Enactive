namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Stopping a step because it is stuck, not because it has been working for a while.
///
/// <para>The loop used to end after a flat twelve turns. On 2026-09-06 a scaffolding step spent
/// those twelve doing exactly what it was asked — two turns of reconnaissance, seven files read, and
/// then one file written per turn — and was cut off on the twelfth with "Segment did not converge",
/// one turn before it would have said it was done. Every tool call had succeeded and seven files
/// were on disk; the step was still scored Incomplete and its two dependents were skipped.</para>
///
/// <para>The count was the wrong quantity. A hundred-file project needs a hundred turns and no
/// setting should have to say so, while a model rereading one file was equally entitled to twelve.
/// What does not grow with the project is REPETITION, so that is what is counted now: three turns in
/// a row that ask for nothing the step has not already asked for.</para>
/// </summary>
public sealed class StallDetectionTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"do the thing"}""";

    private static Turn Writes(int n)
        => Turn.Calls1("write_file", $$"""{"path":"file{{n}}.txt","content":"contents of file {{n}}"}""", $"w{n}");

    /// <summary>The run that used to die on turn twelve: many turns, each doing something new.</summary>
    [Fact]
    public async Task A_step_that_keeps_producing_is_not_cut_off_at_a_turn_count()
    {
        using var fx = new EngineFixture();

        // Twenty files, one per turn — comfortably past the old cap of twelve.
        var script = new List<Turn> { Turn.Says(QuickPlan) };
        for (var i = 1; i <= 20; i++)
            script.Add(Writes(i));
        script.Add(Turn.Says("Wrote twenty files."));

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(script.ToArray())), "write twenty files");

        Assert.True(events.Has(EventKind.TaskCompleted));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        for (var i = 1; i <= 20; i++)
            Assert.True(fx.Exists($"file{i}.txt"), $"file{i}.txt should have been written");
    }

    /// <summary>And the loop it is actually there to catch still gets caught.</summary>
    [Fact]
    public async Task A_step_that_repeats_the_same_call_is_stopped()
    {
        using var fx = new EngineFixture();

        fx.Write("notes.txt", "some notes");

        var repeat = Turn.Calls1("read_file", """{"path":"notes.txt"}""", "r1");

        var provider = new FakeChatProvider(Turn.Says(QuickPlan), repeat)
        {
            // Every turn from here on is the same call again.
            WhenExhausted = repeat
        };

        var events = await fx.RunAsync(fx.Build(provider), "read the notes");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("repeating tool calls", stopped.Summary);
        Assert.Contains("read_file", stopped.Summary);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    // A stuck step costs a handful of turns, not a hundred — and the turn that trips the limit is
    // not executed, because running the same call a third time is exactly what is being stopped.
    [Fact]
    public async Task A_repeating_step_stops_without_running_the_call_that_trips_the_limit()
    {
        using var fx = new EngineFixture();

        fx.Write("notes.txt", "some notes");
        var repeat = Turn.Calls1("read_file", """{"path":"notes.txt"}""", "r1");

        var provider = new FakeChatProvider(Turn.Says(QuickPlan), repeat) { WhenExhausted = repeat };

        var events = await fx.RunAsync(fx.Build(provider), "read the notes");

        // The first read (new), then two repeats; the third repeat is recognised and never run.
        Assert.Equal(3, events.Count(e => e.Kind == EventKind.ToolInvoked));
    }

    // Progress resets it. An edit/build cycle repeats the build command constantly and is the most
    // ordinary thing an agent does; stalling it would make the guard useless on real work.
    [Fact]
    public async Task Repeating_a_call_between_real_work_does_not_count_as_stalling()
    {
        using var fx = new EngineFixture();

        var build = Turn.Calls1("read_file", """{"path":"build.txt"}""", "b");
        fx.Write("build.txt", "pretend this is a build log");

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            build, Writes(1),
            build, Writes(2),
            build, Writes(3),
            build, Writes(4),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider), "fix it until it builds");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.True(fx.Exists("file4.txt"));
    }

    // A new call that FAILS is still new information, and a step that is working through errors is
    // not a step that is stuck. Unresolved failures are caught at the end of the loop instead.
    [Fact]
    public async Task New_calls_that_fail_are_not_stalling()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("read_file", """{"path":"missing-one.txt"}""", "m1"),
            Turn.Calls1("read_file", """{"path":"missing-two.txt"}""", "m2"),
            Turn.Calls1("read_file", """{"path":"missing-three.txt"}""", "m3"),
            Turn.Calls1("read_file", """{"path":"missing-four.txt"}""", "m4"),
            Turn.Says("None of those exist."));

        var events = await fx.RunAsync(fx.Build(provider), "look for the file");

        // It was allowed to keep looking — four attempts, not stopped after three.
        Assert.Equal(4, events.Count(e => e.Kind == EventKind.ToolInvoked));

        // And it is still not a success: the failures were never resolved, which is a different
        // guard doing its own job. Nothing it looked for was there, so it is blocked on that (Phase 7).
        Assert.Equal(RunOutcomeKind.Blocked, events.Last().Outcome());
    }
}

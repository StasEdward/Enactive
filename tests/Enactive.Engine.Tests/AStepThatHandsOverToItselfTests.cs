namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A step too long for one conversation is CUT, not killed.
///
/// <para><b>Both halves were measured on 2026-09-22.</b> A step that had written a 46 KB report
/// over eleven minutes reached the 250-turn backstop, was marked Incomplete, and took the rest of
/// the plan down with it — nothing about it was wrong, the work was simply longer than one
/// conversation. And on the same task a completed run spent 31.3M prompt tokens across five steps
/// whose per-turn cost climbed 6k → 56k → 113k → 161k → 215k, because every turn re-sends what
/// came before: a step of 2N turns costs about four times a step of N.</para>
///
/// <para>So at <c>TurnsBeforeHandover</c> the step writes down what it has established and what is
/// left, and carries on in a fresh conversation holding its instructions and that note. It is the
/// same step throughout: same journal, same artifacts, same reviewer at the end.</para>
/// </summary>
public sealed class AStepThatHandsOverToItselfTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"a long job","steps":[]}""";

    /// <summary>What the step hands itself, in the one place the fake will be asked for it.</summary>
    private const string Note = "Done: notes 0-59 are written. Left: the rest of them.";

    /// <summary>
    /// A model that keeps working: one tool call per turn, for more turns than a conversation is
    /// allowed to hold, and then an answer. Every call is different, so the stall guard stays out
    /// of it — this is a step doing real work for a long time, not a step going in circles.
    ///
    /// <para><paramref name="noteAfter"/> is where the handover's question lands. The fake serves
    /// streamed turns and non-streamed ones from ONE queue, so the answer to that question has to
    /// sit in the script at the position the engine will ask it — after that many working turns.
    /// Null scripts no answer at all, which is the case where the step declines to be cut.</para>
    /// </summary>
    private static FakeChatProvider Busy(int turns, int? noteAfter = null)
    {
        var script = new List<Turn> { Turn.Says(QuickPlan) };

        for (var i = 0; i < turns; i++)
        {
            if (i == noteAfter)
                script.Add(Turn.Says(Note));

            script.Add(Turn.Calls1("write_file", $$"""{"path":"note{{i}}.md","content":"step {{i}}"}"""));
        }

        script.Add(Turn.Says("Finished the long job."));
        return new FakeChatProvider(script.ToArray());
    }

    /// <summary>
    /// THE ONE THAT MATTERS: a step whose work outlasts one conversation now FINISHES. Before this
    /// it either ran to the backstop and was failed, or paid for a transcript that never stopped
    /// growing.
    /// </summary>
    [Fact]
    public async Task A_step_longer_than_one_conversation_still_finishes()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 75, noteAfter: 60);

        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        Assert.Contains(events, e => e.Kind == EventKind.TaskCompleted);
        Assert.Contains(events, e => e.Kind == EventKind.ContextTrimmed
                                     && e.Summary.Contains("fresh conversation", StringComparison.Ordinal));
        Assert.Equal("step 74", fx.Read("note74.md"));
    }

    /// <summary>
    /// And the point of cutting it: the conversation gets SHORTER. Without this the prompt only
    /// ever grows, which is what made the fifth step of a run cost 215k tokens a turn.
    /// </summary>
    [Fact]
    public async Task The_conversation_is_shorter_after_the_handover_than_before_it()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 75, noteAfter: 60);

        await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        var lengths = agent.Requests.Select(r => r.Messages.Count).ToArray();
        var longest = lengths.Max();

        Assert.True(lengths[^1] < longest / 2,
            $"the last prompt held {lengths[^1]} messages and the longest held {longest} — "
            + "the handover did not shorten anything");
    }

    /// <summary>
    /// What survives is the INSTRUCTIONS and the note. A handover that dropped the system prompt
    /// would drop the honesty rules with it, and a step that forgot what it was asked to do would
    /// do something else confidently.
    /// </summary>
    [Fact]
    public async Task The_new_conversation_keeps_the_instructions_and_carries_the_note()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 75, noteAfter: 60);

        await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        var after = agent.Requests[^1].Messages;

        Assert.Equal(ChatRole.System, after[0].Role);
        Assert.Contains("a long job", after[1].Content ?? "", StringComparison.Ordinal);
        Assert.Contains(after, m => (m.Content ?? "").Contains("started again from your own notes",
                                                              StringComparison.Ordinal));
    }

    /// <summary>
    /// A step that cannot say what it has done is NOT cut. Starting it over from its instructions
    /// alone, having forgotten everything it learned, is worse than a long conversation — so when
    /// the note comes back empty the handover is simply not taken and the backstop stays where it
    /// was.
    /// </summary>
    [Fact]
    public async Task A_step_that_writes_no_note_is_not_cut()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 75);   // nothing scripted where the handover asks

        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ContextTrimmed);
        Assert.Contains(events, e => e.Kind == EventKind.TaskCompleted);
    }

    /// <summary>
    /// A step short enough for one conversation is not touched. Most steps are this, and a handover
    /// they did not need would cost a model call and a broken prompt cache for nothing.
    /// </summary>
    [Fact]
    public async Task A_short_step_is_left_alone()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 5);

        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a short job");

        Assert.Contains(events, e => e.Kind == EventKind.TaskCompleted);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ContextTrimmed);
    }
}

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
    private static FakeChatProvider Busy(int turns, params int[] noteAfter)
    {
        var script = new List<Turn> { Turn.Says(QuickPlan) };

        for (var i = 0; i < turns; i++)
        {
            // A note at EACH handover point, because turnsHere resets after a cut: the second cut
            // arrives 60 working turns after the first, and a script with only the first note has
            // the second handover decline rather than happen.
            if (noteAfter.Contains(i))
                script.Add(Turn.Says(Note));

            script.Add(Turn.Calls1("write_file", $$"""{"path":"note{{i}}.md","content":"step {{i}}"}"""));
        }

        script.Add(Turn.Says("Finished the long job."));
        return new FakeChatProvider(script.ToArray());
    }

    /// <summary>
    /// A step is not stopped for finding its feet after a handover.
    ///
    /// <para><b>Measured 2026-09-23 23:36, run 941cc9.</b> Sixty turns of real verification - 34
    /// reads and 32 searches across the source - then a handover carrying a note that named three
    /// wiki pages and eight checked claims. Three turns and seven seconds later: <i>"stopped after
    /// 3 turns that only repeated earlier tool calls: read_file Docs/DRIFT_ollama.md; list_dir
    /// Docs"</i>, and the whole plan skipped behind it.</para>
    ///
    /// <para>Those three were the model orienting itself in a conversation it had just been
    /// handed: does the report I am to append to exist, what is in that folder, let me look
    /// again. New to the conversation and old to a ledger that had outlived it.</para>
    ///
    /// <para><b>Why the step has to write nothing.</b> A successful write advances a GENERATION
    /// (§9r) and every call after it is new again, which hides this entirely. The reported step
    /// was an AUDIT - it read and searched for sixty turns and wrote not one file - so the ledger
    /// it carried across the handover was complete and unforgiving.</para>
    /// </summary>
    [Fact]
    public async Task A_truncated_handover_can_be_retried_without_losing_work()
    {
        using var fx = new EngineFixture();
        var script = new List<Turn> { Turn.Says(QuickPlan) };
        for (var i = 0; i < 80; i++)
        {
            if (i == 60) script.Add(new Turn(Text: "Incomplete notes", FinishReason: "length"));
            if (i == 70) script.Add(Turn.Says(Note));
            script.Add(Turn.Calls1("write_file", $$"""{"path":"note{{i}}.md","content":"step {{i}}"}"""));
        }
        script.Add(Turn.Says("Finished."));
        var agent = new FakeChatProvider(script.ToArray());
        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        Assert.Equal(2, agent.Requests.Count(r => r.Purpose == GenerationPurpose.Handover));
        Assert.Contains(events, e => e.Kind == EventKind.ContextTrimmed);
        Assert.Contains(events, e => e.Kind == EventKind.TaskCompleted);
        for (var i = 0; i < 80; i++) Assert.Equal($"step {i}", fx.Read($"note{i}.md"));
    }

    [Fact]
    public async Task Handover_uses_its_own_output_budget()
    {
        using var fx = new EngineFixture();
        fx.GenerationBudgetsOverride = new(Handover: 777);
        var agent = Busy(65, 60);
        await fx.RunAsync(fx.Build(agent), "do the long job");
        var handover = Assert.Single(agent.Requests, r => r.Purpose == GenerationPurpose.Handover);
        Assert.Equal(777, handover.OutputTokenLimit);
        Assert.NotEmpty(handover.Tools!);
    }

    [Fact]
    public async Task Orienting_itself_after_a_handover_is_not_a_stall()
    {
        using var fx = new EngineFixture();
        fx.Write("source.cs", "class A { }");

        var script = new List<Turn> { Turn.Says(QuickPlan) };

        // The orientation, done once early - exactly as the reported run did before settling in.
        script.Add(Turn.Calls1("read_file", """{"path":"report.md"}"""));
        script.Add(Turn.Calls1("list_dir", """{"path":"."}"""));

        // Fifty-eight turns of reading and searching. No writes: an audit changes nothing, and a
        // write would advance the generation and make every later call new again.
        for (var i = 0; i < 58; i++)
            script.Add(Turn.Calls1("search_files",
                                   $$"""{"pattern":"claim{{i}}","glob":"*.cs"}"""));

        script.Add(Turn.Says(Note));   // the handover asks, and this answers

        // And then the same three moves, in the fresh conversation that cannot see them.
        script.Add(Turn.Calls1("read_file", """{"path":"report.md"}"""));
        script.Add(Turn.Calls1("list_dir", """{"path":"."}"""));
        script.Add(Turn.Calls1("read_file", """{"path":"report.md"}"""));
        script.Add(Turn.Says("Found my bearings and finished."));

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(script.ToArray()), EngineFixture.Role("developer")),
            "audit the claims");

        Assert.DoesNotContain("only repeated earlier tool calls", events.Text(), StringComparison.Ordinal);
        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// The LAST cut says it is the last — the cheap half of layer 2, and the whole of what that
    /// layer was going to buy.
    ///
    /// <para><b>Why the machinery was not built.</b> Layer 2 was to have a step declare what it
    /// repeats over, with the engine resolving and freezing the set, sizing batches from a probe and
    /// breaking the circuit after two failures. Measured 2026-09-23, one task and two workers: with
    /// the workspace census in front of it the planner batched twelve pages into four steps
    /// unprompted, and those steps ran 31, 21, 14 and 12 turns — a fifth of the ceiling. The same
    /// plan with a weaker worker ran 146, 148 and 88. So the census decides the SIZE of a batch and
    /// the model decides whether it fits, and what was worth building was not a set resolver: it
    /// was being told, at the moment it happens, that this run is on the second of those two
    /// paths.</para>
    ///
    /// <para>Before this, the third handover read exactly like the first two, and the run that cost
    /// 30.6M tokens on 2026-09-22 gave no sign until the backstop had already taken it.</para>
    /// </summary>
    [Fact]
    public async Task The_last_handover_says_it_is_the_last()
    {
        using var fx = new EngineFixture();

        // Three cuts, so the third is the one with nothing behind it.
        var agent = Busy(200, 60, 120, 180);

        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        var cuts = events.Where(e => e.Kind == EventKind.ContextTrimmed).ToArray();

        Assert.NotEmpty(cuts);

        // The earlier ones stay plain: a warning on every cut is a warning nobody reads.
        Assert.All(cuts.Take(cuts.Length - 1),
                   e => Assert.DoesNotContain("last handover", e.Summary, StringComparison.Ordinal));

        var last = cuts[^1];
        Assert.Contains("last handover", last.Summary, StringComparison.Ordinal);
        Assert.Contains("250-turn backstop", last.Summary, StringComparison.Ordinal);
        Assert.Contains("how many at a time", last.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the quietest branch of all: a step that could not summarise its own work is NOT cut, so
    /// the safety net is gone and nothing used to say so. It carries on in one long conversation,
    /// which is the right call — starting the step again from nothing is worse — but it is not a
    /// thing to be silent about.
    /// </summary>
    [Fact]
    public async Task A_step_that_could_not_be_cut_says_the_net_is_gone()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 75);   // no note scripted: the handover is declined

        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.Contains("could not summarise", StringComparison.Ordinal)
                                     && e.Summary.Contains("was not cut", StringComparison.Ordinal));
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
        var agent = Busy(75, 60);

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
        var agent = Busy(75, 60);

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
        var agent = Busy(75, 60);

        await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        var after = agent.Requests[^1].Messages;

        Assert.Equal(ChatRole.System, after[0].Role);
        Assert.Contains("a long job", after[1].Content ?? "", StringComparison.Ordinal);
        Assert.Contains(after, m => (m.Content ?? "").Contains("started again from your own notes",
                                                              StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed summary keeps the transcript for one retry. After two failures, measured engine
    /// evidence replaces the missing note; the original instructions and backstop remain.
    /// </summary>
    [Fact]
    public async Task A_step_that_twice_writes_no_note_carries_engine_evidence()
    {
        using var fx = new EngineFixture();
        var agent = Busy(turns: 75);   // nothing scripted where the handover asks

        var events = await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "a long job");

        Assert.Contains(events, e => e.Kind == EventKind.ContextTrimmed
                                    && e.Summary.Contains("Carrying engine evidence", StringComparison.Ordinal));
        Assert.Equal(2, agent.Requests.Count(r => r.Purpose == GenerationPurpose.Handover));
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

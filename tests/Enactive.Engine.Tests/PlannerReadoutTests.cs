namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// The defect log: the planner failed SILENTLY.
///
/// <para>Three different things arrived as the same value — a QuickAction under a title cut from the
/// request — and nothing anywhere could tell them apart: the model deciding the request is one
/// action, the model saying "task" and listing no steps, and nobody being able to read the answer at
/// all. So a genuine multi-step request whose plan came back as prose collapsed into one unplanned
/// action, under a heading that looked deliberate, and the run said nothing.</para>
///
/// <para><c>Reviewer.Parse</c> had already been fixed for exactly this shape of bug — "defaulting
/// anything-but-fail to pass was the same bug wearing a different hat" — and the planner kept it.
/// It now does what the reviewer does: re-ask once, and then be honest about what happened.</para>
/// </summary>
public sealed class PlannerReadoutTests
{
    private static WorkContext Context()
        => new(Guid.NewGuid(), "workspace", null, null, null, Array.Empty<string>(), Array.Empty<string>());

    private static Task<PlanResult> PlanWith(params Turn[] script)
        => new Planner().PlanAsync(
            "rewrite the docs and then publish them", Context(),
            new FakeChatProvider(script), "planner-model", CancellationToken.None);

    private const string RealPlan =
        """{"disposition":"task","title":"Docs","steps":[{"title":"rewrite","dependsOn":[]},{"title":"publish","dependsOn":[0]}]}""";

    // ── the recovery ────────────────────────────────────────────────────────

    /// <summary>
    /// One re-ask, and the plan is recovered. This is the case the whole change is for: without it a
    /// two-step request became one unplanned action because the model put a sentence in front of its
    /// JSON.
    /// </summary>
    [Fact]
    public async Task An_answer_that_could_not_be_read_is_asked_about_once_more()
    {
        var plan = await PlanWith(Turn.Says("Sure! Let me think about how to approach this."),
                                  Turn.Says(RealPlan));

        Assert.Equal(IntentDisposition.Task, plan.Disposition);
        Assert.Equal(PlanReadout.Understood, plan.Readout);
        Assert.Equal(2, plan.Plan!.Steps.Count);
    }

    /// <summary>The re-ask costs a call, and the run pays for it. Tokens spent are tokens counted.</summary>
    [Fact]
    public async Task The_second_attempt_is_paid_for()
    {
        var plan = await PlanWith(Turn.Says("thinking...").Reporting(prompt: 100, completion: 20),
                                  Turn.Says(RealPlan).Reporting(prompt: 150, completion: 30));

        Assert.Equal(250, plan.Usage.Prompt);
        Assert.Equal(50, plan.Usage.Completion);
    }

    /// <summary>A readable answer costs one call. The recovery must not become the normal path.</summary>
    [Fact]
    public async Task A_readable_answer_is_not_asked_about_twice()
    {
        var provider = new FakeChatProvider(Turn.Says(RealPlan), Turn.Says(RealPlan));

        await new Planner().PlanAsync("do the thing", Context(), provider, "m", CancellationToken.None);

        Assert.Single(provider.Requests);
    }

    // ── the honest fallback ─────────────────────────────────────────────────

    /// <summary>
    /// The defect itself. Twice with nothing readable still runs the request as a single action —
    /// refusing it would be worse — but that is a FALLBACK, and it is now distinguishable from a
    /// decision by anything that looks.
    /// </summary>
    [Fact]
    public async Task Two_unreadable_answers_are_a_fallback_not_a_decision()
    {
        var plan = await PlanWith(Turn.Says("I'll help you with that!"),
                                  Turn.Says("Of course — here is what I suggest: first, ..."));

        Assert.Equal(IntentDisposition.QuickAction, plan.Disposition);
        Assert.Equal(PlanReadout.Unreadable, plan.Readout);
        Assert.Null(plan.Plan);
    }

    /// <summary>And a real quick action is NOT marked as one, or the distinction buys nothing.</summary>
    [Fact]
    public async Task A_decision_to_do_one_thing_reads_as_a_decision()
    {
        var plan = await PlanWith(Turn.Says("""{"disposition":"quick_action","title":"Rename it","steps":[]}"""));

        Assert.Equal(IntentDisposition.QuickAction, plan.Disposition);
        Assert.Equal(PlanReadout.Understood, plan.Readout);
    }

    /// <summary>
    /// "task" with an empty steps array still becomes a quick action - that rule is deliberate - but
    /// it is recorded as what it was. A planner that keeps doing this is a planner that is not
    /// working, and it used to be indistinguishable from one that answered correctly.
    /// </summary>
    [Fact]
    public async Task A_task_with_no_steps_says_which_it_was()
    {
        var plan = await PlanWith(Turn.Says("""{"disposition":"task","title":"Something","steps":[]}"""));

        Assert.Equal(IntentDisposition.QuickAction, plan.Disposition);
        Assert.Equal(PlanReadout.TaskWithNoSteps, plan.Readout);
    }

    /// <summary>
    /// Any JSON object at all used to be accepted, and one without a "disposition" became a
    /// QuickAction — so a fragment of something else entirely came back indistinguishable from a
    /// decision. The answer has to be recognisably the shape that was asked for.
    /// </summary>
    [Theory]
    [InlineData("""{"answer":"I think you should rename the file"}""")]
    [InlineData("""{"disposition":"maybe","title":"x"}""")]
    [InlineData("""{"title":"just a title"}""")]
    public async Task An_object_that_is_not_a_plan_is_not_read_as_one(string answer)
    {
        var plan = await PlanWith(Turn.Says(answer), Turn.Says(answer));

        Assert.Equal(PlanReadout.Unreadable, plan.Readout);
    }

    /// <summary>A bare list of steps IS an unambiguous statement of a plan, whatever it omits.</summary>
    [Fact]
    public async Task Steps_on_their_own_are_a_plan()
    {
        var plan = await PlanWith(Turn.Says(
            """{"title":"Docs","steps":[{"title":"rewrite","dependsOn":[]},{"title":"publish","dependsOn":[0]}]}"""));

        Assert.Equal(IntentDisposition.Task, plan.Disposition);
        Assert.Equal(PlanReadout.Understood, plan.Readout);
    }

    // ── the run says so ─────────────────────────────────────────────────────

    /// <summary>
    /// End to end: the fallback is visible in the run, and the run still does the work. An event
    /// rather than a failure, because the request is not wrong — only unplanned.
    /// </summary>
    [Fact]
    public async Task An_unreadable_plan_shows_up_in_the_run()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(
                Turn.Says("Happy to help!"),
                Turn.Says("Let me start by looking at the files."),
                Turn.Says("Done."))),
            "rewrite the docs and publish them");

        var problems = events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary).ToArray();
        Assert.Contains(problems, s => s.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains(problems, s => s.Contains("not a decision", StringComparison.Ordinal));

        // The work still happened.
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>A run whose plan WAS read says nothing - the notice has to mean something.</summary>
    [Fact]
    public async Task A_run_that_was_planned_says_nothing_about_fallbacks()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(
                Turn.Says("""{"disposition":"quick_action","title":"Look around","steps":[]}"""),
                Turn.Says("Done."))),
            "look around");

        Assert.DoesNotContain(events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary),
                              s => s.Contains("could not be read", StringComparison.Ordinal));
    }
}

/// <summary>
/// The two ways of reading a model's text, shared by the planner and the reviewer since they are the
/// only two places that still have to. Both used to be duplicated, and both were confidently wrong
/// in the same two ways.
/// </summary>
public sealed class ModelTextTests
{
    // ── reasoning that never finished ───────────────────────────────────────

    /// <summary>
    /// The one that matters. A reasoning model cut short by num_ctx leaves &lt;think&gt; open, and the
    /// old code stripped nothing at all — so the brace scan then read JSON out of the model's own
    /// deliberation. An abandoned draft, complete with whatever it was considering and rejecting,
    /// came back as the plan.
    /// </summary>
    [Fact]
    public void Reasoning_that_never_finished_is_not_an_answer()
    {
        const string cut = """
            <think>The user wants two things. Maybe {"disposition":"task","steps":[{"title":"guess"}]} would
            """;

        Assert.Equal("", ModelText.StripThink(cut).Trim());
        Assert.Null(ModelText.ExtractJsonObject(ModelText.StripThink(cut)));
    }

    [Fact]
    public void Reasoning_that_did_finish_is_removed_and_the_answer_kept()
    {
        const string answer = """<think>Two stages, I think.</think>{"disposition":"task"}""";

        Assert.Equal("""{"disposition":"task"}""", ModelText.StripThink(answer));
    }

    [Fact]
    public void Several_blocks_of_reasoning_are_all_removed()
        => Assert.Equal("ab", ModelText.StripThink("<think>one</think>a<think>two</think>b"));

    [Fact]
    public void Text_with_no_reasoning_is_untouched()
        => Assert.Equal("""{"verdict":"pass"}""", ModelText.StripThink("""{"verdict":"pass"}"""));

    // ── the object in the prose ─────────────────────────────────────────────

    /// <summary>
    /// "First { to last }" spans everything between them, so one stray brace in a sentence before the
    /// object produced text that is not JSON at all - and the whole answer was thrown away.
    /// </summary>
    [Fact]
    public void A_brace_in_the_prose_does_not_swallow_the_object()
    {
        const string answer = """Here is the plan (I used the shape {like this}): {"verdict":"pass"}""";

        Assert.Equal("""{"verdict":"pass"}""", ModelText.ExtractJsonObject(answer));
    }

    /// <summary>Two objects: the first one is the answer, not a span reaching across both.</summary>
    [Fact]
    public void The_first_object_wins_when_there_are_two()
        => Assert.Equal("""{"a":1}""", ModelText.ExtractJsonObject("""{"a":1} and then {"b":2}"""));

    /// <summary>A brace inside a string is text, not structure.</summary>
    [Fact]
    public void A_brace_inside_a_string_is_not_structure()
    {
        const string answer = """{"notes":"the file contains a { and no closing one"}""";

        Assert.Equal(answer, ModelText.ExtractJsonObject(answer));
    }

    [Fact]
    public void An_escaped_quote_does_not_end_the_string()
    {
        const string answer = """{"notes":"he said \"no\" twice"}""";

        Assert.Equal(answer, ModelText.ExtractJsonObject(answer));
    }

    [Fact]
    public void Nesting_is_followed_to_the_matching_brace()
    {
        const string answer = """{"plan":{"steps":[{"title":"a"}]}}""";

        Assert.Equal(answer, ModelText.ExtractJsonObject(answer));
    }

    /// <summary>An object that was cut off mid-write is not an object.</summary>
    [Fact]
    public void An_unclosed_object_is_not_an_object()
        => Assert.Null(ModelText.ExtractJsonObject("""{"disposition":"task","steps":[{"title":"a"""));

    [Fact]
    public void Prose_with_no_object_at_all_is_nothing()
        => Assert.Null(ModelText.ExtractJsonObject("I would suggest renaming the file first."));
}

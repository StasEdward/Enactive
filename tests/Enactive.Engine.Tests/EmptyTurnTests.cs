namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 17:56 — and found by the AI Analyze button in its first hour,
/// which is the best argument for that feature there is going to be.
///
/// <para>A Code Review run failed twice with "The agent reported reviewing changes and writing to
/// review.md, but no tools were run and no files were changed." Every word of that is true and none
/// of it is the cause. The log line underneath is:</para>
///
/// <para><c>response ← ollama/gemma4-12b:latest (0 chars, 0 tool call(s), finish=stop, 15
/// out-tokens)</c></para>
///
/// <para><b>Fifteen output tokens and nothing to show for them.</b> The model generated a turn and
/// none of it reached the engine — the signature of a reasoning model that spends the turn thinking,
/// which Ollama returns in a <c>thinking</c> field this provider never read.</para>
///
/// <para>And the engine treated that silence as an ANSWER. An empty turn with no tool calls fell
/// through to "genuine final answer — no tool calls" and marked the step <b>Succeeded</b>. The
/// reviewer then failed it for the only thing it could see, the retry produced the same silence, and
/// the run died with a message about the reviewer while the cause appeared nowhere at all.</para>
///
/// <para>An absence is not an answer. That is the rule this codebase applies to an unreachable
/// reviewer, an unevaluable criterion, an approval nobody can give and a plan nobody could read —
/// and this was the last place still breaking it.</para>
/// </summary>
public sealed class EmptyTurnTests
{
    private const string Plan = """{"disposition":"quick_action","title":"Do the thing","steps":[]}""";

    // ── silence is not a final answer ───────────────────────────────────────

    /// <summary>
    /// The decisive one. Before this the step came back Succeeded and the run's failure was
    /// attributed to the reviewer.
    /// </summary>
    [Fact]
    public async Task A_model_that_returned_nothing_has_not_finished_the_step()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(Turn.Says(Plan), Turn.Silent()), EngineFixture.Role("developer")),
            "review what changed");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// And it says what to do about it. "No tools were run" describes the symptom; a person needs to
    /// be pointed at the setting that causes it.
    /// </summary>
    [Fact]
    public async Task The_run_says_the_output_never_arrived_and_what_to_try()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(Turn.Says(Plan), Turn.Silent()), EngineFixture.Role("developer")),
            "review what changed");

        var problems = string.Join("\n", events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary));

        Assert.Contains("returned nothing", problems);
        Assert.Contains("never reached the engine", problems);
        Assert.Contains("Capture raw wire", problems);
    }

    /// <summary>
    /// Reported the same evening the fix shipped: the message led with "a reasoning model that
    /// spends the turn thinking does this" over a turn the provider had reported NO reasoning for.
    /// Leading with a cause the evidence rules out is the habit the rest of this engine exists to
    /// break, and it does not get an exemption for being in an error message.
    /// </summary>
    [Fact]
    public async Task Reasoning_is_not_blamed_when_none_was_reported()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(Turn.Says(Plan), Turn.Silent()), EngineFixture.Role("developer")),
            "review what changed");

        var problems = string.Join("\n", events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary));

        Assert.Contains("no reasoning either", problems);
        Assert.DoesNotContain("spent the whole turn reasoning", problems);
    }

    /// <summary>
    /// And num_ctx is only mentioned when the prompt is actually near the window. The reported case
    /// used 1786 of 131072 tokens; telling that person to raise num_ctx sends them to tune a setting
    /// with nothing to do with it, which is how a diagnosis decays into a list of everything it
    /// might be.
    /// </summary>
    [Fact]
    public async Task A_prompt_nowhere_near_the_window_is_not_blamed_on_the_window()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(Plan), Turn.Silent()) { Window = 131_072 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "review");

        Assert.DoesNotContain(events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary),
                              s => s.Contains("num_ctx", StringComparison.Ordinal));
    }

    /// <summary>
    /// When the provider DID hand over the reasoning, the diagnosis stops being a guess: the turn was
    /// spent thinking, and it can say how much.
    /// </summary>
    [Fact]
    public async Task A_turn_spent_entirely_on_reasoning_is_named_as_that()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(Plan),
                    Turn.Thinks("Let me consider whether to call git diff or read the files first…")),
                EngineFixture.Role("developer")),
            "review what changed");

        var problems = string.Join("\n", events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary));

        Assert.Contains("spent the whole turn reasoning", problems);
        Assert.Contains("Turn Thinking off", problems);
        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// The reasoning never becomes the answer. It is a draft — including what the model was
    /// considering and rejecting — and a transcript that carries it as content invites every later
    /// turn to treat a rejected idea as a decision.
    /// </summary>
    [Fact]
    public async Task Reasoning_does_not_become_the_answer()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Thinks("I could delete everything, but that would be wrong."),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "do it");

        foreach (var request in provider.Requests)
            Assert.DoesNotContain(request.Messages,
                m => (m.Content ?? "").Contains("I could delete everything", StringComparison.Ordinal));
    }

    // ── and what must NOT change ────────────────────────────────────────────

    /// <summary>
    /// A step that did real work and ends without a closing sentence is not a failure. The work
    /// happened; only the summary is missing, and failing that would break every run whose model
    /// stops talking once it is finished.
    /// </summary>
    [Fact]
    public async Task A_step_that_did_the_work_and_said_nothing_still_counts()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(Plan),
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"the work"}"""),
                    Turn.Silent()),
                EngineFixture.Role("developer")),
            "write notes.md");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Equal("the work", fx.Read("notes.md"));
    }

    /// <summary>An ordinary answer is still an ordinary answer.</summary>
    [Fact]
    public async Task A_model_that_answered_is_not_accused_of_silence()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(Turn.Says(Plan), Turn.Says("Nothing needed doing.")),
                     EngineFixture.Role("developer")),
            "look around");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.DoesNotContain(events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary),
                              s => s.Contains("returned nothing", StringComparison.Ordinal));
    }
}

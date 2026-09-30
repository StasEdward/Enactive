namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Reported 2026-09-11: a run was started with Ollama switched off, and nothing said so.
///
/// <para>The engine had diagnosed it perfectly — <i>"Nothing is listening at
/// http://localhost:11434/v1, so ollama/gemma4:31b-cloud could not be asked. If that is a local
/// model server, it is not running."</i> — and written it into the step's payload. What the person
/// saw was a card that said <c>Failed</c> and a run that said
/// <c>1 step(s) failed; 1 step(s) skipped</c>.</para>
///
/// <para>Both were true. Neither was the answer. This is the seam where the cause was being
/// dropped: the run's explanation was assembled from the step OUTCOMES alone, which are an enum and
/// cannot carry a sentence, and the card rendered a literal instead of the reason it had in hand.
/// </para>
/// </summary>
public sealed class RunOutcomeWordsTests
{
    private const string Ollama =
        "Nothing is listening at http://localhost:11434/v1, so ollama/gemma4:31b-cloud could not be "
        + "asked. If that is a local model server, it is not running.";

    // ── the run's own line ──────────────────────────────────────────────────

    /// <summary>
    /// The case that was reported, end to end: one step failed with a reason, one was skipped
    /// behind it, and the run must say WHY rather than how many.
    /// </summary>
    [Fact]
    public void A_run_stopped_by_a_dead_model_server_says_so()
    {
        var said = RunOutcomeWords.Explain(
            new[] { StepOutcomeKind.Failed, StepOutcomeKind.Skipped },
            new[] { Ollama },
            cycle: false);

        Assert.StartsWith(Ollama, said!, StringComparison.Ordinal);

        // The counts survive — they are the shape of the run and somebody reading a list of runs
        // uses them. They just stop being the whole answer.
        Assert.Contains("1 step(s) failed", said, StringComparison.Ordinal);
        Assert.Contains("1 step(s) skipped", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cause LEADS. An Inbox row that runs out of width should lose "1 step(s) skipped", which
    /// the reader can see for themselves, and never the sentence that says the model server is off.
    /// </summary>
    [Fact]
    public void The_cause_comes_before_the_counting()
    {
        var said = RunOutcomeWords.Explain(
            new[] { StepOutcomeKind.Failed },
            new[] { Ollama },
            cycle: false)!;

        var cause = said.IndexOf(Ollama, StringComparison.Ordinal);
        var tally = said.IndexOf("1 step(s) failed", StringComparison.Ordinal);

        // Both have to BE there before their order means anything. Written the obvious way -
        // `cause < tally` alone - this passed with the cause missing entirely, because IndexOf
        // returns -1 and -1 is less than everything. Caught by reverting the fix and finding this
        // test still green.
        Assert.True(cause >= 0, $"The cause is not in the line at all: {said}");
        Assert.True(tally > cause, $"The counts came first: {said}");
    }

    /// <summary>
    /// A step that failed with nothing to add leaves the old sentence exactly as it was. The point
    /// is to stop DISCARDING a reason, not to invent one.
    /// </summary>
    [Fact]
    public void With_no_reason_recorded_it_says_what_it_always_said()
    {
        Assert.Equal(
            "1 step(s) failed; 1 step(s) skipped",
            RunOutcomeWords.Explain(
                new[] { StepOutcomeKind.Failed, StepOutcomeKind.Skipped },
                new string?[] { null, "  " },
                cycle: false));
    }

    /// <summary>
    /// A ceiling still comes first of all, because it EXPLAINS the skipped steps after it — without
    /// it a run that ran out of budget reports "4 step(s) skipped" and nothing about why.
    /// </summary>
    [Fact]
    public void A_limit_that_was_hit_still_leads_the_counting()
    {
        var said = RunOutcomeWords.Explain(
            new[] { StepOutcomeKind.Skipped, StepOutcomeKind.Skipped },
            Array.Empty<string?>(),
            cycle: false,
            limit: "the run reached its token budget")!;

        Assert.StartsWith("the run reached its token budget", said, StringComparison.Ordinal);
    }

    /// <summary>A run where everything succeeded explains nothing, and says nothing.</summary>
    [Fact]
    public void A_run_that_simply_worked_has_nothing_to_explain()
    {
        Assert.Null(RunOutcomeWords.Explain(
            new[] { StepOutcomeKind.Succeeded, StepOutcomeKind.Succeeded },
            Array.Empty<string?>(),
            cycle: false));
    }

    /// <summary>
    /// One line, capped. A reason is occasionally a provider's whole error document, and a
    /// paragraph in an Inbox row pushes out everything beside it. Cut at a word, marked as cut.
    /// </summary>
    [Fact]
    public void An_enormous_reason_is_cut_rather_than_allowed_to_swamp_the_line()
    {
        var huge = string.Join(' ', Enumerable.Repeat("verbose", 200));

        var said = RunOutcomeWords.Explain(
            new[] { StepOutcomeKind.Failed }, new[] { huge }, cycle: false)!;

        Assert.Contains("…", said, StringComparison.Ordinal);
        Assert.True(said.Length < RunOutcomeWords.MaxReason + 60, $"{said.Length} characters");
    }

    /// <summary>
    /// A reason written across several lines becomes one. The run's explanation is a single line in
    /// every place it is shown, and embedded newlines break all of them.
    /// </summary>
    [Fact]
    public void A_reason_with_line_breaks_is_flattened()
    {
        var said = RunOutcomeWords.Explain(
            new[] { StepOutcomeKind.Failed },
            new[] { "first line\r\n\r\nsecond line" },
            cycle: false)!;

        Assert.DoesNotContain('\n', said);
        Assert.StartsWith("first line second line", said, StringComparison.Ordinal);
    }

    // ── and the plumbing, which the wording tests cannot see ────────────────

    /// <summary>
    /// The words above are checked in isolation; this checks that a real run REACHES them.
    ///
    /// <para>Without it the sentences would be tested and the gathering would not, which is the
    /// half that was actually broken: the reasons existed on every step and the run's explanation
    /// was built from the outcome enums alone. A step reason has to travel from the tool loop,
    /// through the outcome dictionary's lock, into the terminal event — and none of that is
    /// visible from a function that takes two lists.</para>
    /// </summary>
    [Fact]
    public async Task A_real_run_carries_its_first_failure_into_the_terminal_event()
    {
        using var fx = new EngineFixture();

        // Two steps, the second depending on the first, so the run ends with one failure and one
        // skip - the exact shape of the reported run.
        var planner = new FakeChatProvider(Turn.Says(
            """
            {"disposition":"task","title":"Audit and fix README.md","steps":[
              {"title":"Read README and survey codebase","dependsOn":[],"complexity":"normal"},
              {"title":"Correct drifted README statements","dependsOn":[0],"complexity":"normal"}]}
            """));

        const string Down = "the model server is not running";

        var events = await fx.RunAsync(
            fx.Build(
                new MapProviderFactory(new ThrowingChatProvider(Down), (Routers.PlannerProviderId, planner)),
                EngineFixture.Role("developer"),
                router: Routers.WithPlannerOn()),
            "check the readme");

        var terminal = Assert.Single(events.OfKind(EventKind.TaskFailed));
        var reason = terminal.OutcomeReason();

        Assert.NotNull(reason);
        Assert.Contains(Down, reason!, StringComparison.Ordinal);
        // Each step that is not done, by name, and what became of it - the second skipped for the first.
        Assert.Contains("[2] Correct drifted README statements - skipped", reason, StringComparison.Ordinal);
    }

    // ── the card under the title ────────────────────────────────────────────

    /// <summary>
    /// The step card's line. It said "Failed" while holding the reason, which is the half of this
    /// defect a person actually looked at.
    /// </summary>
    [Fact]
    public void A_failed_step_says_what_stopped_it()
    {
        Assert.Equal(
            "Failed — " + Ollama,
            RunOutcomeWords.StepActivity(StepOutcomeKind.Failed, Ollama));
    }

    /// <summary>And with nothing recorded, the word alone — not "Failed — " trailing into space.</summary>
    [Fact]
    public void A_failed_step_with_nothing_to_add_says_only_that()
    {
        Assert.Equal("Failed", RunOutcomeWords.StepActivity(StepOutcomeKind.Failed, null));
        Assert.Equal("Failed", RunOutcomeWords.StepActivity(StepOutcomeKind.Failed, "   "));

        // An event from an older build carries no outcome at all and must still read as a failure
        // rather than as a blank.
        Assert.Equal("Failed", RunOutcomeWords.StepActivity(null, null));
    }

    /// <summary>
    /// A skipped step is never given a cause, even when one is passed. Nothing went wrong in THAT
    /// step, and putting another step's failure under it sends somebody to the wrong card.
    /// </summary>
    [Fact]
    public void A_skipped_step_is_not_told_about_another_steps_failure()
    {
        Assert.Equal(
            "Skipped — a dependency failed",
            RunOutcomeWords.StepActivity(StepOutcomeKind.Skipped, Ollama));
    }

    /// <summary>
    /// The reviewer's rejection reads as a rejection and not as a crash. They are different things
    /// and the card is where the difference is seen.
    /// </summary>
    [Fact]
    public void A_rejected_step_says_it_was_rejected()
    {
        var said = RunOutcomeWords.StepActivity(
            StepOutcomeKind.ReviewRejected, "review not passed: the report cites no command");

        Assert.StartsWith("Rejected by the reviewer — ", said, StringComparison.Ordinal);
    }
}

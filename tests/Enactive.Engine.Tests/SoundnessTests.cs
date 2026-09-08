namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Whether a step's reported success FOLLOWS from what the step did.
///
/// <para><c>PLAN_v2.md</c> §11 carried it as: <i>"The execution reviewer checks truth, not
/// soundness. A verification step whose every stated fact is in the evidence can still conclude
/// something the facts do not support (a test 'targeted' that was already failing, and stayed
/// failing), and it passes."</i></para>
///
/// <para><b>Why this is not a fifth prompt clause.</b> Four were added to the execution reviewer
/// during 2026-09-07 and a fifth declined, because no wording fixes a model that misreads what it is
/// shown. So the pass is asked for a POINTER rather than an opinion — the numbers of the calls that
/// show the objective was met — and <see cref="ProofAudit"/> resolves those numbers against the
/// journal. Three of the four ways a claim fails are decided by the engine looking things up. The
/// fourth is the model's own "no", and that is stated rather than dressed up as mechanical.</para>
/// </summary>
public sealed class SoundnessTests
{
    private const string OneStepPlan = """
        {"disposition":"task","title":"fix the failing test",
         "steps":[{"title":"make the failing test pass","dependsOn":[]}]}
        """;

    /// <summary>A step that runs a command and reports success.</summary>
    private static Turn RunsTests()
        => Turn.Calls1("run_command", """{"command":"echo pretend-this-is-dotnet-test"}""", "t1");

    private static string[] Failures(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.ReviewFailed).Select(e => e.Summary).ToArray();

    private static ExecutedAction Action(ActionOutcome outcome, string tool = "run_command")
        => new(DateTimeOffset.UtcNow, 1, tool, "{}", outcome, "output");

    // ── the audit, on its own ───────────────────────────────────────────────
    //
    // Pure, and tested without a model on purpose: this is the half of the pass that does not
    // depend on anything being persuasive.

    /// <summary>
    /// The documented case. The report is true, the reviewer passed it, and the only call named as
    /// proof is the one that failed. The conclusion does not follow, and no judgement was needed to
    /// see that — the engine looked the call up.
    /// </summary>
    [Fact]
    public void A_proof_that_rests_on_a_failed_call_does_not_hold()
    {
        var calls = new[] { Action(ActionOutcome.Succeeded, "read_file"), Action(ActionOutcome.Failed) };

        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.Shown, new[] { 2 }, "the test run shows it"), calls);

        Assert.False(verdict.Sound);
        Assert.Contains("failed", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One good citation among bad ones is enough. The rule is that the conclusion must rest on
    /// SOMETHING that worked, not that nothing beside it went wrong — a step is allowed to have
    /// tried things.
    /// </summary>
    [Fact]
    public void A_proof_with_one_call_that_worked_holds()
    {
        var calls = new[] { Action(ActionOutcome.Failed), Action(ActionOutcome.Succeeded) };

        Assert.True(ProofAudit.Check(
            new ProofClaim(ProofClaimKind.Shown, new[] { 1, 2 }, "it passed on the second try"), calls).Sound);
    }

    /// <summary>
    /// A citation of a call that was never made. This is the shape a model reaches for when it wants
    /// to agree and has nothing to agree with, and it is caught by arithmetic rather than by insight.
    /// </summary>
    [Fact]
    public void A_proof_that_cites_a_call_nobody_made_does_not_hold()
    {
        var calls = new[] { Action(ActionOutcome.Succeeded) };

        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.Shown, new[] { 4 }, "call 4 shows it"), calls);

        Assert.False(verdict.Sound);
        Assert.Contains("cites call(s) 4", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("made 1", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>Claims to point at something and points at nothing. An absence is not an answer.</summary>
    [Fact]
    public void A_proof_that_names_no_call_at_all_does_not_hold()
    {
        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.Shown, Array.Empty<int>(), "it clearly worked"),
            new[] { Action(ActionOutcome.Succeeded) });

        Assert.False(verdict.Sound);
        Assert.Contains("no call was named", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// NOTHING THERE is a result, not a failure. A step whose objective was to establish that a file
    /// is absent is proven by the call that found it absent — the same distinction the engine
    /// already draws everywhere else, and the one the execution reviewer once got wrong.
    /// </summary>
    [Fact]
    public void A_lookup_that_found_nothing_can_still_prove_something()
        => Assert.True(ProofAudit.Check(
            new ProofClaim(ProofClaimKind.Shown, new[] { 1 }, "the file is not there, which was the question"),
            new[] { Action(ActionOutcome.Answered, "read_file") }).Sound);

    /// <summary>A call that never ran cannot show anything either.</summary>
    [Fact]
    public void A_proof_resting_on_a_refused_call_does_not_hold()
    {
        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.Shown, new[] { 1 }, "the command shows it"),
            new[] { Action(ActionOutcome.Refused) });

        Assert.False(verdict.Sound);
        Assert.Contains("never ran", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch, and it is never held against a step. Without it this pass would be asking
    /// "should a command have been run", which is the exact question the execution reviewer was told
    /// on 2026-09-07 20:16 to stop asking after it failed an analysis step for running no analysis
    /// command.
    /// </summary>
    [Fact]
    public void A_step_no_call_could_settle_is_not_failed_for_it()
    {
        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.NotByAnyCall, Array.Empty<int>(), "it read the code and explained it"),
            new[] { Action(ActionOutcome.Succeeded, "read_file") });

        Assert.True(verdict.Sound);
        Assert.Contains("no tool call could settle", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>The model's own "no" — the one answer here that is a judgement, and it is acted on.</summary>
    [Fact]
    public void Not_shown_is_not_sound()
    {
        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.NotShown, Array.Empty<int>(), "the test still fails"),
            new[] { Action(ActionOutcome.Succeeded) });

        Assert.False(verdict.Sound);
        Assert.Contains("nothing in the evidence shows", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("the test still fails", verdict.Reason, StringComparison.Ordinal);
    }

    // ── the evidence has to be citable ──────────────────────────────────────

    /// <summary>
    /// A citation needs a handle. The calls are numbered in the evidence, from 1 within the slice
    /// the reviewer is shown — not within the run, which would name calls this evidence does not
    /// contain and make the audit check the wrong list.
    /// </summary>
    [Fact]
    public void The_evidence_numbers_its_calls_from_one()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", """{"path":"a.txt"}""", ActionOutcome.Succeeded, "aaa");
        var mark = journal.Mark();
        journal.Record(1, "run_command", """{"command":"build"}""", ActionOutcome.Succeeded, "ok");
        journal.Record(1, "run_command", """{"command":"test"}""", ActionOutcome.Failed, "1 failed");

        var described = journal.Describe(mark);

        Assert.Contains("[1] -> run_command", described, StringComparison.Ordinal);
        Assert.Contains("[2] -> run_command", described, StringComparison.Ordinal);
        Assert.DoesNotContain("[3]", described, StringComparison.Ordinal);
        Assert.Contains("numbered [n]", described, StringComparison.Ordinal);

        // And the numbers resolve back to the same calls the reviewer was shown.
        Assert.Equal(2, journal.CountFrom(mark));
        Assert.Equal(ActionOutcome.Succeeded, journal.Cited(1, mark)!.Outcome);
        Assert.Equal(ActionOutcome.Failed, journal.Cited(2, mark)!.Outcome);
        Assert.Null(journal.Cited(3, mark));
        Assert.Null(journal.Cited(0, mark));
    }

    // ── reading the answer ──────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"shown":"yes","calls":[2],"what":"the build"}""", ProofClaimKind.Shown)]
    [InlineData("""{"shown":"no","calls":[],"what":"still failing"}""", ProofClaimKind.NotShown)]
    [InlineData("""{"shown":"not-by-any-call","calls":[],"what":"analysis"}""", ProofClaimKind.NotByAnyCall)]
    [InlineData("""{"shown":"NOT_BY_ANY_CALL","calls":[],"what":"analysis"}""", ProofClaimKind.NotByAnyCall)]
    public void The_three_answers_are_read(string json, ProofClaimKind expected)
        => Assert.Equal(expected, Reviewer.ParseProof(json)!.Kind);

    /// <summary>A word that is not one of the three is not an answer, and does not become one.</summary>
    [Theory]
    [InlineData("""{"shown":"maybe","calls":[],"what":""}""")]
    [InlineData("""{"shown":"","calls":[],"what":""}""")]
    [InlineData("""{"calls":[1],"what":"no verdict at all"}""")]
    [InlineData("there is no json in this reply")]
    public void An_answer_that_says_none_of_the_three_is_not_read(string json)
        => Assert.Null(Reviewer.ParseProof(json));

    /// <summary>
    /// A number written as the text around it — "3", "[3]" — has still pointed at a call. Failing a
    /// proof over its punctuation would be failing work for the model's formatting.
    /// </summary>
    [Fact]
    public void A_call_number_written_as_text_still_counts()
    {
        var claim = Reviewer.ParseProof("""{"shown":"yes","calls":["3","[4]",5],"what":"x"}""");
        Assert.Equal(new[] { 3, 4, 5 }, claim!.Calls);
    }

    // ── through the engine ──────────────────────────────────────────────────

    /// <summary>
    /// The whole defect, end to end: the reviewer passes the report as true, the proof pass says
    /// nothing shows the objective was met, and the step is no longer green.
    /// </summary>
    [Fact]
    public async Task A_true_report_of_an_unsupported_conclusion_no_longer_passes()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(OneStepPlan), RunsTests())
        {
            WhenExhausted = Turn.Says("I targeted the failing test.")
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass("every fact checks out"), Verdicts.NotShown());

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "make the failing test pass");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(Failures(events), s => s.Contains("soundness", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Failures(events), s => s.Contains("still failing", StringComparison.Ordinal));
    }

    /// <summary>A step whose proof holds up passes, and says what held it up.</summary>
    [Fact]
    public async Task A_step_whose_proof_holds_up_passes()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(OneStepPlan), RunsTests())
        {
            WhenExhausted = Turn.Says("the tests pass now")
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Shown("the test run succeeded", 1));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "make the failing test pass");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("soundness", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Failures(events));
    }

    /// <summary>
    /// A citation of a call that was never made fails the step even though the pass SAID yes. The
    /// engine is not taking the answer's word for it, which is the point of asking for a number.
    /// </summary>
    [Fact]
    public async Task A_yes_that_cites_a_call_nobody_made_still_fails()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(OneStepPlan), RunsTests())
        {
            WhenExhausted = Turn.Says("done")
        };
        // One call was made. The proof points at a ninth.
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Shown("call nine shows it", 9));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "make the failing test pass");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(Failures(events), s => s.Contains("cites call(s) 9", StringComparison.Ordinal));
    }

    /// <summary>
    /// A step that ran NOTHING is asked exactly like any other, and passes when its work is work no
    /// call could settle.
    ///
    /// <para>This used to be skipped, on the reasoning that "there is nothing to cite, so the answer
    /// is known before it is asked". It is not: with no calls the answer is "not-by-any-call" for an
    /// analysis and NOT SOUND for anything else, and which one it is cannot be known without asking.
    /// Closed 2026-09-08, §9af — the run that showed it did the same work twice, ten minutes apart,
    /// and the copy that made one call was failed while the copy that made none was never asked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_step_that_ran_nothing_is_asked_like_any_other_and_may_pass()
    {
        using var fx = new EngineFixture();

        // No tool calls: the step answers in text.
        var worker = new FakeChatProvider(Turn.Says(OneStepPlan))
        {
            WhenExhausted = Turn.Says("having read it, here is the analysis")
        };
        var reviewer = new FakeChatProvider(
            Verdicts.Pass("nothing to check"),
            Verdicts.NotByAnyCall("this step's work was reading and reasoning"));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "analyse the code");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(2, reviewer.Requests.Count);
        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("no tool call could settle", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the reported run. A step that ran nothing, reported that nothing needed changing, and had
    /// passed its execution review is now asked — and "nothing needed doing" with no call behind it
    /// is not believed, because it is a finding and a finding rests on having looked.
    ///
    /// <para>The evidence the pass is shown here reads "(no tools were run in this step)". That is a
    /// real answer to the question, which is the whole point: the gate used to be silent in exactly
    /// this case and loud for the step beside it that had done the same work with one call.</para>
    /// </summary>
    [Fact]
    public async Task A_step_that_ran_nothing_cannot_report_that_nothing_needed_doing()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(OneStepPlan))
        {
            WhenExhausted = Turn.Says("It was already correct, so I changed nothing.")
        };
        var reviewer = new FakeChatProvider(
            Verdicts.Pass("it did not claim to have run anything"),
            Verdicts.NothingToDo("it was already correct"));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "fix it if it is broken");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(Failures(events), s => s.Contains("named no call that looked", StringComparison.Ordinal));
    }

    /// <summary>
    /// Off means off. The pass is a switch because it costs a second call on the most expensive
    /// model bound, and a setting that quietly still ran would be worse than not having one.
    /// </summary>
    [Fact]
    public async Task With_the_check_off_nothing_is_asked()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(OneStepPlan), RunsTests())
        {
            WhenExhausted = Turn.Says("I targeted the failing test.")
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: false),
            "make the failing test pass");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(reviewer.Requests);
    }

    /// <summary>
    /// An unsound step earns the same retry a rejected one does, and is told what its report rests
    /// on that does not hold it up — which is a more useful thing to be told than that it was wrong
    /// about a fact.
    /// </summary>
    [Fact]
    public async Task An_unsound_step_is_told_why_and_gets_another_go()
    {
        using var fx = new EngineFixture();

        // Two attempts, each one call and a closing sentence. The retry KEEPS the transcript for an
        // execution review, so the second attempt's evidence still holds the first attempt's call —
        // which is why the proof below can cite call 1.
        var worker = new FakeChatProvider(
            Turn.Says(OneStepPlan), RunsTests(), Turn.Says("I targeted the failing test."), RunsTests())
        {
            WhenExhausted = Turn.Says("now it passes")
        };
        var reviewer = new FakeChatProvider(
            Verdicts.Pass(), Verdicts.NotShown("the test named in the report is still failing"),
            Verdicts.Pass(), Verdicts.Shown("it passes now", 1));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 1, checkSoundness: true),
            "make the failing test pass");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(Failures(events));

        // The reason reached the worker, not only the log.
        Assert.Contains(
            worker.Requests.SelectMany(r => r.Messages).Select(m => m.Content ?? ""),
            c => c.Contains("still failing", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fails CLOSED, like the verdict beside it. A pass that never answered has not proven anything,
    /// and treating "we could not find out" as "it is fine" is how a gate ends up enforcing nothing
    /// while looking configured.
    /// </summary>
    [Fact]
    public async Task A_proof_pass_that_never_answers_does_not_pass_the_step()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(OneStepPlan), RunsTests())
        {
            WhenExhausted = Turn.Says("done")
        };
        // The verdict, then two replies with nothing in them — the re-ask and its answer.
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Turn.Says("sure"), Turn.Says("looks fine to me"));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "make the failing test pass");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(Failures(events), s => s.Contains("did not answer", StringComparison.Ordinal));
    }
}

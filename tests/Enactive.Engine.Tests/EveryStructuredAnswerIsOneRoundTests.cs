namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Every structured answer the engine asks a model for goes through one round (StructuredAnswer): the budget kept, an
/// unusable answer told once what was wrong, the tokens of every call counted, a provider's failure said.
///
/// <para>Until 2026-10-08 the planner, its repair, the failing-check decision and the check diagnosis each wrote that
/// round by hand. The failing-check decision ignored the budget it was handed; it and the diagnosis took one unusable
/// answer as final - a check the request may not ask to pass kept, a repair stopped; a planning call that failed threw.</para>
/// </summary>
public sealed class EveryStructuredAnswerIsOneRoundTests
{
    private static RunBudget Spent() => new(new ExecutionLimits(MaxTokens: 100), DateTimeOffset.UtcNow, tokensAlreadySpent: 100);

    // ── a correction ────────────────────────────────────────────────────────

    /// <summary>
    /// A round that will ask again has to say what was wrong, and is refused before it asks anything when it cannot. One
    /// of a single attempt never asks again and needs none: the plan's repair handed in an empty correction for it.
    /// </summary>
    [Fact]
    public async Task Only_a_round_that_asks_again_needs_a_correction()
    {
        var provider = new FakeChatProvider(Turn.Says("not an answer"));
        Task<AnswerRound<string>> Ask(int attempts) => StructuredAnswer.AskAsync<string>(provider, [],
            messages => new ChatRequest("a-model", messages), (_, _) => (null, ["not an answer"]),
            null, null, requireComplete: true, default, attempts);

        await Assert.ThrowsAsync<ArgumentException>(() => Ask(attempts: 2));
        Assert.Empty(provider.Requests);

        Assert.Equal(AnswerKind.Unusable, (await Ask(attempts: 1)).Kind);
        Assert.Single(provider.Requests);
    }

    // ── what a round that gave nothing says ─────────────────────────────────

    private static AnswerRound<string> Round(AnswerKind kind, bool cutOff = false, bool readUnfinished = false, params string[] errors)
        => new(kind, kind == AnswerKind.Answered ? "x" : null, errors,
               kind is AnswerKind.Failed or AnswerKind.OutOfBudget ? "the provider is down" : null, TokenUsage.None)
           { CutOff = cutOff, ReadUnfinished = readUnfinished };

    /// <summary>
    /// A round that gave nothing usable says why in one wording, whoever asked. The diagnosis, the failing-check decision,
    /// the plan and its contract each switched over the round's kind and said the same four things four ways. A reader
    /// that judged an unfinished answer itself is quoted: "cut off" would put a vaguer word in place of its own.
    /// </summary>
    [Fact]
    public void A_round_that_gave_nothing_says_why_in_one_wording()
    {
        Assert.Null(Round(AnswerKind.Answered).Shortfall("The plan"));
        Assert.Equal("The plan failed: the provider is down", Round(AnswerKind.Failed).Shortfall("The plan"));
        Assert.Equal("the provider is down", Round(AnswerKind.OutOfBudget).Shortfall("The plan"));
        Assert.Equal("The plan was cut off at its length limit",
            Round(AnswerKind.Unusable, cutOff: true, errors: "your answer was cut off").Shortfall("The plan"));
        Assert.Equal("The plan could not be used: the contract ends mid-field",
            Round(AnswerKind.Unusable, cutOff: true, readUnfinished: true, "the contract ends mid-field").Shortfall("The plan"));
        Assert.Equal("The plan could not be used: a; b", Round(AnswerKind.Unusable, errors: ["a", "b"]).Shortfall("The plan"));
        Assert.Equal("The plan could not be used", Round(AnswerKind.Unusable).Shortfall("The plan"));
    }

    // ── the decision on a check that fails before the work ──────────────────

    private static readonly SuccessCriterionDefinition Site = new("site shows the report", "check-site");

    private static readonly CriterionResult SiteFailing = new("site shows the report", "check-site", true, CriterionOutcome.Failed, 1, null)
        { Output = "404 - the upload is broken" };

    private static Task<FailingCheckReview.Decision> Decide(FakeChatProvider provider, RunBudget? budget = null)
        => FailingCheckReview.RunAsync("Write the disk report. Do not touch the report site.", [(Site, SiteFailing)], provider, "strong",
            budget ?? RunBudget.Unlimited(), 1000, CancellationToken.None);

    /// <summary>An unusable answer is told what was wrong and asked once more; it used to keep the check outright.</summary>
    [Fact]
    public async Task An_unusable_decision_is_corrected_once_and_the_tokens_of_both_counted()
    {
        var provider = new FakeChatProvider(Turn.Says("I think the site is not ours.").Reporting(10, 5),
            Turn.Says("""{"checks":[{"name":"site shows the report","keep":false,"reason":"the site is not part of the request"}]}""").Reporting(10, 5));

        var decision = await Decide(provider);

        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("there is no JSON object in the answer", provider.Requests[1].Messages[^1].Content);
        Assert.Equal((Site, "the site is not part of the request"), Assert.Single(decision.Dropped));
        Assert.Equal((20, 10), (decision.Usage.Prompt, decision.Usage.Completion));
        Assert.Null(decision.Problem);
    }

    /// <summary>The run's budget was handed in and never looked at: spent, nothing is asked and the check stays.</summary>
    [Fact]
    public async Task A_spent_budget_asks_nothing_and_keeps_the_check()
    {
        var provider = new FakeChatProvider(Turn.Says("""{"checks":[]}"""));

        var decision = await Decide(provider, Spent());

        Assert.Empty(provider.Requests);
        Assert.Empty(decision.Dropped);
        // Said in the budget's own words, as every round that ran out is.
        Assert.False(string.IsNullOrWhiteSpace(decision.Problem));
    }

    [Fact]
    public async Task A_decision_cut_off_at_its_limit_says_so()
    {
        var provider = new FakeChatProvider(new Turn("""{"checks":[{"name":"site""", FinishReason: "length"),
            new Turn("""{"checks":[{"name":"site""", FinishReason: "length"));

        Assert.Equal("the decision was cut off at its length limit", (await Decide(provider)).Problem);
    }

    // ── the diagnosis of a proposed check that failed after the work ────────

    private static readonly WorkContext Context = new(Guid.NewGuid(), "disks", null, null, null, [], []);

    /// <summary>
    /// A diagnosis with a wrong entry is told which, and asked once more: one bad index used to stop the repair outright,
    /// with the original criteria retained.
    /// </summary>
    [Fact]
    public async Task A_diagnosis_with_a_wrong_entry_is_told_which_and_corrected()
    {
        var provider = new FakeChatProvider(
            Turn.Says("""{"decisions":[{"index":5,"kind":"work","reason":"the report is missing","command":"check-site"}]}""").Reporting(10, 5),
            Turn.Says("""{"decisions":[{"index":0,"kind":"work","reason":"the report is missing","command":"check-site"}]}""").Reporting(10, 5));

        var diagnosis = await CheckDiagnosis.RunAsync("Write the disk report.", Context, [SiteFailing], provider, "strong",
            RunBudget.Unlimited(), CancellationToken.None);

        Assert.Contains("index 5 is not one of the failed checks", provider.Requests[1].Messages[^1].Content);
        Assert.Equal(new CheckDecision(0, "work", "the report is missing", "check-site"), Assert.Single(diagnosis.Decisions!));
        Assert.Equal((20, 10), (diagnosis.Spent.Prompt, diagnosis.Spent.Completion));
    }

    // ── the planner ─────────────────────────────────────────────────────────

    private sealed class Failing(params Turn[] before) : IChatProvider
    {
        private readonly FakeChatProvider _first = new(before);
        private int _left = before.Length;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => _left-- > 0 ? _first.CompleteAsync(request, ct) : Task.FromException<ChatCompletion>(new HttpRequestException("the provider is down"));

        public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A planning call that fails is said in the result - the run ends "Planning failed", as it did by an exception.</summary>
    [Fact]
    public async Task A_planning_call_that_fails_is_said_and_not_thrown()
    {
        var plan = await new Planner().PlanAsync("check the disks", Context, new Failing(), "strong", CancellationToken.None);

        Assert.Equal("Planning failed: the provider is down", plan.IncompleteReason);
    }

    /// <summary>A plan that ran out of room starts no work, even when the correction was unreadable for another reason.</summary>
    [Fact]
    public async Task A_plan_cut_off_and_then_unreadable_starts_nothing()
    {
        var provider = new FakeChatProvider(new Turn("""{"disposition":"task","title":"disks","steps":[{"title":""", FinishReason: "length"),
            Turn.Says("I will check the disks."));

        var plan = await new Planner().PlanAsync("check the disks", Context, provider, "strong", CancellationToken.None);

        Assert.Equal("Planner clarification was cut off at its length limit; no work was started.", plan.IncompleteReason);
    }

    /// <summary>A plan repair whose call fails ends the run unfinished, said - from the result, not from a caught exception.</summary>
    [Fact]
    public async Task A_plan_repair_whose_call_fails_ends_the_run_unfinished()
    {
        using var fx = new EngineFixture();
        var provider = new Failing(Turn.Says("""{"disposition":"task","title":"bad","steps":[{"title":"write","dependsOn":[0]}]}"""));

        var events = await fx.RunAsync(fx.Build(provider), "write");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains(events, e => e.Summary == "Plan repair failed: the provider is down");
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
    }

    // ── the rule ────────────────────────────────────────────────────────────

    /// <summary>What asks a model for a structured answer asks it through StructuredAnswer, and calls no provider itself.</summary>
    [Theory]
    [InlineData("Planner.cs")]
    [InlineData("FailingCheckReview.cs")]
    [InlineData("CheckDiagnosis.cs")]
    [InlineData("StepVerdictReview.cs")]
    [InlineData("CriteriaReview.cs")]
    [InlineData("PlanCheckReview.cs")]
    public void A_structured_answer_is_asked_for_through_the_one_round(string file)
    {
        var source = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.Agents", file));

        Assert.DoesNotContain("CompleteAsync(", source, StringComparison.Ordinal);
    }
}

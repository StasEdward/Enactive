namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// The round every review shares - ask for a structured answer, and correct it once - asked directly; and what a
/// reviewer's verdict makes of a step.
///
/// <para>Until 2026-10-08 the step's review, the criteria review and the plan's contract review each wrote the round
/// by hand, and the copies drifted: the criteria review read an answer cut off at its length limit as a finished
/// verdict, where the step's review sent it back. The verdict was five overlapping fields read in the right order.</para>
/// </summary>
public sealed class StructuredAnswerTests
{
    private static Task<AnswerRound<string>> Ask(IChatProvider provider, Func<int, int, string?>? budget = null,
        CancellationToken ct = default)
        => StructuredAnswer.AskAsync<string>(provider,
            [ChatMessage.User("How full is disk C:? Answer as {\"percent\":N}.")],
            messages => new ChatRequest("model", messages, Temperature: 0),
            (answer, _) => answer.Contains("percent", StringComparison.Ordinal)
                ? (answer, [])
                : (null, ["no percent in the answer"]),
            errors => StructuredAnswer.Listed(errors, "Return the corrected JSON object."),
            budget, requireComplete: true, ct);

    [Fact]
    public async Task An_answer_that_cannot_be_used_is_corrected_once_and_the_second_is_taken()
    {
        var provider = new FakeChatProvider(Turn.Says("C: is quite full").Reporting(100, 10), Turn.Says("{\"percent\":91}").Reporting(150, 8));

        var round = await Ask(provider);

        Assert.Equal(AnswerKind.Answered, round.Kind);
        Assert.Equal("{\"percent\":91}", round.Value);
        Assert.Contains("- no percent in the answer", provider.Requests[1].Messages.Last().Content);
        // Both calls were made, and both are paid for.
        Assert.Equal((250, 18), (round.PromptTokens, round.CompletionTokens));
    }

    [Fact]
    public async Task Two_answers_that_cannot_be_used_leave_the_round_unusable_with_what_was_wrong()
    {
        var round = await Ask(new FakeChatProvider(Turn.Says("full"), Turn.Says("very full")));

        Assert.Equal(AnswerKind.Unusable, round.Kind);
        Assert.Equal(["no percent in the answer"], round.Errors);
    }

    /// <summary>An answer cut off at its length limit is not a finished answer, however whole the JSON in it looks.</summary>
    [Fact]
    public async Task An_answer_cut_off_at_its_limit_is_sent_back_unread()
    {
        var provider = new FakeChatProvider(Turn.Says("{\"percent\":91}") with { FinishReason = "length" }, Turn.Says("{\"percent\":91}"));

        var round = await Ask(provider);

        Assert.Equal(AnswerKind.Answered, round.Kind);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("cut off at its length limit", provider.Requests[1].Messages.Last().Content);
    }

    [Fact]
    public async Task A_provider_that_fails_ends_the_round_with_its_error()
    {
        var round = await Ask(new Failing(new HttpRequestException("Nothing is listening at http://localhost:11434")));

        Assert.Equal(AnswerKind.Failed, round.Kind);
        Assert.Contains("Nothing is listening", round.Problem);
    }

    [Fact]
    public async Task A_cancelled_round_is_cancelled_and_not_a_failure()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ask(new Failing(new OperationCanceledException())));

    [Fact]
    public async Task A_budget_spent_on_the_first_answer_stops_the_correction()
    {
        var provider = new FakeChatProvider(Turn.Says("full").Reporting(5000, 10));

        var round = await Ask(provider, (prompt, _) => prompt >= 5000 ? "the run's token budget is spent" : null);

        Assert.Equal(AnswerKind.OutOfBudget, round.Kind);
        Assert.Equal("the run's token budget is spent", round.Problem);
        Assert.Single(provider.Requests);
    }

    // ── what a verdict makes of a step ──────────────────────────────────────

    [Fact]
    public void A_missing_verdict_leaves_the_work_done_and_says_why_it_is_missing()
    {
        Assert.Null(new ReviewVerdict.Pass("done").Missing);
        Assert.Null(new ReviewVerdict.Fail("not done", "do it", []).Missing);
        Assert.Equal((StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUndecided), new ReviewVerdict.Undecided("cannot tell").Missing);
        Assert.Equal((StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUnprocessable), new ReviewVerdict.Unavailable("review error").Missing);
        Assert.Equal((StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUnprocessable), new ReviewVerdict.OutOfBudget("spent").Missing);
    }

    // ── the drift this ended ────────────────────────────────────────────────

    /// <summary>
    /// The criteria review read an answer cut off at its length limit as a finished verdict - the step's review had
    /// sent such an answer back since engeen_v4's code review (P2). Both go through one round now.
    /// </summary>
    [Fact]
    public async Task The_criteria_review_sends_back_an_answer_cut_off_at_its_limit()
    {
        const string verdict = """{"criteria":[{"id":"C1","verdict":"pass","reason":"every disk is named","files":["disks.md"]}]}""";
        var provider = new FakeChatProvider(Turn.Says(verdict) with { FinishReason = "length" }, Turn.Says(verdict));
        var input = new CriteriaReviewInput("Write the disk report", 1, "Done.", null,
            [new ShownFile("disks.md", "C: 91%\nD: 40%", Whole: true)], new ExecutionJournal().Describe(),
            RequestObligations.Create("write the disk report", "Write the disk report", 1, null));
        var criterion = new SuccessCriterionDefinition("names every disk", "")
            { Step = 0, Typed = new TypedCriterion(TypedCriterionKind.Semantic, Text: "the report names every disk") };

        var result = await CriteriaReview.RunAsync(input, [criterion], (_, _) => true, provider, "reviewer", null, CancellationToken.None);

        Assert.IsType<ReviewVerdict.Pass>(result.Verdict);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("cut off at its length limit", provider.Requests[1].Messages.Last().Content);
    }

    private sealed class Failing(Exception error) : IChatProvider
    {
        public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct) => throw error;
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct) => throw error;
    }
}

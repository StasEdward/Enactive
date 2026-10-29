namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// The review after a repair is shown what the review before it said: the repair was made to that verdict, and judged
/// without it the second review took the repair for the worker's own claim and failed it in other words.
///
/// <para><b>Measured 2026-10-29</b> (bench test-coverage, runs 20261029-112618 and 20261028-221352): the first review
/// failed a coverage step for naming the wrong line of an untested branch and said what to write; the worker wrote it,
/// and the second review - shown the work alone - failed the step on that very line. In the other run the first review
/// asked for 1/6 branches covered, the worker wrote 1/6, and the second asked for 3/6. Either way the step was rejected
/// and the three steps after it skipped.</para>
/// </summary>
public sealed class TheRetryReviewSeesItsEarlierVerdictTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";
    private const string FirstVerdict = "the report names line 11 for the untested branch; it is line 12";

    [Fact]
    public async Task The_review_after_a_repair_is_shown_the_verdict_it_repaired_and_the_first_is_shown_none()
    {
        using var fx = new EngineFixture();
        fx.Write("src.md", "line 12: if (count < 1)");

        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"src.md"}""", "r1"),
            Turn.Calls1("write_file", """{"path":"report.md","content":"untested: line 11"}""", "w1"),
            Turn.Says("Wrote the report."),
            Turn.Calls1("edit_file", """{"path":"report.md","old_string":"line 11","new_string":"line 12"}""", "e1"),
            Turn.Says("Fixed the line."));
        var reviewer = new FakeChatProvider(Verdicts.Fail(FirstVerdict), Verdicts.Pass());

        var events = await fx.RunAsync(
            fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "write the report");

        var reviews = reviewer.Requests.Select(r => string.Join("\n", r.Messages.Select(m => m.Content))).ToArray();
        Assert.Equal(2, reviews.Length);
        Assert.DoesNotContain("EARLIER REVIEWS", reviews[0], StringComparison.Ordinal);
        Assert.Contains("EARLIER REVIEWS", reviews[1], StringComparison.Ordinal);
        Assert.Contains(FirstVerdict, reviews[1], StringComparison.Ordinal);
        Assert.True(events.Has(Enactive.Core.Events.EventKind.TaskCompleted), events.Text());
    }
}

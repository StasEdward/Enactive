namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// The second review mode (2026-09-06). A step that only writes text has no exit code to judge, so
/// execution review passes anything it produces — which is how a cluster guide full of invented
/// package names, made-up pcs syntax and stray CJK characters inside an identifier finished green.
/// </summary>
public sealed class ContentReviewTests
{
    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    // The point of the whole thing: a rejected document stops the run.
    [Fact]
    public async Task Invented_content_fails_the_run()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a guide"}"""),
            Turn.Calls1("write_file", """{"path":"guide.md","content":"sudo apt install libmkfailover-dev"}"""),
            Turn.Says("Wrote the guide."));

        var reviewer = new FakeChatProvider
        {
            WhenExhausted = Verdicts.Fail("libmkfailover-dev is not a real package")
        };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.ReviewFailed
                                     && e.Summary.Contains("libmkfailover-dev", StringComparison.Ordinal));
    }

    // ── a step that composed nothing ─────────────────────────────────────────────────

    // ── an excerpt has to announce itself ─────────────────────────────────

}

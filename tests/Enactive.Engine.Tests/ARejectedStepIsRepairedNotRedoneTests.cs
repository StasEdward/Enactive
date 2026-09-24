namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// After a rejection, the step repairs the points the reviewer named - from what it had already
/// established - instead of starting over.
///
/// <para><b>Measured 2026-09-24.</b> A content review rejected an audit step, rightly: some claims were
/// confirmed by the documentation itself instead of the code. The attempt was then cut out of the
/// transcript and the evidence with "Redo this step from scratch", and the step spent another 58.7 s
/// of local generation and repeated 10 reads exactly - the sources went out with the draft.</para>
/// </summary>
public sealed class ARejectedStepIsRepairedNotRedoneTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";

    [Fact]
    public async Task What_was_read_is_still_there_after_a_rejection()
    {
        using var fx = new EngineFixture();
        fx.Write("src.md", "SOURCE-FACT: the window is 8000 characters");

        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"src.md"}""", "r1"),
            Turn.Calls1("write_file", """{"path":"report.md","content":"claim: confirmed by the wiki"}""", "w1"),
            Turn.Says("Wrote the report."),
            // After the rejection: a repair in place, not a rewrite.
            Turn.Calls1("edit_file", """{"path":"report.md","old_string":"confirmed by the wiki","new_string":"confirmed by src.md line 1"}""", "e1"),
            Turn.Says("Fixed the citation."));
        var reviewer = new FakeChatProvider(
            Verdicts.Fail("the claim is confirmed by the wiki itself, not by the code"),
            Verdicts.Pass());

        var events = await fx.RunAsync(
            fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "write the report");

        var afterRejection = worker.Requests.First(r => r.Messages.Any(
            m => m.Content?.Contains("A reviewer rejected", StringComparison.Ordinal) == true));
        var said = string.Join("\n", afterRejection.Messages.Select(m => m.Content));

        // The source it read is still in front of it.
        Assert.Contains("SOURCE-FACT", said, StringComparison.Ordinal);

        // And it is told to repair, not to start again.
        Assert.Contains("Repair the work: fix exactly the points above", said, StringComparison.Ordinal);
        Assert.Contains("do not read or run it again", said, StringComparison.Ordinal);
        Assert.DoesNotContain("from scratch", said, StringComparison.Ordinal);

        // The repair landed on the file the first attempt wrote.
        Assert.Equal("claim: confirmed by src.md line 1", fx.Read("report.md"));
        Assert.True(events.Has(Enactive.Core.Events.EventKind.TaskCompleted), events.Text());
    }
}

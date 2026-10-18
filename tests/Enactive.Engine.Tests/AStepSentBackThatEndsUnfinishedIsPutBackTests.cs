namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Bench build-error, 2026-10-18: the step review rejected the step for a change to a file the request forbade, made with a
/// PowerShell <c>-replace | Set-Content</c> after the write boundary had refused the edit twice. The attempt after the
/// rejection got stuck and the step ended INCOMPLETE - and with RevertRejectedSteps on, the run ended with the forbidden
/// change still there: only a ReviewRejected step was put back, and a command's writes are not the store's to put back.
/// A step whose last verdict sent its work back and that ended with no verdict since is now put back as a rejected step
/// is; and what a command changed that a revert leaves is named, with why it is left.
/// </summary>
public sealed class AStepSentBackThatEndsUnfinishedIsPutBackTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write"}""";

    private static IReadOnlyList<WorkEvent> Reverts(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.ArtifactReverted).ToArray();

    /// <summary>
    /// THE ONE THAT MATTERS: rejected by its review, the step's next attempt only goes round in circles and is stopped -
    /// Incomplete, with no verdict since the rejection. Its work goes back as a rejected step's does, and the log says why.
    /// </summary>
    [Fact]
    public async Task A_step_sent_back_by_its_review_that_then_ends_incomplete_has_its_work_put_back()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."))
        {
            // The attempt after the rejection reads the same file over and over: the engine stops it as stuck.
            WhenExhausted = Turn.Calls1("read_file", """{"path":"record.txt"}""", "r1")
        };
        var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"));

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains(Reverts(events), e => e.Summary.StartsWith("The step ended incomplete after its last verdict sent its work back",
            StringComparison.Ordinal));
        Assert.Contains(Reverts(events), e => e.RevertedPaths()!.Contains("record.txt"));
    }

    /// <summary>
    /// A step that ends incomplete with no verdict ever against it keeps its work, as it always did: nobody said it was
    /// wrong. And one whose last attempt had a verdict of its own - what is left cannot be reached within the request -
    /// keeps it too, though an earlier verdict sent it back.
    /// </summary>
    [Fact]
    public async Task A_step_that_was_never_sent_back_or_was_last_judged_unreachable_keeps_its_work()
    {
        using (var fx = new EngineFixture())
        {
            var worker = new FakeChatProvider(Turn.Says(QuickAction),
                Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"))
            {
                WhenExhausted = Turn.Calls1("read_file", """{"path":"record.txt"}""", "r1")
            };
            var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: new FakeChatProvider()),
                "Write one record");

            Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
            Assert.Equal("one", fx.Read("record.txt"));
            Assert.Empty(Reverts(events));
        }

        using (var fx = new EngineFixture())
        {
            var worker = new FakeChatProvider(Turn.Says(QuickAction),
                Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."),
                Turn.Calls1("write_file", """{"path":"record.txt","content":"two"}""", "w2"), Turn.Says("Wrote it again."));
            var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"),
                Turn.Says(Verdicts.ShortVerdict("unreachable", "the rest needs a file the request forbids", [], [])));

            var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

            Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
            Assert.Equal("two", fx.Read("record.txt"));
            Assert.Empty(Reverts(events));
        }
    }

    /// <summary>
    /// A rejected step that changed a file through a command: the store puts back what the file tools wrote, and the file
    /// the command changed is named as left, with why - it used to be named nowhere, as if the revert had undone the step.
    /// </summary>
    [Fact]
    public async Task What_a_rejected_steps_command_changed_is_named_as_left_with_why()
    {
        using var fx = new EngineFixture();
        fx.Write("other.txt", "as it was");
        var worker = new FakeChatProvider(Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"),
            Turn.Calls1("run_command", """{"command":"echo changed> other.txt"}""", "c1"),
            Turn.Says("Wrote the record and tidied the other file.")) { WhenExhausted = Turn.Says("Wrote the record.") };
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Fail("other.txt was not to be changed") };

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains("changed", fx.Read("other.txt"), StringComparison.Ordinal);
        var left = Assert.Single(Reverts(events), e => e.Summary.StartsWith("Left as it is, changed by a command", StringComparison.Ordinal));
        Assert.Contains("other.txt - a command's writes are not recorded per file", left.Summary, StringComparison.Ordinal);
        Assert.Equal(["other.txt"], left.KeptPaths()!);
        Assert.DoesNotContain("record.txt", left.Summary, StringComparison.Ordinal);
    }
}

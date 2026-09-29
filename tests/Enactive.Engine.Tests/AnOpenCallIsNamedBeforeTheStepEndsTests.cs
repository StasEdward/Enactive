namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Amendment A, run 0cf51c, 2026-09-29: a command written for bash failed under cmd.exe; the step ran it again rightly
/// spelled two seconds later and it passed; and the step - never told the first was still open - ended INCOMPLETE on it,
/// two steps skipped. A step is now told once which calls are still open. What is still open after that goes to the
/// review, named by the engine, where there is one; without a reviewer the step is unfinished, as before.
/// Deliberately not code: a count of wiki pages.
/// </summary>
public sealed class AnOpenCallIsNamedBeforeTheStepEndsTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"count the pages"}""";

    private static FakeChatProvider Worker(params Turn[] after) => new(
        [Turn.Says(QuickAction),
         Turn.Calls1("run_command", """{"command":"exit 3"}""", "c1"),
         Turn.Calls1("run_command", """{"command":"echo 12 pages"}""", "c2"),
         Turn.Says("There are 12 pages."),
         .. after]);

    [Fact]
    public async Task Without_a_reviewer_the_step_is_told_once_and_then_unfinished()
    {
        using var fx = new EngineFixture();
        var worker = Worker(Turn.Says("There are 12 pages; the first command was a slip."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "count the wiki pages");

        var told = worker.Requests.Select(r => r.Messages.Last().Content ?? "").Where(c => c.StartsWith("Before this step ends: this call did not go through", StringComparison.Ordinal)).ToArray();
        Assert.Single(told);
        Assert.Contains("exit 3", told[0], StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("unresolved tool call", events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_made_good_after_the_reminder_ends_the_step_as_done()
    {
        using var fx = new EngineFixture();
        var worker = Worker(Turn.Calls1("run_command", """{"command":"exit 3","expectedExitCodes":[0,3]}""", "c3"), Turn.Says("There are 12 pages."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "count the wiki pages");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task With_a_reviewer_what_is_still_open_goes_to_it_named_and_it_decides()
    {
        using var fx = new EngineFixture();
        var worker = Worker(Turn.Says("There are 12 pages; 'exit 3' was a slip, and the count came from the second command."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "count the wiki pages");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var shown = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("engine_open_calls", shown, StringComparison.Ordinal);
        Assert.Contains("exit 3", shown, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.Contains("the review decides whether the result stands without them", StringComparison.Ordinal));
    }
}

namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Run a7a8cf, 2026-09-29: a step wrote four tests and saw all 133 pass, was refused a whole-file rewrite of its own
/// scratch notes (it had read them only in part), wrote a new notes file instead - and ended INCOMPLETE on the refused
/// call, its two dependents skipped. Scratch is kept out of review, of the workspace comparison and of rollback: a call
/// on it that did not go through leaves the work as it was. The same refusal on a file of the work still holds the step.
/// Deliberately not code: notes about a wiki.
/// </summary>
public sealed class ScratchIsNotTheWorkTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"fix the wiki page"}""";

    private static string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"line {i}"));

    private static FakeChatProvider Worker(string notes) => new(
        Turn.Says(QuickAction),
        Turn.Calls1("read_file", $$"""{"path":"{{notes}}","offset":390,"limit":10}""", "r1"),
        Turn.Calls1("write_file", $$"""{"path":"{{notes}}","content":"all new notes"}""", "w1"),
        Turn.Calls1("write_file", """{"path":"wiki/home.page","content":"# Home"}""", "w2"),
        Turn.Says("Fixed the page."));

    [Fact]
    public async Task A_refused_write_to_scratch_does_not_hold_the_step_open()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/notes.md", Lines(400));

        var events = await fx.RunAsync(fx.Build(Worker(".enactive/scratch/notes.md")), "fix the wiki home page");

        Assert.True(events.Any(e => e.Summary.Contains("refused — the file has only been read in part", StringComparison.Ordinal)), events.Text());
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task The_same_refusal_on_a_file_of_the_work_still_does()
    {
        using var fx = new EngineFixture();
        fx.Write("wiki/notes.page", Lines(400));

        var events = await fx.RunAsync(fx.Build(Worker("wiki/notes.page")), "fix the wiki home page");

        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

}

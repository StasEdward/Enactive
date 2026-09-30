namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A step the review rejects keeps its files when the review found the work itself right and
/// failed the step for something else - its report, its process.
///
/// <para><b>Measured 2026-09-28, run 3fe4f8.</b> The final review of step 1 said of the seven tests
/// it wrote "implementation: pass ... 7/7 pass", and failed the step for how its commands were run
/// and what its report left out. The step was rejected and the test file put back: the one thing the
/// review had found right was the one thing thrown away.</para>
/// </summary>
public sealed class RejectedButRightWorkIsKeptTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write"}""";

    private static async Task<(IReadOnlyList<WorkEvent> Events, EngineFixture Fx)> Run(string verdict)
    {
        var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}"""),
            Turn.Says("Two records")) { WhenExhausted = Turn.Says("Two records") };
        var reviewer = new FakeChatProvider { WhenExhausted = Turn.Says(verdict) };
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(),
            reviewProvider: reviewer), "Write one record");
        return (events, fx);
    }

    /// <summary>The short review says it in so many words: a fail whose work stands keeps the file, as the earlier one did.</summary>
    [Fact]
    public async Task A_short_review_that_fails_the_report_and_says_the_work_stands_keeps_the_file()
    {
        var (events, fx) = await Run("""{"verdict":"fail","reason":"the report says two records; there is one","calls":[1],"files":["record.txt"],"work_stands":true}""");
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.Equal("one", fx.Read("record.txt"));
        Assert.Contains(events, e => e.Summary.Contains("NOT put back: record.txt", StringComparison.Ordinal));
    }

    /// <summary>A short fail that does not say so is a fail of the work too, and it is put back, as it always was.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(",\"work_stands\":false")]
    public async Task A_short_review_that_does_not_say_the_work_stands_has_it_put_back(string stands)
    {
        var (events, fx) = await Run("""{"verdict":"fail","reason":"the record is wrong","calls":[1],"files":[]""" + stands + "}");
        using var _ = fx;

        Assert.False(File.Exists(Path.Combine(fx.Root, "record.txt")));
        Assert.Contains(events, e => e.Summary.StartsWith("Rejected work put back: record.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_fail_carries_it()
    {
        var input = new StepVerdictInput("write", "Write", 1, [], "Done.", null, [new ShownFile("record.txt", "one", true)],
            new Enactive.Core.Execution.ExecutionJournal().Describe());
        Assert.False(StepVerdictReview.Read("""{"verdict":"pass","reason":"ok","calls":[],"files":["record.txt"],"work_stands":true}""", input).Result!.WorkStands);
        Assert.True(StepVerdictReview.Read("""{"verdict":"fail","reason":"report","calls":[],"files":[],"work_stands":true}""", input).Result!.WorkStands);
        Assert.False(StepVerdictReview.Read("""{"verdict":"fail","reason":"report","calls":[],"files":[],"work_stands":"yes"}""", input).Result!.WorkStands);
    }

}

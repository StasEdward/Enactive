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

    private static async Task<(IReadOnlyList<WorkEvent> Events, EngineFixture Fx)> Run(string verdict, bool twoFiles = false)
    {
        var fx = new EngineFixture();
        if (twoFiles) fx.Write("other.txt", "as it was");
        var worker = new FakeChatProvider(
            [Turn.Says(QuickAction),
             Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}"""),
             .. twoFiles ? [Turn.Calls1("write_file", """{"path":"other.txt","content":"tidied"}""", "w2")] : Array.Empty<Turn>(),
             Turn.Says("Two records")]) { WhenExhausted = Turn.Says("Two records") };
        var reviewer = new FakeChatProvider { WhenExhausted = Turn.Says(verdict) };
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(),
            reviewProvider: reviewer), "Write one record");
        return (events, fx);
    }

    /// <summary>The short review names the file that is right: a fail of the report keeps it, as the earlier review did.</summary>
    [Fact]
    public async Task A_short_review_that_fails_the_report_and_keeps_the_file_keeps_it()
    {
        var (events, fx) = await Run("""{"verdict":"fail","reason":"the report says two records; there is one","calls":[1],"files":["record.txt"],"keep":["record.txt"]}""");
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.Equal("one", fx.Read("record.txt"));
        Assert.Contains(events, e => e.Summary.Contains("NOT put back: record.txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Benchmark build-error: asked to add Median to one file and leave the rest alone, the worker also tidied another
    /// file. Told only "the work stands" or not, the review said it stood in 12 of 12 answers - Median was right - and the
    /// change the request forbade would have been kept. Naming the files, it kept Stats.cs and not Report.cs, 12 of 12:
    /// the file named is kept, the rest of what the step changed goes back.
    /// </summary>
    [Fact]
    public async Task Only_the_files_the_review_keeps_are_kept()
    {
        var (events, fx) = await Run("""{"verdict":"fail","reason":"other.txt was not to be changed","calls":[1],"files":["record.txt","other.txt"],"keep":["./record.txt"]}""", twoFiles: true);
        using var _ = fx;

        Assert.Equal("one", fx.Read("record.txt"));
        Assert.Equal("as it was", fx.Read("other.txt"));
        Assert.Contains(events, e => e.Summary.Contains("NOT put back: record.txt", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Summary.StartsWith("Rejected work put back: other.txt", StringComparison.Ordinal));
    }

    /// <summary>A short fail that keeps nothing is a fail of the work too, and it is put back, as it always was.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(",\"keep\":[]")]
    [InlineData(",\"work_stands\":true")]
    public async Task A_short_review_that_keeps_nothing_has_it_put_back(string keep)
    {
        var (events, fx) = await Run("""{"verdict":"fail","reason":"the record is wrong","calls":[1],"files":[]""" + keep + "}");
        using var _ = fx;

        Assert.False(File.Exists(Path.Combine(fx.Root, "record.txt")));
        Assert.Contains(events, e => e.Summary.StartsWith("Rejected work put back: record.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_fail_carries_it()
    {
        var input = new StepVerdictInput("write", "Write", 1, [], "Done.", null, [new ShownFile("record.txt", "one", true)],
            new Enactive.Core.Execution.ExecutionJournal().Describe());
        // A pass keeps everything anyway: it names nothing to keep.
        Assert.IsType<ReviewVerdict.Pass>(StepVerdictReview.Read("""{"verdict":"pass","reason":"ok","calls":[],"files":["record.txt"],"keep":["record.txt"]}""", input).Verdict);
        Assert.Equal(["record.txt"], ((ReviewVerdict.Fail)StepVerdictReview.Read("""{"verdict":"fail","reason":"report","calls":[],"files":[],"keep":["record.txt"]}""", input).Verdict!).Keep);
        Assert.Empty(((ReviewVerdict.Fail)StepVerdictReview.Read("""{"verdict":"fail","reason":"report","calls":[],"files":[],"keep":"record.txt"}""", input).Verdict!).Keep);
    }

    /// <summary>The review is told to name what it keeps, and that a change the request did not ask for is not kept.</summary>
    [Fact]
    public async Task The_review_is_told_to_name_the_files_it_keeps()
    {
        var reviewer = new FakeChatProvider { WhenExhausted = Turn.Says("""{"verdict":"pass","reason":"ok","calls":[1],"files":[]}""") };
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(QuickAction), Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}"""), Turn.Says("One record"));
        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

        var instruction = reviewer.Requests[0].Messages[0].Content!;
        Assert.Contains("names it in\n\"keep\":[\"path\"]", instruction.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("a change the request did not ask for, is not kept", instruction.Replace("\r\n", " ").Replace("\n", " "), StringComparison.Ordinal);
        Assert.DoesNotContain("work_stands", instruction, StringComparison.Ordinal);
    }

}

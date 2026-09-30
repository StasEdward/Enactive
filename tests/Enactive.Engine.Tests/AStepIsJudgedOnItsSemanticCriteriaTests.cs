namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Phase 1.4 and 5.2: the engine decides completion; judgement enters only through semantic criteria, and only with
/// cited evidence. A step whose plan set semantic criteria is judged against those alone - its report is a claim - and
/// each verdict stands on evidence of a kind the criterion allows: a file read whole, a command that ran, a call that
/// succeeded. Run 793825, 2026-09-29: a step's work done, and the step left unconfirmed over "38 and 16 tests", a figure
/// in its report no criterion asked for. Deliberately not code: a stocktake.
/// </summary>
public sealed class AStepIsJudgedOnItsSemanticCriteriaTests
{
    private static string Plan(string evidence = "\"file_read\"") => $$"""
        {"disposition":"task","title":"stocktake",
         "steps":[{"title":"Count and report","dependsOn":[],
                   "criteria":[{"kind":"semantic","text":"The report states the number of boxes the shelf listing shows","evidence":[{{evidence}}]}]}]}
        """;

    private static Turn Verdict(string verdict, int call = 1, string file = "stock/report.md", string reason = "the listing shows 42 and the report says 42")
        => Turn.Says($$"""{"criteria":[{"id":"C1","verdict":"{{verdict}}","reason":"{{reason}}","calls":[{{call}}],"files":["{{file}}"]}]}""");

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Worker, FakeChatProvider Reviewer)> Run(
        Turn[] reviews, string? plan = null, bool semantic = true, string read = """{"path":"stock/shelf17.txt"}""", params Turn[] retry)
    {
        using var fx = new EngineFixture { TypedCriteria = true, SemanticCriteria = semantic };
        fx.Write("stock/shelf17.txt", "shelf 17: 42 boxes");
        var worker = new FakeChatProvider(
            [Turn.Says(plan ?? Plan()),
             Turn.Calls1("read_file", read, "r1"),
             Turn.Calls1("write_file", """{"path":"stock/report.md","content":"Shelf 17: 42 boxes"}""", "w1"),
             Turn.Says("Counted and reported."),
             .. retry]);
        var reviewer = new FakeChatProvider(reviews);
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer), "Count the boxes on shelf 17 and write a report.");
        return (events, worker, reviewer);
    }

    /// <summary>THE ONE THAT MATTERS: judged on its criterion alone, on evidence of the kind it allows.</summary>
    [Fact]
    public async Task A_step_is_judged_on_its_criterion_and_nothing_else()
    {
        var (events, _, reviewer) = await Run([Verdict("pass")]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var asked = string.Join("\n", reviewer.Requests.Single().Messages.Select(m => m.Content));
        Assert.Contains("C1: The report states the number of boxes the shelf listing shows (evidence: file_read)", asked, StringComparison.Ordinal);
        Assert.Contains("REPORT - the worker's claim, not evidence:", asked, StringComparison.Ordinal);
        Assert.Contains("--- stock/report.md", asked, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.Contains("PASS (Criteria review)", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("judged:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_criterion_is_put_right_on_its_reason()
    {
        var (events, worker, _) = await Run([Verdict("fail", reason: "the report says 40, the listing 42"), Verdict("pass")],
            retry: [Turn.Calls1("write_file", """{"path":"stock/report.md","content":"Shelf 17: 42 boxes"}""", "w2"), Turn.Says("Corrected.")]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains(worker.Requests, r => r.Messages.Any(m => m.Content?.Contains("These criteria of the step are not met", StringComparison.Ordinal) == true
                                                                 && m.Content.Contains("the report says 40, the listing 42", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task What_it_cannot_tell_leaves_the_step_unconfirmed_as_an_open_question()
    {
        var (events, _, _) = await Run([Verdict("unknown", reason: "the listing's count is not shown")]);

        var step = Assert.Single(events, e => e.Kind == EventKind.StepCompleted);
        Assert.Equal(StepOutcomeKind.DoneUnverified, step.StepOutcome());
        Assert.Contains("the listing's count is not shown", step.OutcomeReason(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_of_a_kind_the_criterion_does_not_allow_is_not_taken()
    {
        var (events, _, reviewer) = await Run([Verdict("pass"), Verdict("pass")], plan: Plan("\"command\""));

        Assert.Contains("call 1 (read_file) is not evidence of a kind this criterion allows (command)",
            reviewer.Requests[^1].Messages.Last().Content, StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.DoneUnverified, Assert.Single(events, e => e.Kind == EventKind.StepCompleted).StepOutcome());
    }

    [Fact]
    public async Task A_read_of_part_of_a_file_is_not_a_read_of_the_file()
    {
        using var fx = new EngineFixture { TypedCriteria = true, SemanticCriteria = true };
        fx.Write("stock/shelf17.txt", string.Join("\n", Enumerable.Range(1, 600).Select(i => $"line {i}")));
        var worker = new FakeChatProvider(
            Turn.Says(Plan()),
            Turn.Calls1("read_file", """{"path":"stock/shelf17.txt","offset":1,"limit":10}""", "r1"),
            Turn.Says("Counted."));
        var reviewer = new FakeChatProvider(
            Turn.Says("""{"criteria":[{"id":"C1","verdict":"pass","reason":"it shows it","calls":[1],"files":[]}]}"""),
            Turn.Says("""{"criteria":[{"id":"C1","verdict":"pass","reason":"it shows it","calls":[1],"files":[]}]}"""));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer), "Count the boxes on shelf 17 and write a report.");

        Assert.Contains("a read counts only where it covered the file whole", reviewer.Requests[^1].Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_too_long_to_show_whole_says_so_and_does_not_count_as_read()
    {
        using var fx = new EngineFixture { TypedCriteria = true, SemanticCriteria = true };
        var worker = new FakeChatProvider(
            Turn.Says(Plan()),
            Turn.Calls1("write_file", $$"""{"path":"stock/report.md","content":"{{new string('x', 20_000)}}"}""", "w1"),
            Turn.Says("Reported."));
        var cites = Turn.Says("""{"criteria":[{"id":"C1","verdict":"pass","reason":"it says so","calls":[],"files":["stock/report.md"]}]}""");
        var reviewer = new FakeChatProvider(cites, cites);

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer), "Count the boxes on shelf 17 and write a report.");

        var asked = reviewer.Requests[0].Messages[1].Content!;
        Assert.Contains("--- stock/report.md (shown in part - not whole)", asked, StringComparison.Ordinal);
        Assert.Contains("characters not shown here; the end follows", asked, StringComparison.Ordinal);
        Assert.Contains("'stock/report.md' is shown in part, not whole", reviewer.Requests[^1].Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Off_a_semantic_criterion_is_dropped_and_the_step_has_its_short_review()
    {
        var (events, _, reviewer) = await Run([Verdicts.Pass()], semantic: false);

        Assert.Contains(events, e => e.Summary.StartsWith("Planner criterion dropped - a semantic criterion is the reviewer's to judge", StringComparison.Ordinal));
        Assert.DoesNotContain("CRITERIA - judge these", string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"criteria":[{"kind":"semantic","text":"The report states the count","evidence":["file_read"]}],"steps":[{"title":"a"}]}""", "state it on that step")]
    [InlineData("""{"steps":[{"title":"a","criteria":[{"kind":"semantic","text":"ok"}]}]}""", "states nothing a result could be held to")]
    [InlineData("""{"steps":[{"title":"a","criteria":[{"kind":"semantic","text":"The report states the count","evidence":["vibes"]}]}]}""", "'vibes' is not an evidence kind")]
    public void A_semantic_criterion_it_cannot_judge_is_dropped_with_the_reason(string plan, string reason)
    {
        using var doc = JsonDocument.Parse(plan);
        var (accepted, dropped) = TypedCriteria.Validate(TypedCriteria.Read(doc.RootElement), Path.GetTempPath(), [], null, semantic: true);

        Assert.Empty(accepted);
        Assert.Contains(reason, Assert.Single(dropped), StringComparison.Ordinal);
    }
}

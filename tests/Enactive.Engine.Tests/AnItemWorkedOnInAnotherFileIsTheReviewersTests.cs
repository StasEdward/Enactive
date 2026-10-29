namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// A list consumed with file_write evidence, where the step's right work on an item changes another file: tests written
/// for a source file, a note kept on a page. The engine cannot tell that from work left undone, so it does not reject
/// it - the reviewer judges it, and without an approving verdict the step is done, not confirmed.
///
/// <para><b>Measured</b> (bench test-coverage, 20261017-231237): the planner made "the files with gaps in coverage" the
/// items of "add tests" with write evidence. The step wrote the tests into the test file, left the library alone as it
/// should, and was rejected twice by the coverage check - "Shelf/Inventory.cs (no file_write evidence)" - and the run
/// failed. The only way through the check was to change the code under test.</para>
/// </summary>
public sealed class AnItemWorkedOnInAnotherFileIsTheReviewersTests
{
    private const string Source = "src/Lib.cs";
    private const string TestFile = "tests/LibTests.cs";

    private static SuccessCriterionDefinition WriteCoverage()
        => new("Every files item is covered", "covers_all", Required: true, Origin: CriterionOrigin.Declared)
        {
            Typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "files",
                ResultsStep: 1, ResultsField: "notes", Evidence: EvidenceKind.FileWrite)
        };

    private static StepOutput Output(int stepNo, object values, IReadOnlyList<ItemEvidence>? items = null)
        => new(stepNo, $"step {stepNo}", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow, JsonSerializer.Serialize(values), [], 1, []) { Items = items };

    private static StepCoverageGate.Outcome Gate(IReadOnlyList<string>? wroteInstead)
        => StepCoverageGate.Check([WriteCoverage()], [Output(1, new { files = new[] { Source } })],
            Output(2, new { notes = new Dictionary<string, string> { [Source] = "tests added" } },
                [new ItemEvidence("notes", Source, [EvidenceKind.Call], null) { WroteInstead = wroteInstead }]), 2)!;

    [Fact]
    public void An_item_whose_step_changed_other_files_passes_the_gate_and_stands_only_on_a_review()
    {
        var gate = Gate([TestFile]);

        Assert.True(gate.Passed, gate.Notes);
        var (reason, cause) = gate.Unconfirmed(StepCoverageGate.Approval.None)!.Value;
        Assert.Equal(OutcomeCause.CoverageWrittenElsewhere, cause);
        Assert.Contains(Source, reason, StringComparison.Ordinal);
        Assert.Contains(TestFile, reason, StringComparison.Ordinal);
        Assert.Null(gate.Unconfirmed(StepCoverageGate.Approval.WorkOnly));
    }

    /// <summary>A step that changed no file has no write to show for any item: still not covered.</summary>
    [Fact]
    public void An_item_whose_step_changed_nothing_is_not_covered()
    {
        var gate = Gate(null);

        Assert.False(gate.Passed);
        Assert.Contains($"{Source} (no file_write evidence)", gate.Notes, StringComparison.Ordinal);
    }

    /// <summary>The reviewer is shown the item in full, with the files changed in its place - the question is theirs.</summary>
    [Fact]
    public void The_reviewer_is_shown_what_was_changed_in_its_place()
    {
        var block = StepCoverageGate.ReviewerBlock(Gate([TestFile]), [], 4000);

        Assert.Contains($"- {Source}: required file_write", block, StringComparison.Ordinal);
        Assert.Contains($"changed instead: {TestFile}", block, StringComparison.Ordinal);
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    private const string Plan = """
        {"disposition":"task","title":"add tests",
         "steps":[{"title":"find the files with gaps","dependsOn":[],"output":{"files":{"type":"path[]","description":"files"}}},
                  {"title":"add tests for each","dependsOn":[0],"consumes":[{"step":0,"field":"files","evidence":"file_write"}],
                   "output":{"notes":{"type":"results","description":"a note per file"}}}]}
        """;

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Reviewer)> RunAsync(params string[] verdicts)
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        fx.Write(Source, "class Lib {}");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { files = new[] { Source } }), "s1"),
            Turn.Says("Found it."),
            Turn.Calls1("read_file", JsonSerializer.Serialize(new { path = Source }), "r1"),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = TestFile, content = "class LibTests {}" }), "w1"),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = new Dictionary<string, string> { [Source] = $"tests in {TestFile}" } }), "s2"),
            Turn.Says("Added the tests."));
        var reviewer = new FakeChatProvider(verdicts.Select(v => v == "pass" ? Verdicts.Pass() : Verdicts.Fail(v)).ToArray());
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "add tests for the files with gaps");
        Assert.Equal("class Lib {}", fx.Read(Source));
        return (events, reviewer);
    }

    /// <summary>THE ONE THAT MATTERS: the run of 20261017-231237, in small - the tests go to the test file, and it stands.</summary>
    [Fact]
    public async Task Tests_written_for_a_source_file_reach_the_reviewer_and_stand_on_its_pass()
    {
        var (events, reviewer) = await RunAsync("pass", "pass");

        var end = events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == 2);
        Assert.Equal(StepOutcomeKind.Succeeded, end.StepOutcome());
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed && e.Summary.Contains("engine's coverage check", StringComparison.Ordinal));
        var review = string.Join("\n", reviewer.Requests.Last().Messages.Select(m => m.Content));
        Assert.Contains($"changed instead: {TestFile}", review, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>The reviewer has the last word: it fails the work, and the step is rejected as any failed step is.</summary>
    [Fact]
    public async Task A_reviewer_that_finds_it_is_not_the_work_rejects_the_step()
    {
        var (events, _) = await RunAsync("pass", "the tests do not exercise Lib", "the tests still do not exercise Lib");

        Assert.Equal(StepOutcomeKind.ReviewRejected,
            events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == 2).StepOutcome());
    }
}

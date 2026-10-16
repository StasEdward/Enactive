namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Phase 5.1: every item a run set out to cover is covered only by a result for THAT item backed by
/// complete evidence of the required kind - never by a list of names, and never by part of a file.
/// </summary>
public sealed class EvidenceCoversAllTests
{
    // ── the decision ─────────────────────────────────────────────────────────────────────

    private static readonly TypedCriterion Coverage = new(TypedCriterionKind.EvidenceCoversAll,
        SourceStep: 0, SourceField: "pages", ResultsStep: 1, ResultsField: "notes", Evidence: EvidenceKind.FileRead);

    private static SuccessCriterionDefinition Criterion()
        => new("Every pages item is covered", EvidenceCoverage.Describe(Coverage), 0, Origin: CriterionOrigin.Proposed) { Typed = Coverage };

    private static string[] Pages(int n) => Enumerable.Range(1, n).Select(i => $"wiki/page{i:00}.md").ToArray();

    private static StepOutput Output(int stepNo, object values, IReadOnlyList<ItemEvidence>? items = null)
        => new(stepNo, $"step {stepNo}", Guid.NewGuid(), $"step{stepNo}", 1, DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(values), [], 1, []) { Items = items };

    private static ItemEvidence Read(string item) => new("notes", item, [EvidenceKind.FileRead, EvidenceKind.Call], null);
    private static ItemEvidence NotRead(string item, string gap = "never read") => new("notes", item, [], gap);

    private static CriterionResult Decide(string[] pages, IEnumerable<string> withResult, Func<string, ItemEvidence> evidence)
        => EvidenceCoverage.Evaluate(Criterion(),
        [
            Output(1, new { pages }),
            Output(2, new { notes = withResult.ToDictionary(p => p, p => "reviewed") }, withResult.Select(evidence).ToArray())
        ]);

    /// <summary>THE ONE THAT MATTERS: the wiki case - twelve pages named, two read - is not a pass.</summary>
    [Fact]
    public void Twelve_pages_named_and_two_read_is_not_covered()
    {
        var pages = Pages(12);
        var result = Decide(pages, pages, p => pages[..2].Contains(p) ? Read(p) : NotRead(p));

        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.StartsWith("covered 2 of 12", result.Detail);
    }

    /// <summary>A result for every page with nothing behind any of them is a list of names.</summary>
    [Fact]
    public void Naming_every_item_in_the_result_without_evidence_does_not_pass()
    {
        var pages = Pages(12);
        Assert.Equal(CriterionOutcome.Failed, Decide(pages, pages, p => NotRead(p)).Outcome);
    }

    /// <summary>Part of a file is not the file: the item is not covered, and the reason says which lines were not read.</summary>
    [Fact]
    public void A_file_read_in_part_does_not_cover_it_and_the_reason_says_what_was_not_read()
    {
        var pages = Pages(1);
        var result = Decide(pages, pages, p => NotRead(p, "lines 401-518 of 518 not read"));

        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Contains("wiki/page01.md (lines 401-518 of 518 not read)", result.Detail);
    }

    /// <summary>Amendment D, the pair: 9 of 12 is "these 9, not these 3, and why" - never an empty result.</summary>
    [Fact]
    public void Nine_of_twelve_says_which_three_and_why()
    {
        var pages = Pages(12);
        var result = Decide(pages, pages[..10], p => p == pages[9] ? NotRead(p, "seen only as an excerpt") : Read(p));

        Assert.StartsWith("covered 9 of 12", result.Detail);
        Assert.Contains("wiki/page10.md (seen only as an excerpt)", result.Detail);
        Assert.Contains("wiki/page12.md (no result)", result.Detail);
    }

    [Fact]
    public void Every_item_with_a_result_backed_by_a_whole_read_is_covered_however_the_path_is_written()
    {
        var pages = Pages(3);
        var result = EvidenceCoverage.Evaluate(Criterion(),
        [
            Output(1, new { pages }),
            Output(2, new { notes = pages.ToDictionary(p => "./" + p.Replace('/', '\\'), _ => "ok") },
                pages.Select(p => Read("./" + p.Replace('/', '\\'))).ToArray())
        ]);
        Assert.Equal(CriterionOutcome.Passed, result.Outcome);
    }

    /// <summary>
    /// A pass says what it stood on: "every file read whole" only for reads; under "mentioned", that every item is
    /// accounted for - and that a mention shows neither a read nor the work (Plan Phase 1.2).
    /// </summary>
    [Fact]
    public void A_pass_says_every_file_read_only_when_reads_back_it()
    {
        var pages = Pages(2);
        Assert.Equal("covered 2 of 2: every file read whole.", Decide(pages, pages, Read).Detail);

        var mentioned = EvidenceCoverage.Evaluate(Criterion() with { Typed = Coverage with { Evidence = EvidenceKind.Call } },
        [
            Output(1, new { pages }),
            Output(2, new { notes = pages.ToDictionary(p => p, _ => "ok") }, pages.Select(p => new ItemEvidence("notes", p, [EvidenceKind.Call], null)).ToArray())
        ]);
        Assert.Equal(CriterionOutcome.Passed, mentioned.Outcome);
        Assert.StartsWith("covered 2 of 2: every item accounted for", mentioned.Detail, StringComparison.Ordinal);
        Assert.Contains("mentioned", mentioned.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("read whole", mentioned.Detail, StringComparison.Ordinal);
    }

    /// <summary>A mention is not a read and not a write: an item only named in a call covers neither.</summary>
    [Theory]
    [InlineData(EvidenceKind.FileRead, "no file_read evidence")]
    [InlineData(EvidenceKind.FileWrite, "no file_write evidence")]
    public void A_mention_satisfies_neither_a_read_nor_a_write(EvidenceKind required, string why)
    {
        var pages = Pages(1);
        var result = EvidenceCoverage.Evaluate(Criterion() with { Typed = Coverage with { Evidence = required } },
        [
            Output(1, new { pages }),
            Output(2, new { notes = pages.ToDictionary(p => p, _ => "ok") }, [new ItemEvidence("notes", pages[0], [EvidenceKind.Call], null)])
        ]);
        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Contains($"{pages[0]} ({why})", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>What backed each item is kept with the output, so a resumed run decides coverage the same way.</summary>
    [Fact]
    public void What_backed_each_item_survives_a_checkpoint()
    {
        var saved = Output(2, new { notes = new Dictionary<string, string> { ["wiki/a.md"] = "ok" } }, [Read("wiki/a.md"), NotRead("wiki/b.md")]);
        var restored = JsonSerializer.Deserialize<StepOutput>(JsonSerializer.Serialize(saved))!;

        Assert.Equal([EvidenceKind.FileRead, EvidenceKind.Call], restored.Items![0].Complete);
        Assert.Equal("never read", restored.Items[1].Gap);
    }

    [Fact]
    public void A_source_that_handed_nothing_on_covers_nothing()
        => Assert.Equal(CriterionOutcome.Failed, EvidenceCoverage.Evaluate(Criterion(), []).Outcome);

    // ── what the engine records ──────────────────────────────────────────────────────────

    private static Enactive.Core.Tools.ToolResult ReadResult(string path, int first, int last, int total)
        => new(true, "text", null, [], new Dictionary<string, object?>
        {
            ["path"] = path, ["firstLine"] = first, ["lastLine"] = last, ["totalLines"] = total
        });

    private static readonly Enactive.Core.Tools.ToolDefinition Reader = new("read_file", "read", "{}",
        Kind: Enactive.Core.Tools.ToolKind.Read, FileCoverage: Enactive.Core.Tools.FileCoverageBehavior.Read);

    private static Enactive.Core.Tools.ToolCall Call(string path) => new("c", "read_file", $$"""{"path":"{{path}}"}""");

    [Fact]
    public void Windows_that_join_up_are_a_whole_read_and_a_gap_is_named()
    {
        var reads = new ReadLedger();
        reads.Saw(Call("a.md"), ReadResult("a.md", 401, 518, 518), Reader);
        Assert.Equal((false, "lines 1-400 of 518 not read"), reads.SeenWhole("a.md"));

        reads.Saw(Call("a.md"), ReadResult("a.md", 1, 400, 518), Reader);
        Assert.True(reads.SeenWhole("./a.md").Whole);

        // A trim takes the text out of the conversation; it does not undo the reading.
        reads.ForgetDiscardedReads();
        Assert.True(reads.SeenWhole("a.md").Whole);
        Assert.Equal((false, "never read"), reads.SeenWhole("b.md"));
    }

    // ── write evidence ───────────────────────────────────────────────────────────────────

    private static readonly StepOutputSchema NotesSchema = new("s", 1, [new StepOutputField("notes", StepOutputFieldType.Results, "notes")]);

    private static Enactive.Core.Execution.ExecutedAction Did(string tool, string arguments, string[]? changed = null, int? step = 2,
        Enactive.Core.Execution.ActionOutcome outcome = Enactive.Core.Execution.ActionOutcome.Succeeded)
        => new(DateTimeOffset.UtcNow, step, tool, arguments, outcome, "ok",
            changed is null ? Enactive.Core.Tools.WorkspaceEffect.None : Enactive.Core.Tools.WorkspaceEffect.Changed, changed);

    private static IReadOnlyList<EvidenceKind> Found(IReadOnlyList<ItemEvidence> items, string item)
        => items.Single(i => i.Item == item).Complete;

    /// <summary>
    /// A write, an edit, a deletion and a rename - from it or to it - are this step's recorded changes to the item's path,
    /// and each is write evidence. A path written the other way round is the same item.
    /// </summary>
    [Fact]
    public void A_recorded_change_to_the_items_path_by_this_step_is_write_evidence()
    {
        string[] items = ["wiki/a.md", "wiki/b.md", "wiki/c.md", "wiki/d.md", "wiki/e.md"];
        var values = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { notes = items.ToDictionary(i => i, _ => "fixed") }))!.AsObject();
        var gathered = EvidenceCoverage.Gather(NotesSchema, values, new ReadLedger(),
        [
            Did("write_file", """{"path":"wiki/a.md","content":"x"}""", ["wiki/a.md"]),
            Did("edit_file", """{"path":"wiki\\b.md"}""", ["wiki\\b.md"]),
            Did("delete_file", """{"path":"./wiki/c.md"}""", ["./wiki/c.md"]),
            Did("move_file", """{"from":"wiki/d.md","to":"old/d.md"}""", ["wiki/d.md", "old/d.md"]),
            Did("move_file", """{"from":"draft/e.md","to":"wiki/e.md"}""", ["draft/e.md", "wiki/e.md"])
        ], [], step: 2);

        Assert.All(items, i => Assert.Contains(EvidenceKind.FileWrite, Found(gathered, i)));
        // A write names the item as surely as its arguments: it is a mention too.
        Assert.All(items, i => Assert.Contains(EvidenceKind.Call, Found(gathered, i)));
    }

    /// <summary>A recorded write is a mention even where nothing readable is left of its arguments (an older record).</summary>
    [Fact]
    public void A_write_is_a_mention_even_when_its_recorded_arguments_name_nothing()
    {
        var values = System.Text.Json.Nodes.JsonNode.Parse("""{"notes":{"wiki/a.md":"fixed"}}""")!.AsObject();
        var gathered = EvidenceCoverage.Gather(NotesSchema, values, new ReadLedger(),
            [Did("write_file", """{"path":"wiki/a.md","content":"# a very long fi…""", ["wiki/a.md"])], [], step: 2);

        Assert.Equal([EvidenceKind.Call, EvidenceKind.FileWrite], Found(gathered, "wiki/a.md"));
    }

    /// <summary>
    /// A mention is read from what the call's FULL arguments named, kept with the record - not from the arguments as
    /// recorded for reading, which are cut at 120 characters and no longer parse.
    /// </summary>
    [Fact]
    public void A_mention_is_read_from_the_full_arguments_not_the_cut_record()
    {
        var pages = Pages(12);
        var full = JsonSerializer.Serialize(new { paths = pages });
        var values = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { notes = pages.ToDictionary(i => i, _ => "seen") }))!.AsObject();
        var cut = Did("list_dir", full[..120] + "…") with { Named = EvidenceCoverage.NamesIn(full) };

        var gathered = EvidenceCoverage.Gather(NotesSchema, values, new ReadLedger(), [cut], [], step: 2);

        Assert.All(pages, p => Assert.Equal([EvidenceKind.Call], Found(gathered, p)));
        // What is kept is short: a file's content names no item.
        Assert.Empty(EvidenceCoverage.NamesIn(JsonSerializer.Serialize(new { content = new string('x', 2000) })));
    }

    /// <summary>A mention, a whole read, a refused or failed write, or another step's write is no write by this step.</summary>
    [Fact]
    public void A_mention_a_read_a_write_that_did_not_succeed_or_another_steps_write_is_not_write_evidence()
    {
        string[] items = ["wiki/named.md", "wiki/read.md", "wiki/refused.md", "wiki/failed.md", "wiki/earlier.md"];
        var values = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { notes = items.ToDictionary(i => i, _ => "fixed") }))!.AsObject();
        var reads = new ReadLedger();
        reads.Saw(Call("wiki/read.md"), ReadResult("wiki/read.md", 1, 3, 3), Reader);
        var gathered = EvidenceCoverage.Gather(NotesSchema, values, reads,
        [
            Did("list_dir", """{"paths":["wiki/named.md"]}"""),
            Did("read_file", """{"path":"wiki/read.md"}"""),
            Did("write_file", """{"path":"wiki/refused.md"}""", ["wiki/refused.md"], outcome: Enactive.Core.Execution.ActionOutcome.Refused),
            Did("write_file", """{"path":"wiki/failed.md"}""", ["wiki/failed.md"], outcome: Enactive.Core.Execution.ActionOutcome.Failed),
            Did("write_file", """{"path":"wiki/earlier.md"}""", ["wiki/earlier.md"], step: 1)
        ], [], step: 2);

        Assert.All(items, i => Assert.DoesNotContain(EvidenceKind.FileWrite, Found(gathered, i)));
        Assert.Equal([EvidenceKind.Call], Found(gathered, "wiki/named.md"));
    }

    // ── what the planner may state ───────────────────────────────────────────────────────

    private static Plan TwoStepPlan(bool dependent = true, string listType = "path[]")
    {
        using var first = JsonDocument.Parse("{\"pages\":{\"type\":\"" + listType + "\"}}");
        using var second = JsonDocument.Parse("""{"notes":{"type":"results"}}""");
        var plan = DagPlan.FromSpecs(
        [
            new PlanStepSpec("find", [], DependenciesDeclared: true) { Output = Planner.ParseOutput(first.RootElement, 1) },
            new PlanStepSpec("review", dependent ? [0] : [], DependenciesDeclared: true) { Output = Planner.ParseOutput(second.RootElement, 2) }
        ]);
        return plan;
    }

    private static (IReadOnlyList<SuccessCriterionDefinition> Accepted, IReadOnlyList<string> Dropped) Validate(string criteria, Plan? plan)
    {
        using var doc = JsonDocument.Parse($$"""{"criteria":[{{criteria}}]}""");
        return TypedCriteria.Validate(TypedCriteria.Read(doc.RootElement), Path.GetTempPath(), [], plan);
    }

    private const string Stated = """
        {"kind":"covers_all","source":{"step":0,"field":"pages"},"results":{"step":1,"field":"notes"},"evidence":"file_read"}
        """;

    [Fact]
    public void A_coverage_criterion_over_declared_outputs_is_accepted()
    {
        var (accepted, dropped) = Validate(Stated, TwoStepPlan());
        Assert.Empty(dropped);
        Assert.True(Assert.Single(accepted).Typed!.FromRun);
    }

    [Theory]
    [InlineData(false, "path[]", "does not depend on step 0")]
    [InlineData(true, "text", "declares no list output 'pages'")]
    [InlineData(true, "string[]", "a file read needs items that are paths")]
    public void A_coverage_criterion_the_plan_cannot_back_is_dropped_with_the_reason(bool dependent, string listType, string why)
        => Assert.Contains(why, Assert.Single(Validate(Stated, TwoStepPlan(dependent, listType)).Dropped));

    [Fact]
    public void An_evidence_kind_the_engine_does_not_know_is_dropped()
        => Assert.Contains("'vibes' is not an evidence kind",
            Assert.Single(Validate(Stated.Replace("file_read", "vibes"), TwoStepPlan()).Dropped));

    [Fact]
    public void The_planner_is_told_about_coverage_only_when_it_can_use_it()
    {
        Assert.DoesNotContain("covers_all", Planner.SystemPromptFor(null, typedCriteria: true), StringComparison.Ordinal);
        Assert.DoesNotContain("covers_all", Planner.SystemPromptFor(null, stepOutputs: true), StringComparison.Ordinal);
        Assert.Contains("covers_all", Planner.SystemPromptFor(null, stepOutputs: true, typedCriteria: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_what_is_covered_cannot_be_shown_to_cover_as_much()
    {
        var other = Criterion() with { Typed = Coverage with { Evidence = EvidenceKind.Call } };
        Assert.Equal(CriterionStrength.Same, ContractMonotonicity.Compare(Criterion(), Criterion()).Strength);
        Assert.Equal(CriterionStrength.Incomparable, ContractMonotonicity.Compare(Criterion(), other).Strength);
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    private const string FindThenReview = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"output":{"notes":{"type":"results","description":"a note per page"}}}],
         "criteria":[{"kind":"covers_all","source":{"step":0,"field":"pages"},"results":{"step":1,"field":"notes"},"evidence":"file_read"}]}
        """;

    private static EngineFixture Wiki()
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        fx.Write("wiki/a.md", "# A\nabout a\n");
        fx.Write("wiki/b.md", "# B\nabout b\n");
        return fx;
    }

    /// <summary>
    /// A run that read one page of two and wrote a note for both: the step is told on the spot which
    /// item nothing backs, and the run is not called finished.
    /// </summary>
    [Fact]
    public async Task A_run_that_read_one_of_two_pages_is_not_finished()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(FindThenReview),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s1"), Turn.Says("Found two."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "r1"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"fine","wiki/b.md":"fine too"}}""", "s2"),
            Turn.Says("Reviewed both."));

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        // Decided at the step's own gate (Plan Phase 1.3), and not first at the run's end: the step is rejected on it.
        var step = events.Last(e => e.Kind == EventKind.StepCompleted);
        Assert.Equal(StepOutcomeKind.ReviewRejected, step.StepOutcome());
        Assert.Equal("covered 1 of 2; not covered: wiki/b.md (never read).", step.OutcomeReason());
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult
                                     && e.Summary.Contains("not counted as covered: wiki/b.md (never read)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_read_every_page_it_reviewed_is_covered()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(FindThenReview),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s1"), Turn.Says("Found two."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "r1"),
            Turn.Calls1("read_file", """{"path":"wiki/b.md"}""", "r2"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"fine","wiki/b.md":"fine too"}}""", "s2"),
            Turn.Says("Reviewed both."));

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("Every pages item is covered", StringComparison.Ordinal));
        var recorded = events.Last(e => e.Kind == EventKind.StepOutputRecorded);
        Assert.Contains("\"complete\":[\"FileRead\"", recorded.PayloadJson!, StringComparison.OrdinalIgnoreCase);
    }
}

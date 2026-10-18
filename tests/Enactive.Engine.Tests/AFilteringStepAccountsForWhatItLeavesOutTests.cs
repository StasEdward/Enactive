namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 1.2, a step that filters (a subset consume): it may leave items of the list it consumes out, each with a
/// reason, instead of processing every one. The engine checks the partition deterministically - processed and excluded
/// are disjoint, name nothing foreign, and together are the list; every exclusion has a reason; every processed item
/// has its evidence - and a reviewer judges whether the request permits each exclusion. A reason alone never authorises
/// an omission: without an approving verdict the step is done, not confirmed.
/// </summary>
public sealed class AFilteringStepAccountsForWhatItLeavesOutTests
{
    private static string[] Pages(int n) => Enumerable.Range(1, n).Select(i => $"wiki/p{i:00}.md").ToArray();

    private const string Filtering = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","subset":true}],
                   "output":{"notes":{"type":"results","description":"a note per page"},
                             "excluded":{"type":"results","description":"why a page is left out"}}}]}
        """;

    // A filtering step with nowhere to say what it left out.
    private const string NoExcludedField = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","subset":true}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    private static EngineFixture Wiki(int pages)
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        foreach (var page in Pages(pages))
            fx.Write(page, $"# {page}\nabout it\n");
        return fx;
    }

    private static Turn[] Found(string[] pages)
        => [Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { pages }), "s1"), Turn.Says("Found them.")];

    private static Turn Read(string page) => Turn.Calls1("read_file", JsonSerializer.Serialize(new { path = page }), "r-" + page);

    /// <summary>Reads and notes the first two pages, and leaves the third out as a stub.</summary>
    private static Turn[] ReviewTwoLeaveOne(string[] pages, string id = "s2")
        => [Read(pages[0]), Read(pages[1]),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new
            {
                notes = pages[..2].ToDictionary(p => p, p => $"{p} is fine"),
                excluded = new Dictionary<string, string> { [pages[2]] = "a stub with nothing to review" }
            }), id),
            Turn.Says("Reviewed two, left the stub out.")];

    private static WorkEvent StepEnd(List<WorkEvent> events, int step)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == step);

    private static ChatRequest[] ReviewsOf(FakeChatProvider reviewer, int step)
        => reviewer.Requests.Where(r => r.Messages.Any(m => m.Content?.Contains($"THIS STEP ({step}):", StringComparison.Ordinal) == true)).ToArray();

    // ── the criterion carries the subset ────────────────────────────────────────────────

    private static Plan PlanOf(params PlanStepSpec[] specs) => DagPlan.FromSpecs(specs);

    private static StepOutputSchema Out(params (string Name, StepOutputFieldType Type)[] fields)
        => new("s", 1, fields.Select(f => new StepOutputField(f.Name, f.Type, f.Name)).ToArray());

    private static readonly StepOutputSchema PagesOut = Out(("pages", StepOutputFieldType.PathList));

    private static Plan FilteringPlan(StepOutputSchema consumer)
        => PlanOf(new PlanStepSpec("find pages", []) { Output = PagesOut },
                  new PlanStepSpec("review pages", [0]) { Output = consumer, Consumes = [new ConsumeSpec(0, "pages") { Subset = true }] });

    /// <summary>
    /// The criterion derived from a subset consume says it is one, and is over the processed results - not over the
    /// field that says what was left out, wherever the two are declared.
    /// </summary>
    [Fact]
    public void A_subset_consume_derives_a_subset_criterion_over_the_processed_results()
    {
        var derived = UpstreamCoverage.Derive(FilteringPlan(Out(("excluded", StepOutputFieldType.Results), ("notes", StepOutputFieldType.Results))));

        Assert.Empty(derived.Defects);
        var typed = Assert.Single(derived.Criteria).Criterion.Typed!;
        Assert.True(typed.Subset);
        Assert.Equal("notes", typed.ResultsField);
    }

    /// <summary>A filtering step with no 'excluded' results field has nowhere to give a reason: a contract defect.</summary>
    [Fact]
    public void A_subset_consume_without_an_excluded_field_is_a_defect()
    {
        var derived = UpstreamCoverage.Derive(FilteringPlan(Out(("notes", StepOutputFieldType.Results))));

        Assert.Empty(derived.Criteria);
        Assert.Contains("'excluded'", Assert.Single(derived.Defects), StringComparison.Ordinal);
    }

    /// <summary>
    /// De-duplication is by the whole obligation: a written criterion that is not a subset does not stand in for a
    /// derived one that is, and one that is the same subset criterion does.
    /// </summary>
    [Fact]
    public void De_duplication_tells_a_subset_criterion_from_one_that_is_not()
    {
        var plan = FilteringPlan(Out(("notes", StepOutputFieldType.Results), ("excluded", StepOutputFieldType.Results)));
        var derived = UpstreamCoverage.Derive(plan).Criteria;
        SuccessCriterionDefinition Written(bool subset) => new("Every pages item is covered", "covers_all", Origin: CriterionOrigin.Proposed)
        {
            Typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
                ResultsStep: 1, ResultsField: "notes", Evidence: EvidenceKind.Call, Subset: subset)
        };

        Assert.Single(UpstreamCoverage.NotAlreadyChecked(derived, [Written(subset: false)]));
        Assert.Empty(UpstreamCoverage.NotAlreadyChecked(derived, [Written(subset: true)]));
    }

    private static SuccessCriterionDefinition SubsetCriterion(EvidenceKind evidence = EvidenceKind.Call)
        => new("Every pages item is covered", "covers_all", Required: true, Origin: CriterionOrigin.Declared)
        {
            Typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
                ResultsStep: 1, ResultsField: "notes", Evidence: evidence, Subset: true)
        };

    /// <summary>A criterion that now lets items be left out is not the one it replaced: not shown to cover as much.</summary>
    [Fact]
    public void Turning_a_coverage_criterion_into_a_subset_cannot_be_shown_to_cover_as_much()
    {
        var every = SubsetCriterion() with { Typed = SubsetCriterion().Typed! with { Subset = false } };

        Assert.Equal(CriterionStrength.Same, ContractMonotonicity.Compare(SubsetCriterion(), SubsetCriterion()).Strength);
        Assert.Equal(CriterionStrength.Incomparable, ContractMonotonicity.Compare(every, SubsetCriterion()).Strength);
    }

    /// <summary>Kept in the checkpoint and read back: a resumed run is held to the same partition.</summary>
    [Fact]
    public async Task A_subset_criterion_survives_a_checkpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonCheckpointStore(WorkspaceInfo.For(root));
            var written = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, "review the wiki", "a plan",
                "developer", null,
                [new CheckpointStep(Guid.NewGuid(), "find pages", [], nameof(StepComplexity.Normal), nameof(StepStatus.Pending))],
                [], [], [], 0, 0) { Checks = [SubsetCriterion()] };

            await store.SaveAsync(written, CancellationToken.None);
            var back = await store.LoadAsync(written.RunId, CancellationToken.None);

            Assert.True(Assert.Single(back!.Checks!).Typed!.Subset);
            Assert.Equal(SubsetCriterion().Typed, back.Checks![0].Typed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>Kept in the run record's specification and read back.</summary>
    [Fact]
    public void A_subset_criterion_survives_the_run_records_specification()
    {
        var spec = new ResolvedTaskSpec("t", 1, "T", Guid.NewGuid(), "project", Path.GetTempPath(), "review the wiki",
            new Dictionary<string, string>(), new PermissionPolicy(PermissionLevel.Observe, [], []), [SubsetCriterion()], ExecutionLimits.None, null, false);

        var back = ResolvedTaskSpec.Parse(spec.Snapshot())!;

        Assert.True(Assert.Single(back.SuccessCriteria).Typed!.Subset);
    }

    /// <summary>A covers_all as a build before the subset stored it: the same criterion, without the property.</summary>
    private static SuccessCriterionDefinition EveryItem()
        => SubsetCriterion(EvidenceKind.FileRead) with { Typed = SubsetCriterion(EvidenceKind.FileRead).Typed! with { Subset = false } };

    /// <summary>Takes the property out of stored JSON, as a build from before it would have written it.</summary>
    private static string WithoutSubset(string json)
    {
        var without = System.Text.RegularExpressions.Regex.Replace(json, @",\s*""Subset""\s*:\s*false", "");
        Assert.DoesNotContain("Subset", without, StringComparison.Ordinal);
        return without;
    }

    /// <summary>
    /// A checkpoint written before there was a subset - its criterion has no such property - is read back by the store
    /// as what it was: every item needs a result.
    /// </summary>
    [Fact]
    public async Task An_old_checkpoint_criterion_without_the_field_loads_as_not_a_subset()
    {
        var root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonCheckpointStore(WorkspaceInfo.For(root));
            var written = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, "review the wiki", "a plan",
                "developer", null,
                [new CheckpointStep(Guid.NewGuid(), "find pages", [], nameof(StepComplexity.Normal), nameof(StepStatus.Pending))],
                [], [], [], 0, 0) { Checks = [EveryItem()] };
            await store.SaveAsync(written, CancellationToken.None);
            var file = Path.Combine(root, ".enactive", "checkpoints", $"{written.RunId:N}.json");
            await File.WriteAllTextAsync(file, WithoutSubset(await File.ReadAllTextAsync(file)));

            var back = await new JsonCheckpointStore(WorkspaceInfo.For(root)).LoadAsync(written.RunId, CancellationToken.None);

            var typed = Assert.Single(back!.Checks!).Typed!;
            Assert.Equal(EvidenceKind.FileRead, typed.Evidence);
            Assert.False(typed.Subset);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>The same for a run record's specification, read back as the history reads it.</summary>
    [Fact]
    public void An_old_run_record_criterion_without_the_field_loads_as_not_a_subset()
    {
        var spec = new ResolvedTaskSpec("t", 1, "T", Guid.NewGuid(), "project", Path.GetTempPath(), "review the wiki",
            new Dictionary<string, string>(), new PermissionPolicy(PermissionLevel.Observe, [], []), [EveryItem()], ExecutionLimits.None, null, false);

        var back = ResolvedTaskSpec.Parse(WithoutSubset(spec.Snapshot()))!;

        var typed = Assert.Single(back.SuccessCriteria).Typed!;
        Assert.Equal(EvidenceKind.FileRead, typed.Evidence);
        Assert.False(typed.Subset);
    }

    // ── the partition, decided by the engine ───────────────────────────────────────────

    private static StepOutput Output(int stepNo, object values, IReadOnlyList<ItemEvidence>? items = null)
        => new(stepNo, $"step {stepNo}", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow, JsonSerializer.Serialize(values), [], 1, []) { Items = items };

    /// <summary>The consumer's candidate: notes for some pages, each backed by a read, and reasons for others.</summary>
    private static StepOutput Partition(Dictionary<string, string> notes, Dictionary<string, string?> excluded)
        => Output(2, new { notes, excluded }, notes.Keys.Select(p => new ItemEvidence("notes", p, [EvidenceKind.FileRead, EvidenceKind.Call], null)).ToArray());

    private static StepCoverageGate.Outcome Gate(StepOutput candidate, EvidenceKind evidence = EvidenceKind.Call)
        => StepCoverageGate.Check([SubsetCriterion(evidence)], [Output(1, new { pages = Pages(4) })], candidate, 2)!;

    private static Dictionary<string, string> Notes(params string[] pages) => pages.ToDictionary(p => p, p => $"{p} is fine");

    [Fact]
    public void A_partition_of_the_list_with_reasons_passes()
    {
        var p = Pages(4);
        var gate = Gate(Partition(Notes(p[0], p[1]), new() { [p[2]] = "a stub", [p[3]] = "a redirect" }));

        Assert.True(gate.Passed, gate.Notes);
        Assert.Equal([(p[2], "a stub"), (p[3], "a redirect")], gate.Exclusions);
        Assert.Empty(gate.OnlyMentioned);
        var result = EvidenceCoverage.Evaluate(SubsetCriterion(), [Output(1, new { pages = p }), Partition(Notes(p[0], p[1]), new() { [p[2]] = "a stub", [p[3]] = "a redirect" })]);
        Assert.Equal(CriterionOutcome.Passed, result.Outcome);
    }

    [Fact]
    public void An_item_both_processed_and_excluded_fails_as_an_overlap()
    {
        var p = Pages(4);
        var gate = Gate(Partition(Notes(p[0], p[1], p[2]), new() { [p[2]] = "a stub", [p[3]] = "a redirect" }));

        Assert.False(gate.Passed);
        Assert.Equal($"covered 3 of 4; both processed ('notes') and excluded: {p[2]}.", gate.Notes);
    }

    [Fact]
    public void An_item_neither_processed_nor_excluded_fails_as_a_gap()
    {
        var p = Pages(4);
        var gate = Gate(Partition(Notes(p[0], p[1]), new() { [p[2]] = "a stub" }));

        Assert.False(gate.Passed);
        Assert.Equal($"covered 3 of 4; neither processed nor excluded: {p[3]}.", gate.Notes);
        Assert.Contains($"- {p[3]}: a result in 'notes', and mentioned", gate.RepairAdvice, StringComparison.Ordinal);
        Assert.Contains("or an entry in 'excluded' giving the reason it is left out", gate.RepairAdvice, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_not_in_the_list_fails_as_foreign()
    {
        var p = Pages(4);
        var gate = Gate(Partition(Notes(p[0], p[1]), new() { [p[2]] = "a stub", [p[3]] = "a redirect", ["wiki/elsewhere.md"] = "not mine" }));

        Assert.False(gate.Passed);
        Assert.Equal("covered 4 of 4; not in step 1's list 'pages': wiki/elsewhere.md.", gate.Notes);
        Assert.Contains("- wiki/elsewhere.md: not an item of the list - take it out", gate.RepairAdvice, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exclusion_without_a_reason_fails()
    {
        var p = Pages(4);
        var gate = Gate(Partition(Notes(p[0], p[1]), new() { [p[2]] = "  ", [p[3]] = null }));

        Assert.False(gate.Passed);
        Assert.Equal($"covered 2 of 4; excluded without a reason: {p[2]}, {p[3]}.", gate.Notes);
    }

    [Fact]
    public void A_processed_item_without_its_evidence_fails()
    {
        var p = Pages(4);
        var candidate = Output(2, new { notes = Notes(p[0], p[1]), excluded = new Dictionary<string, string> { [p[2]] = "a stub", [p[3]] = "a redirect" } },
            [new ItemEvidence("notes", p[0], [EvidenceKind.FileRead, EvidenceKind.Call], null), new ItemEvidence("notes", p[1], [EvidenceKind.Call], null)]);

        var gate = Gate(candidate, EvidenceKind.FileRead);

        Assert.False(gate.Passed);
        Assert.Equal($"covered 3 of 4; processed without file_read evidence: {p[1]}.", gate.Notes);
    }

    /// <summary>Several things wrong are each said, in their own words - the run-end check says the same as the gate.</summary>
    [Fact]
    public void Each_fault_is_said_apart_and_the_run_end_check_agrees()
    {
        var p = Pages(4);
        var candidate = Partition(Notes(p[0], p[1]), new() { [p[1]] = "dup", [p[2]] = "", ["wiki/x.md"] = "foreign" });

        var gate = Gate(candidate);
        var result = EvidenceCoverage.Evaluate(SubsetCriterion(), [Output(1, new { pages = p }), candidate]);

        Assert.Equal($"covered 1 of 4; both processed ('notes') and excluded: {p[1]}; not in step 1's list 'pages': wiki/x.md; "
                     + $"neither processed nor excluded: {p[3]}; excluded without a reason: {p[2]}.", gate.Notes);
        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Equal(gate.Notes, result.Detail);
    }

    /// <summary>
    /// A reason is text. A value that is not - false, 0, an object - says nothing about why the item was left out, and is
    /// no reason (the output contract refuses one at submission; an output that did not come through it is held to the
    /// same).
    /// </summary>
    [Fact]
    public void A_reason_that_is_not_text_is_no_reason()
    {
        var p = Pages(4);
        var candidate = Output(2, new { notes = Notes(p[0]), excluded = new Dictionary<string, object> { [p[1]] = false, [p[2]] = 0, [p[3]] = new { } } },
            [new ItemEvidence("notes", p[0], [EvidenceKind.FileRead, EvidenceKind.Call], null)]);

        var gate = Gate(candidate);

        Assert.False(gate.Passed);
        Assert.Equal($"covered 1 of 4; excluded without a reason: {p[1]}, {p[2]}, {p[3]}.", gate.Notes);
    }

    /// <summary>
    /// What the steps after are told names what is wrong even when every item of the list is accounted for: an item
    /// that is not on the list at all - not an empty "not covered:".
    /// </summary>
    [Fact]
    public void The_hand_off_names_items_not_on_the_list()
    {
        var p = Pages(4);
        var gate = Gate(Partition(Notes(p[0], p[1]), new() { [p[2]] = "a stub", [p[3]] = "a redirect", ["wiki/elsewhere.md"] = "not mine" }));

        var said = gate.HandOff("'review pages'");

        Assert.Contains("'review pages' hands on items that are not on step 1's list 'pages', which cover nothing: wiki/elsewhere.md.", said,
            StringComparison.Ordinal);
        Assert.DoesNotContain("leaves items not covered", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only what the partition needs is optional - the processed results field and 'excluded' - so a filtering step can
    /// leave nothing out, or everything; any other results field it declared is as required as it was.
    /// </summary>
    [Fact]
    public void Only_the_processed_results_and_the_excluded_field_of_a_filtering_step_are_optional()
    {
        var output = Out(("summary", StepOutputFieldType.Text), ("notes", StepOutputFieldType.Results),
            ("excluded", StepOutputFieldType.Results), ("links", StepOutputFieldType.Results));

        var partitioned = Planner.Partitioned(output, [new ConsumeSpec(0, "pages") { Subset = true }])!;

        Assert.Equal(["summary", "links"], partitioned.Fields.Where(f => f.Required).Select(f => f.Name));
        Assert.Same(output, Planner.Partitioned(output, [new ConsumeSpec(0, "pages")]));
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: two pages reviewed, the stub left out with a reason - the engine's partition holds, the
    /// reviewer is shown the exclusion and asked whether the request permits it, and approves: the step is done.
    /// </summary>
    [Fact]
    public async Task A_partition_with_reasons_stands_as_done_on_an_approving_review()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(Filtering), .. Found(pages), .. ReviewTwoLeaveOne(pages)]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review the wiki pages worth reviewing");

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        var asked = string.Join("\n", Assert.Single(ReviewsOf(reviewer, 2)).Messages.Select(m => m.Content));
        Assert.Contains(StepCoverageGate.ExclusionsHeading, asked, StringComparison.Ordinal);
        Assert.Contains($"- {pages[2]}: excluded - a stub with nothing to review", asked, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>No reviewer to approve the exclusions: done, not confirmed, the exclusions named - never Succeeded on a reason.</summary>
    [Fact]
    public async Task Valid_exclusions_with_review_off_are_done_but_not_confirmed()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(Filtering), .. Found(pages), .. ReviewTwoLeaveOne(pages)]);

        var events = await fx.RunAsync(fx.Build(worker), "review the wiki pages worth reviewing");

        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains($"{pages[2]} (a stub with nothing to review)", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("no review approved", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        // The step was not told that the page it left out has nothing behind it: an exclusion needs no evidence.
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains("not counted as covered", StringComparison.Ordinal));
    }

    /// <summary>A review with no usable verdict approves nothing either.</summary>
    [Fact]
    public async Task Valid_exclusions_without_a_usable_verdict_are_done_but_not_confirmed()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(Filtering), .. Found(pages), .. ReviewTwoLeaveOne(pages)]);
        var reviewer = new FakeChatProvider(Verdicts.Pass()) { WhenExhausted = Turn.Says("no idea") };

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review the wiki pages worth reviewing");

        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains($"{pages[2]} (a stub with nothing to review)", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A budget too small for the item account leaves it out of the review (CoverageAtTheStepGate): the reviewer is
    /// never asked about the exclusions, so its pass does not approve them - done, not confirmed, the exclusions named.
    /// </summary>
    [Fact]
    public async Task A_pass_from_a_review_never_shown_the_exclusions_does_not_approve_them()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(Filtering), .. Found(pages), .. ReviewTwoLeaveOne(pages)]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer, evidenceBudget: 1500),
            "review the wiki pages worth reviewing");

        var asked = string.Join("\n", Assert.Single(ReviewsOf(reviewer, 2)).Messages.Select(m => m.Content));
        Assert.DoesNotContain(StepCoverageGate.ExclusionsHeading, asked, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed && e.StepNo() == 2);
        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains($"{pages[2]} (a stub with nothing to review)", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("the review that passed it was not shown them", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    private const string FilteringJudged = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","subset":true}],
                   "output":{"notes":{"type":"results","description":"a note per page"},
                             "excluded":{"type":"results","description":"why a page is left out"}},
                   "criteria":[{"kind":"semantic","text":"Each note says what its page is about","evidence":["file_read"]}]}]}
        """;

    /// <summary>
    /// A step with semantic criteria is judged on those and nothing else - per criterion, no question about what it
    /// left out - so its pass does not approve the exclusions either.
    /// </summary>
    [Fact]
    public async Task A_pass_on_semantic_criteria_alone_does_not_approve_the_exclusions()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        fx.SemanticCriteria = true;
        var worker = new FakeChatProvider([Turn.Says(FilteringJudged), .. Found(pages), .. ReviewTwoLeaveOne(pages)]);
        // The criterion, passed on the first whole read the review was shown.
        var judged = Turn.Says("") with
        {
            Adapt = r => System.Text.RegularExpressions.Regex.Match(string.Join("\n", r.Messages.Select(m => m.Content)), @"\[(\d+)\][^\n]*?-> read_file") is { Success: true } m
                ? $$"""{"criteria":[{"id":"C1","verdict":"pass","reason":"each note matches its page","calls":[{{m.Groups[1].Value}}],"files":[]}]}"""
                : null
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass(), judged);

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review the wiki pages worth reviewing");

        Assert.Contains(events, e => e.Summary.Contains("PASS (Criteria review)", StringComparison.Ordinal));
        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains($"{pages[2]} (a stub with nothing to review)", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("judged on its semantic criteria alone", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A reviewer that finds an exclusion the request does not permit fails the step: tried again, then rejected.</summary>
    [Fact]
    public async Task An_exclusion_the_reviewer_does_not_permit_takes_the_rejected_path()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(Filtering), .. Found(pages), .. ReviewTwoLeaveOne(pages), .. ReviewTwoLeaveOne(pages, "s3")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Fail("the request asks for every page"), Verdicts.Fail("the request asks for every page"));

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review every wiki page");

        var end = StepEnd(events, 2);
        Assert.Equal(2, ReviewsOf(reviewer, 2).Length);
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.StartsWith("review not passed:", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A filtering step that leaves nothing out has nothing to put in 'excluded': it hands its notes on without it, and
    /// is done - no exclusion, nothing for a reviewer to permit. A required 'excluded' could not be sent at all (an empty
    /// results field is refused), and the step would be sent back for it.
    /// </summary>
    [Fact]
    public async Task A_filtering_step_that_leaves_nothing_out_hands_on_without_the_excluded_field()
    {
        var pages = Pages(2);
        using var fx = Wiki(2);
        var worker = new FakeChatProvider([Turn.Says(Filtering), .. Found(pages), Read(pages[0]), Read(pages[1]),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = pages.ToDictionary(p => p, p => "fine") }), "s2"),
            Turn.Says("Reviewed both.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review the wiki pages worth reviewing");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains("Missing: excluded", StringComparison.Ordinal));
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>The planner is told, in the paragraph on consumes, what a subset costs.</summary>
    [Fact]
    public void The_planner_is_told_a_subset_step_declares_its_excluded_reasons()
        => Assert.Contains("A subset step also declares an \"excluded\" results output giving the reason for each item it leaves out.",
            Planner.StepOutputsPrompt, StringComparison.Ordinal);

    /// <summary>
    /// A filtering step with no 'excluded' field: the plan goes back to the planner once with the diagnosis, and what it
    /// still leaves unresolved is an obligation that keeps the run from Completed.
    /// </summary>
    [Fact]
    public async Task A_subset_consume_without_an_excluded_field_is_replanned_and_left_unresolved_is_an_obligation()
    {
        var pages = Pages(2);
        using var fx = Wiki(2);
        fx.Decisions.Answer = "go_on";
        var worker = new FakeChatProvider([Turn.Says(NoExcludedField), Turn.Says(NoExcludedField), .. Found(pages), Read(pages[0]), Read(pages[1]),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = pages.ToDictionary(p => p, p => "fine") }), "s2"),
            Turn.Says("Reviewed both.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review the wiki pages worth reviewing");

        var replan = Assert.Single(worker.Requests, r => r.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content?
            .Contains("The plan does not say how some steps account for the lists", StringComparison.Ordinal) == true);
        Assert.Contains("'excluded'", replan.Messages.Last().Content!, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("the plan does not cover a list", events.Last().Summary, StringComparison.Ordinal);
        Assert.Contains("'excluded'", events.Last().Summary, StringComparison.Ordinal);
    }
}

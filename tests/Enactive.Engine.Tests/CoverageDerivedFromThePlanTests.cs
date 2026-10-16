namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Plan Phase 1.2: a list a step consumes is a list it must cover, and the check that it did is derived from the plan's
/// structure - not left to the planner to remember to write. Pure: a plan in, criteria and what cannot be derived out.
/// </summary>
public sealed class CoverageDerivedFromThePlanTests
{
    private static StepOutputSchema Out(params (string Name, StepOutputFieldType Type)[] fields)
        => new("s", 1, fields.Select(f => new StepOutputField(f.Name, f.Type, f.Name)).ToArray());

    private static readonly StepOutputSchema Pages = Out(("pages", StepOutputFieldType.PathList));
    private static readonly StepOutputSchema Notes = Out(("notes", StepOutputFieldType.Results));

    private static Plan PlanOf(params PlanStepSpec[] specs) => DagPlan.FromSpecs(specs);

    [Fact]
    public void A_consume_by_a_step_with_a_results_output_derives_a_blocking_coverage_criterion()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("review pages", [0]) { Output = Notes, Consumes = [new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileRead }] });

        var result = UpstreamCoverage.Derive(plan);

        Assert.Empty(result.Defects);
        var derived = Assert.Single(result.Criteria).Criterion;
        Assert.Equal(new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
            ResultsStep: 1, ResultsField: "notes", Evidence: EvidenceKind.FileRead), derived.Typed);
        // The shape a planner-written covers_all is accepted in: the engine can check it.
        Assert.Null(EvidenceCoverage.Invalid(derived.Typed!, plan));
        Assert.True(derived.Required);

        // Blocking whether it fails or cannot be decided: the run cannot end Completed on a list nobody showed covered.
        CriterionResult As(CriterionOutcome outcome) => new(derived.Name, derived.Command, derived.Required, outcome, null, null, derived.Origin);
        Assert.True(As(CriterionOutcome.Failed).Blocking);
        Assert.True(As(CriterionOutcome.Unknown).Blocking);
        Assert.Equal(RunOutcomeKind.Incomplete, new SuccessReport([As(CriterionOutcome.Unknown)]).Apply(RunOutcomeKind.Completed));
    }

    /// <summary>A step done for each item accounts for the items by being one step per item - the perItem case.</summary>
    [Fact]
    public void A_step_for_each_item_of_the_list_covers_it_with_its_own_items()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("review a page", [0]) { ForEach = new ForEachSource(0, "pages"), Output = Out(("findings", StepOutputFieldType.Text)) });

        var derived = Assert.Single(UpstreamCoverage.Derive(plan).Criteria).Criterion;

        Assert.Equal(new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
            ResultsStep: 1, ResultsField: "findings", Evidence: EvidenceKind.Call), derived.Typed);
        Assert.Null(EvidenceCoverage.Invalid(derived.Typed!, plan));
    }

    [Fact]
    public void A_consume_by_a_step_with_no_results_output_derives_nothing_and_is_a_defect()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("summarise", [0]) { Output = Out(("summary", StepOutputFieldType.Text)), Consumes = [new ConsumeSpec(0, "pages")] });

        var result = UpstreamCoverage.Derive(plan);

        Assert.Empty(result.Criteria);
        Assert.Equal("step 2 consumes step 1's list pages but has no results output to account for each item", Assert.Single(result.Defects));
    }

    [Fact]
    public void A_step_for_each_item_that_hands_nothing_on_cannot_be_shown_to_cover_its_items()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("review a page", [0]) { ForEach = new ForEachSource(0, "pages") });

        var result = UpstreamCoverage.Derive(plan);

        Assert.Empty(result.Criteria);
        Assert.Contains("step 2 is done for each item of step 1's list pages but hands nothing on", Assert.Single(result.Defects));
    }

    /// <summary>What EvidenceCoverage refuses of a planner's criterion it refuses of a derived one: a read needs paths.</summary>
    [Fact]
    public void A_consume_whose_coverage_the_engine_cannot_check_is_a_defect_with_the_reason()
    {
        var plan = PlanOf(new PlanStepSpec("list names", []) { Output = Out(("names", StepOutputFieldType.StringList)) },
                          new PlanStepSpec("greet", [0]) { Output = Notes, Consumes = [new ConsumeSpec(0, "names") { Evidence = EvidenceKind.FileRead }] });

        var result = UpstreamCoverage.Derive(plan);

        Assert.Empty(result.Criteria);
        Assert.Contains("a file read needs items that are paths", Assert.Single(result.Defects));
    }

    /// <summary>
    /// A DERIVED criterion keeps what the plan declared rather than asking for something weaker: a write is met only by
    /// the step's recorded change to the item's path.
    /// </summary>
    [Fact]
    public void A_consume_that_asks_for_write_evidence_derives_a_criterion_asking_for_it()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("fix pages", [0]) { Output = Notes, Consumes = [new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileWrite }] });

        Assert.Equal(EvidenceKind.FileWrite, Assert.Single(UpstreamCoverage.Derive(plan).Criteria).Criterion.Typed!.Evidence);
    }

    /// <summary>A consume the classifier refuses (a later step, a field that is no list) is its defect, not a criterion.</summary>
    [Fact]
    public void A_consume_that_names_no_earlier_list_derives_nothing()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Out(("pages", StepOutputFieldType.PathList), ("summary", StepOutputFieldType.Text)) },
                          new PlanStepSpec("review", [0]) { Output = Notes, Consumes = [new ConsumeSpec(0, "summary"), new ConsumeSpec(5, "pages")] });

        var result = UpstreamCoverage.Derive(plan);

        Assert.Empty(result.Criteria);
        Assert.Empty(result.Defects);
    }

    // ── against what the planner wrote ──────────────────────────────────────────────────

    private static SuccessCriterionDefinition Written(EvidenceKind evidence, string results = "notes")
        => new("Every pages item is covered", "covers_all", Origin: CriterionOrigin.Proposed)
        {
            Typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
                ResultsStep: 1, ResultsField: results, Evidence: evidence)
        };

    // A filtering step says what it left out in an 'excluded' results field (Task 6b): without one it is a defect.
    private static Plan Reviewed(EvidenceKind evidence, bool subset = false)
        => PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                  new PlanStepSpec("review pages", [0])
                  {
                      Output = subset ? Out(("notes", StepOutputFieldType.Results), ("excluded", StepOutputFieldType.Results)) : Notes,
                      Consumes = [new ConsumeSpec(0, "pages") { Evidence = evidence, Subset = subset }]
                  });

    [Fact]
    public void A_derived_criterion_the_planner_already_wrote_is_not_added_twice()
    {
        var derived = UpstreamCoverage.Derive(Reviewed(EvidenceKind.FileRead)).Criteria;

        Assert.Empty(UpstreamCoverage.NotAlreadyChecked(derived, [Written(EvidenceKind.FileRead)]));
    }

    /// <summary>A written check of another field of the consumer's results is not this one.</summary>
    [Fact]
    public void A_derived_criterion_is_kept_beside_one_the_planner_wrote_over_another_results_field()
        => Assert.Single(UpstreamCoverage.NotAlreadyChecked(UpstreamCoverage.Derive(Reviewed(EvidenceKind.FileRead)).Criteria,
            [Written(EvidenceKind.FileRead, results: "other")]));

    /// <summary>Every kind of evidence the engine records for an item also names it, so "mentioned" adds nothing to a read.</summary>
    [Fact]
    public void A_weaker_derived_criterion_adds_nothing_to_a_stronger_one_the_planner_wrote()
        => Assert.Empty(UpstreamCoverage.NotAlreadyChecked(UpstreamCoverage.Derive(Reviewed(EvidenceKind.Call)).Criteria,
            [Written(EvidenceKind.FileRead)]));

    /// <summary>Never the other way: a planner's "mentioned" does not stand in for a declared read.</summary>
    [Fact]
    public void A_stronger_derived_criterion_is_kept_beside_a_weaker_one_the_planner_wrote()
        => Assert.Single(UpstreamCoverage.NotAlreadyChecked(UpstreamCoverage.Derive(Reviewed(EvidenceKind.FileRead)).Criteria,
            [Written(EvidenceKind.Call)]));

    /// <summary>A filtering consumer owes a partition of the list (Task 6), which no planner criterion states.</summary>
    [Fact]
    public void A_derived_criterion_for_a_filtering_step_is_kept_beside_the_planners()
        => Assert.Single(UpstreamCoverage.NotAlreadyChecked(UpstreamCoverage.Derive(Reviewed(EvidenceKind.FileRead, subset: true)).Criteria,
            [Written(EvidenceKind.FileRead)]));

    /// <summary>
    /// A covers_all the planner wrote says what the step does with the list: it consumes it. Read into the plan as a
    /// consume, the dependency is classified, and the criterion derived from it is the planner's own.
    /// </summary>
    [Fact]
    public void A_coverage_criterion_the_planner_wrote_is_read_as_a_consume()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("review pages", [0]) { Output = Notes });
        Assert.NotEmpty(PlanConsumes.Classify(plan).Defects);

        var folded = UpstreamCoverage.WithPlannedCoverage(plan, [Written(EvidenceKind.FileRead)]);

        Assert.Equal([new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileRead }], folded.Steps[1].Consumes);
        Assert.Empty(PlanConsumes.Classify(folded).Defects);
        Assert.Empty(UpstreamCoverage.NotAlreadyChecked(UpstreamCoverage.Derive(folded).Criteria, [Written(EvidenceKind.FileRead)]));
        // Already declared: not declared again.
        Assert.Same(folded, UpstreamCoverage.WithPlannedCoverage(folded, [Written(EvidenceKind.FileRead)]));
    }

    // ── the steps a step for each item is given ─────────────────────────────────────────

    /// <summary>
    /// An item's step is the forEach step for some items: it waits for the same steps in the same way. Given the
    /// dependencies but not which of them are only an order, its hand-off would treat an "after" as data.
    /// </summary>
    [Fact]
    public void A_step_for_some_items_keeps_what_its_forEach_step_only_waits_for_and_what_it_consumes()
    {
        var tidy = Guid.NewGuid();
        var source = Guid.NewGuid();
        var forEach = new PlanStep(Guid.NewGuid(), "review a page", StepStatus.Pending, [source, tidy])
        {
            ForEach = new ForEachSource(0, "pages"), OrderOnly = [tidy], Consumes = [new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileRead }]
        };

        var item = Assert.Single(FanOut.Steps(forEach, [["wiki/a.md"]]));

        Assert.Equal(forEach.OrderOnly, item.OrderOnly);
        Assert.Equal(forEach.Consumes, item.Consumes);
    }
}

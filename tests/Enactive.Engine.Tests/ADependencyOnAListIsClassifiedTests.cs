namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Plan Phase 1.1: a step that depends on a step handing on a list either consumes that list - and must then account
/// for every item - or only waits for the step. Leaving it unsaid is a defect of the plan, never read as "only waits":
/// that silence is how a run that named twelve pages and read two could end green.
/// </summary>
public sealed class ADependencyOnAListIsClassifiedTests
{
    private static StepOutputSchema Out(params (string Name, StepOutputFieldType Type)[] fields)
        => new("s", 1, fields.Select(f => new StepOutputField(f.Name, f.Type, f.Name)).ToArray());

    private static readonly StepOutputSchema Pages = Out(("pages", StepOutputFieldType.PathList));

    private static Plan PlanOf(params PlanStepSpec[] specs) => DagPlan.FromSpecs(specs);

    /// <summary>THE ONE THAT MATTERS: a step depends on the step that found the pages, and says nothing about them.</summary>
    [Fact]
    public void A_dependency_on_a_list_that_says_neither_consumes_nor_after_is_a_defect()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("summarise", [0]));

        var result = PlanConsumes.Classify(plan);

        Assert.Equal("step 2 depends on step 1, which hands on the list(s) pages, without saying whether it consumes them "
                     + "(consumes) or only waits for it (after)", Assert.Single(result.Defects));
        Assert.Same(plan, result.Plan);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Consuming_every_list_classifies_the_edge_and_consuming_some_does_not()
    {
        var two = Out(("pages", StepOutputFieldType.PathList), ("tags", StepOutputFieldType.StringList), ("count", StepOutputFieldType.Integer));
        var all = PlanOf(new PlanStepSpec("find", []) { Output = two },
                         new PlanStepSpec("use", [0]) { Consumes = [new ConsumeSpec(0, "pages"), new ConsumeSpec(0, "tags")] });
        var some = PlanOf(new PlanStepSpec("find", []) { Output = two },
                          new PlanStepSpec("use", [0]) { Consumes = [new ConsumeSpec(0, "pages")] });

        Assert.Empty(PlanConsumes.Classify(all).Defects);
        var defect = Assert.Single(PlanConsumes.Classify(some).Defects);
        Assert.Contains("step 2 consumes step 1's pages but not its tags:", defect, StringComparison.Ordinal);  // count is no list: nothing to cover
    }

    /// <summary>A step done for each item of the list is, by definition, a consume of it.</summary>
    [Fact]
    public void A_step_for_each_item_of_the_list_classifies_the_edge()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("review a page", [0]) { ForEach = new ForEachSource(0, "pages") });

        var result = PlanConsumes.Classify(plan);
        Assert.Empty(result.Defects);
        Assert.Same(plan, result.Plan);
    }

    [Fact]
    public void Only_waiting_for_the_step_classifies_the_edge()
    {
        var plan = PlanOf(new PlanStepSpec("build", []) { Output = Pages },
                          new PlanStepSpec("deploy", []) { After = [0] });

        var result = PlanConsumes.Classify(plan);
        Assert.Empty(result.Defects);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData(1, "pages", "step 2 consumes from step 2, which does not come before it")]
    [InlineData(5, "pages", "step 2 consumes from step 6, which does not come before it")]
    [InlineData(-1, "pages", "step 2 consumes from step 0, which does not come before it")]
    [InlineData(0, "notes", "step 2 consumes step 1's 'notes', which step 1 does not declare")]
    [InlineData(0, "count", "step 2 consumes step 1's 'count', which is integer, not a list (path[] or string[])")]
    public void A_consume_of_something_that_is_not_an_earlier_steps_list_is_a_defect(int step, string field, string why)
    {
        var plan = PlanOf(new PlanStepSpec("find", []) { Output = Out(("pages", StepOutputFieldType.PathList), ("count", StepOutputFieldType.Integer)) },
                          new PlanStepSpec("use", []) { Consumes = [new ConsumeSpec(step, field)] });

        var result = PlanConsumes.Classify(plan);

        Assert.Contains(why, result.Defects);
        Assert.Same(plan, result.Plan);                                              // nothing is added for a consume that is wrong
    }

    /// <summary>
    /// A consume of a step the consumer does not wait for would read a list that does not exist yet: the dependency is
    /// added, and said - not added silently, as a forEach's is.
    /// </summary>
    [Fact]
    public void Consuming_a_step_it_does_not_depend_on_adds_the_dependency_and_says_so()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("other", []),
                          new PlanStepSpec("summarise", [1]) { Consumes = [new ConsumeSpec(0, "pages")] });

        var result = PlanConsumes.Classify(plan);

        Assert.Empty(result.Defects);
        Assert.Equal([plan.Steps[1].Id, plan.Steps[0].Id], result.Plan.Steps[2].DependsOn);
        Assert.Contains("step 3 consumes step 1's pages but did not depend on it", Assert.Single(result.Notes), StringComparison.Ordinal);
        Assert.Equal(plan.Steps.Select(s => s.Id), result.Plan.Steps.Select(s => s.Id));    // no step removed or replaced
        Assert.Equal(plan.Steps[2].Consumes, result.Plan.Steps[2].Consumes);
        Assert.Equal(plan.Id, result.Plan.Id);
    }

    [Fact]
    public void Consuming_a_step_it_already_depends_on_through_another_adds_nothing()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("other", [0]) { After = [0] },
                          new PlanStepSpec("summarise", [1]) { Consumes = [new ConsumeSpec(0, "pages")] });

        var result = PlanConsumes.Classify(plan);

        Assert.Same(plan, result.Plan);
        Assert.Empty(result.Notes);
        Assert.Empty(result.Defects);
    }

    [Fact]
    public void Consuming_a_step_and_only_waiting_for_it_is_a_conflict()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("summarise", [0]) { After = [0], Consumes = [new ConsumeSpec(0, "pages")] });

        Assert.Equal("step 2 both consumes step 1's pages and says it only waits for it (after): one or the other",
            Assert.Single(PlanConsumes.Classify(plan).Defects));
    }

    [Fact]
    public void An_evidence_word_the_engine_does_not_know_is_a_defect()
    {
        var plan = PlanOf(new PlanStepSpec("find pages", []) { Output = Pages },
                          new PlanStepSpec("summarise", [0]) { Consumes = [new ConsumeSpec(0, "pages") { UnknownEvidence = "vibes" }] });

        Assert.Equal("step 2 asks for 'vibes' evidence of step 1's pages, which is not a kind the engine knows "
                     + "(mentioned, file_read, file_write, command)", Assert.Single(PlanConsumes.Classify(plan).Defects));
    }

    /// <summary>Without lists there is nothing to cover: the plan comes back as it went in, whatever it declares.</summary>
    [Fact]
    public void A_plan_with_no_list_outputs_is_returned_unchanged()
    {
        var plan = PlanOf(new PlanStepSpec("write", []) { Output = Out(("report", StepOutputFieldType.Path)) },
                          new PlanStepSpec("check", [0]),
                          new PlanStepSpec("tidy", []) { Consumes = [new ConsumeSpec(0, "report") { UnknownEvidence = "vibes" }] });

        var result = PlanConsumes.Classify(plan);

        Assert.Same(plan, result.Plan);
        Assert.Empty(result.Notes);
        Assert.Empty(result.Defects);
    }
}

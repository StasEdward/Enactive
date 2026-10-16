namespace Enactive.Agents;

using Enactive.Core.Tasks;
using Enactive.Core.Templates;

/// <summary>
/// The coverage a plan owes for the lists its steps consume, derived from its structure (Plan Phase 1.2): for every
/// consume - declared, or a forEach, which is one by definition - a covers_all criterion over the consumer's results.
/// Pure: a plan in, criteria and what cannot be derived out.
///
/// <para><b>Why derived and not written.</b> The planner could write covers_all and mostly did not: a run that named
/// twelve wiki pages and summarised two ended green because nothing said the step was meant to handle the twelve. A
/// consume IS that statement, so the check follows from it; the planner cannot forget it.</para>
/// </summary>
public static class UpstreamCoverage
{
    /// <summary>A derived criterion, with the consume it was derived from and the consuming step's plan position.</summary>
    public sealed record Derived(SuccessCriterionDefinition Criterion, ConsumeSpec Consume, int Consumer);

    /// <param name="Criteria">One coverage criterion per consume the engine can check.</param>
    /// <param name="Defects">Each consume no criterion can be derived for - for the planner to correct.</param>
    public sealed record Result(IReadOnlyList<Derived> Criteria, IReadOnlyList<string> Defects);

    public static Result Derive(Plan plan)
    {
        var steps = plan.Steps;
        bool IsList(int step, string field) => step >= 0 && step < steps.Count
            && steps[step].Output?.Fields.FirstOrDefault(f => f.Name == field)?.Type is StepOutputFieldType.PathList or StepOutputFieldType.StringList;

        var criteria = new List<Derived>();
        var defects = new List<string>();
        for (var s = 0; s < steps.Count; s++)
        {
            // What this step consumes and the engine can hold it to. A consume that names no earlier step's list, or an
            // evidence word nobody knows, is the classifier's defect (PlanConsumes) and derives nothing here.
            var consumes = (steps[s].Consumes ?? [])
                .Where(c => c.Step < s && c.UnknownEvidence is null && IsList(c.Step, c.Field)).Distinct().ToList();
            if (steps[s].ForEach is { } each && each.Step < s && IsList(each.Step, each.Field)
                && !consumes.Any(c => c.Step == each.Step && c.Field == each.Field))
                consumes.Add(new ConsumeSpec(each.Step, each.Field));

            foreach (var c in consumes)
            {
                var perItem = steps[s].ForEach is { } over && over.Step == c.Step && over.Field == c.Field;
                // A results field names its items; a step done for each item accounts for them by being one step per
                // item, and whatever it hands on is the item's result (the perItem case EvidenceCoverage accepts).
                // A step that filters says what it left out in its own results field ('excluded'): that one is not where it
                // accounts for what it processed, wherever the plan declared it.
                var field = steps[s].Output?.Fields.FirstOrDefault(f => f.Type == StepOutputFieldType.Results
                                                                         && !(c.Subset && f.Name == EvidenceCoverage.ExcludedField))?.Name
                            ?? (perItem ? steps[s].Output?.Fields.FirstOrDefault(f => !(c.Subset && f.Name == EvidenceCoverage.ExcludedField))?.Name : null);
                if (field is null)
                {
                    defects.Add(perItem
                        ? $"step {s + 1} is done for each item of step {c.Step + 1}'s list {c.Field} but hands nothing on, so no "
                          + "item's result can be accounted for"
                        : $"step {s + 1} consumes step {c.Step + 1}'s list {c.Field} but has no results output to account for each item");
                    continue;
                }

                // Built with the evidence the plan declared, file_write included: asking for less than was declared would
                // be the quiet weakening this exists to stop. A write is the step's own recorded change to the item's
                // path (EvidenceCoverage.Gather).
                // A subset consume (a step that filters) is checked as a partition of the list - processed with evidence, or
                // left out with a reason (EvidenceCoverage.PartitionProblems) - and needs its 'excluded' results field to
                // say what it left out: without one, Invalid refuses it, and that is a defect of the contract like any other.
                var typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll,
                    SourceStep: c.Step, SourceField: c.Field, ResultsStep: s, ResultsField: field, Evidence: c.Evidence, Subset: c.Subset);
                if (EvidenceCoverage.Invalid(typed, plan) is { } why)
                {
                    defects.Add($"step {s + 1}'s coverage of step {c.Step + 1}'s list {c.Field} cannot be checked: {why}");
                    continue;
                }

                //
                // Declared and required, so it BLOCKS (CriterionResult.Blocking): failed, or not decidable, the run
                // cannot be Completed. Proposed would let "could not be decided" through - right for a planner's guess,
                // wrong for the coverage the plan's own structure owes. System is never required. Declared also keeps
                // it out of what the engine does only to a planner's proposals: the before-the-work baseline, being
                // dropped when commands are denied, and the diagnosis that may swap a failing check for another.
                criteria.Add(new Derived(new SuccessCriterionDefinition(
                    $"Every {c.Field} item of step {c.Step + 1} is covered by step {s + 1} ({EvidenceCoverage.NameOf(c.Evidence)})",
                    EvidenceCoverage.Describe(typed) + ", derived from what the step consumes", 0, Required: true,
                    Origin: CriterionOrigin.Declared) { Typed = typed }, c, s));
            }
        }
        return new(criteria, defects);
    }

    /// <summary>
    /// The derived criteria that add something to <paramref name="existing"/>. One is redundant beside a covers_all
    /// over the same list and the same consuming step's same results field that asks for the same evidence - a check of
    /// another field of the step's results is not this one - or for any evidence when the
    /// derived one asks only "mentioned", which every kind the engine records implies (EvidenceCoverage.Gather counts a
    /// read or a command as a mention). Never the other way: an explicit "mentioned" does not stand in for a declared
    /// read. And only one with the same exclusion policy: a step that filters owes a partition (processed or left out with
    /// a reason), which a criterion asking a result for every item does not check, and that one asks more than a
    /// partition does - neither stands in for the other.
    /// </summary>
    public static IReadOnlyList<SuccessCriterionDefinition> NotAlreadyChecked(IReadOnlyList<Derived> derived,
        IEnumerable<SuccessCriterionDefinition> existing)
    {
        var written = existing.Select(e => e.Typed).OfType<TypedCriterion>().Where(t => t.Kind == TypedCriterionKind.EvidenceCoversAll).ToArray();
        return derived
            .Where(d => !written.Any(t => t.SourceStep == d.Consume.Step && t.SourceField == d.Consume.Field
                && t.ResultsStep == d.Consumer && t.ResultsField == d.Criterion.Typed!.ResultsField
                && t.Subset == d.Criterion.Typed.Subset
                && (t.Evidence == d.Consume.Evidence || d.Consume.Evidence == EvidenceKind.Call)))
            .DistinctBy(d => d.Criterion.Typed)
            .Select(d => d.Criterion).ToArray();
    }

    /// <summary>
    /// The plan, with each covers_all the planner wrote read as what it says: its results step consumes its source's
    /// list. Without it a plan that stated the coverage as a criterion would still be refused as not saying what the
    /// step does with the list. Only accepted criteria (TypedCriteria.Validate), so what the engine could not check
    /// declares nothing. The same plan when nothing is added.
    /// </summary>
    public static Plan WithPlannedCoverage(Plan plan, IEnumerable<SuccessCriterionDefinition> accepted)
    {
        var steps = plan.Steps.ToArray();
        var changed = false;
        foreach (var t in accepted.Select(c => c.Typed).OfType<TypedCriterion>())
        {
            if (t is not { Kind: TypedCriterionKind.EvidenceCoversAll, SourceStep: { } source, SourceField: { } field, ResultsStep: { } results }
                || results < 0 || results >= steps.Length)
                continue;
            if (steps[results].Consumes?.Any(c => c.Step == source && c.Field == field) == true)
                continue;
            steps[results] = steps[results] with
            {
                Consumes = [.. steps[results].Consumes ?? [], new ConsumeSpec(source, field) { Evidence = t.Evidence ?? EvidenceKind.Call, Subset = t.Subset }]
            };
            changed = true;
        }
        return changed ? plan with { Steps = steps } : plan;
    }
}

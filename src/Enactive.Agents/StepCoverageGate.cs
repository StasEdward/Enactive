namespace Enactive.Agents;

using System.Text;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;

/// <summary>
/// Coverage of a consumed list, decided at the consuming step's own gate (Plan Phase 1.3): when the step has ended, before
/// any review, from the outputs the steps before it handed on and the candidate it has just handed on. Pure: criteria and
/// outputs in, a decision out.
///
/// <para><b>Why at the step and not only at the end.</b> The coverage criteria were decided when the run was over. A step
/// that consumed twelve pages and handed on notes for two passed its review - the reviewer saw two good notes - and the
/// run found out at the end, when nothing could be done about it but call it incomplete. The engine can see that ten
/// items have nothing behind them the moment the step ends, which is when the step can still be told and put it
/// right; and a reviewer asked about it would only be asked to count.</para>
/// </summary>
internal static class StepCoverageGate
{
    /// <summary>How many uncovered items a one-line summary names before it gives a count of the rest.</summary>
    private const int NamedInLine = 5;

    /// <summary>How many uncovered items the repair advice lists, each with what it needs, before it gives a count.</summary>
    private const int NamedInAdvice = 40;

    /// <summary>One criterion as the gate decided it, with the account of every item.</summary>
    internal sealed record Checked(SuccessCriterionDefinition Criterion, IReadOnlyList<EvidenceCoverage.ItemAccount> Items)
    {
        public TypedCriterion Typed => Criterion.Typed!;
        public bool Passed => Items.All(i => i.Covered);
        public int Covered => Items.Count(i => i.Covered);

        /// <summary>"covered 2 of 12; not covered: a (no result); ...; and 7 more" - a handful named, then a count.</summary>
        public string Line()
        {
            var missing = Items.Where(i => !i.Covered).ToArray();
            return missing.Length == 0
                ? $"covered {Covered} of {Items.Count}"
                : $"covered {Covered} of {Items.Count}; not covered: "
                  + string.Join("; ", missing.Take(NamedInLine).Select(i => $"{i.Item} ({i.Why})"))
                  + (missing.Length > NamedInLine ? $"; and {missing.Length - NamedInLine} more" : "") + ".";
        }
    }

    /// <summary>What the gate decided for one attempt of a step.</summary>
    internal sealed record Outcome(IReadOnlyList<Checked> Checks)
    {
        public bool Passed => Checks.All(c => c.Passed);

        /// <summary>
        /// The covered items of a criterion that asked only for "mentioned" with nothing but a mention behind them: named
        /// in a call that succeeded, and neither read, written nor run. One call naming every path covers every item
        /// (EvidenceCoverage.Gather counts any successful call whose arguments name it), so coverage that rests on this
        /// alone shows the items were in hand, not that anything was done with them - the step may stand as done only on
        /// a reviewer's approving verdict.
        /// </summary>
        public IReadOnlyList<string> OnlyMentioned => Checks
            .Where(c => c.Typed.Evidence is null or EvidenceKind.Call)
            .SelectMany(c => c.Items.Where(i => i.Covered && i.Found.All(k => k == EvidenceKind.Call)).Select(i => i.Item))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>The reason a person reads, and the step is rejected with: the failed criteria, one line each.</summary>
        public string Notes
        {
            get
            {
                var failed = Checks.Where(c => !c.Passed).ToArray();
                return failed.Length == 1 ? failed[0].Line()
                    : string.Join(" ", failed.Select(c => $"{c.Typed.SourceField} of step {c.Typed.SourceStep + 1}: {c.Line()}"));
            }
        }

        /// <summary>
        /// What the step is told on its retry: every uncovered item, what it needs and what was found, and that the
        /// engine counts evidence when the result is handed on - evidence gathered after the hand-over counts only once
        /// the result is handed on again (EvidenceCoverage.Gather runs at submit time).
        /// </summary>
        public string RepairAdvice
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var c in Checks.Where(c => !c.Passed))
                {
                    var missing = c.Items.Where(i => !i.Covered).ToArray();
                    sb.AppendLine($"Step {c.Typed.SourceStep + 1}'s list '{c.Typed.SourceField}' has {c.Items.Count} items; this step "
                        + $"covered {c.Covered}. Not covered, and what each needs:");
                    foreach (var i in missing.Take(NamedInAdvice))
                        sb.AppendLine($"- {i.Item}: {Needs(i, c.Typed.ResultsField!)} - found: {Found(i)}");
                    if (missing.Length > NamedInAdvice)
                        sb.AppendLine($"- and {missing.Length - NamedInAdvice} more of the list, each needing the same.");
                }
                sb.Append($"Do what each item needs, then hand the result on again with {StepOutputContract.ToolName}, with a result for "
                    + "every item: the engine counts what backs an item when the result is handed on.");
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// The coverage criteria this step's gate decides: every covers_all - derived from a consume, or the planner's - whose
    /// results step is this one. The caller passes none for a step done for each item and for its join (Task 6b): the
    /// join's coverage is the item steps', and stays with the run-end check for now.
    /// </summary>
    internal static IReadOnlyList<SuccessCriterionDefinition> For(IEnumerable<SuccessCriterionDefinition> criteria, int planIndex)
        => criteria.Where(c => c.Typed is { Kind: TypedCriterionKind.EvidenceCoversAll } t && t.ResultsStep == planIndex)
            .DistinctBy(c => c.Typed).ToArray();

    /// <summary>
    /// Decides the step's coverage from what the steps before it handed on and its own candidate output. A criterion
    /// whose source handed on no list is left to the run-end check: there is nothing for this step to cover, and nothing
    /// a retry of it could change.
    /// </summary>
    /// <param name="stepNo">The step's number, its plan position + 1 - the number its candidate output carries.</param>
    internal static Outcome? Check(IReadOnlyList<SuccessCriterionDefinition> criteria, IEnumerable<StepOutput> committed,
        StepOutput? candidate, int stepNo)
    {
        if (criteria.Count == 0) return null;
        var outputs = committed.Where(o => o.StepNo != stepNo).Concat(candidate is null ? [] : [candidate]).ToArray();
        var checks = criteria.Select(c => EvidenceCoverage.Account(c.Typed!, outputs) is { } items ? new Checked(c, items) : null)
            .OfType<Checked>().ToArray();
        return checks.Length == 0 ? null : new Outcome(checks);
    }

    internal static string Needs(EvidenceCoverage.ItemAccount item, string resultsField)
        => (item.HasResult ? "" : $"a result in '{resultsField}', and ") + item.Required switch
        {
            EvidenceKind.FileRead => "file_read: the file read whole, every line",
            EvidenceKind.FileWrite => "file_write: a change to the file by this step (a write, an edit, a deletion or a rename)",
            EvidenceKind.Command => "command: a command that ran successfully with the item among its arguments",
            _ => "mentioned: a call that succeeded with the item among its arguments"
        };

    internal static string Found(EvidenceCoverage.ItemAccount item)
    {
        var kinds = item.Found.Select(EvidenceCoverage.NameOf).Distinct().ToArray();
        var said = kinds.Length == 0 ? "nothing" : string.Join(", ", kinds);
        return item.Gap is { } gap && item.Required == EvidenceKind.FileRead && !item.Found.Contains(EvidenceKind.FileRead)
            ? $"{said} ({gap})" : said;
    }
}

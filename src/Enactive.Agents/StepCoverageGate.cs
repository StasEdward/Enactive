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
        /// <summary>For a step that filters: what it processed or left out that is not on the list (EvidenceCoverage.Foreign).</summary>
        public IReadOnlyList<string> Foreign { get; init; } = [];

        public TypedCriterion Typed => Criterion.Typed!;
        public bool Passed => Items.All(i => i.Covered) && Foreign.Count == 0;
        public int Covered => Items.Count(i => i.Covered);

        /// <summary>
        /// "covered 2 of 12; not covered: a (no result); ...; and 7 more" - a handful named, then a count. For a step that
        /// filters, the faults of its partition, each said apart (EvidenceCoverage.PartitionProblems) - the run-end check's
        /// own words.
        /// </summary>
        public string Line()
        {
            if (Typed.Subset)
                return EvidenceCoverage.PartitionProblems(Typed, Items, Foreign, NamedInLine) is { Count: > 0 } problems
                    ? $"covered {Covered} of {Items.Count}; " + string.Join("; ", problems) + "."
                    : $"covered {Covered} of {Items.Count}";
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
        /// The items a step that filters left out, each with its reason. The engine checks only that each has one; whether
        /// the request permits leaving it out is a reviewer's to judge, and a reason alone never authorises an omission -
        /// without an approving verdict the step is done, not confirmed (Plan Phase 1.2).
        /// </summary>
        public IReadOnlyList<(string Item, string Reason)> Exclusions => Checks
            .SelectMany(c => c.Items.Where(i => i.ExcludedWithReason).Select(i => (i.Item, Reason: i.Excluded!)))
            .DistinctBy(x => EvidenceCoverage.Normal(x.Item), StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>
        /// What the steps after a join are told when its items are not all covered (Plan Phase 1.3): every item not
        /// covered, by name and why, so a report can say what is missing rather than present the work as whole. The
        /// item steps' own account lists each item's status; this says which the coverage the plan requires does not
        /// count, which a finished item without its evidence is too.
        /// </summary>
        public string HandOff(string what)
            => string.Join(" ", Checks.Where(c => !c.Passed).Select(c =>
               {
                   var missing = c.Items.Where(i => !i.Covered).ToArray();
                   return $"{what} leaves items not covered, as the coverage the plan requires counts them (covered {c.Covered} of {c.Items.Count}): "
                          + string.Join(", ", missing.Take(NamedInAdvice).Select(i => $"{i.Item} ({i.Why})"))
                          + (missing.Length > NamedInAdvice ? $", and {missing.Length - NamedInAdvice} more" : "") + ".";
               }))
               + " Say which items are not covered, and why, in what this step writes - do not present the work as complete.";

        /// <summary>
        /// What a step is told, and its end says, when it left items out and no reviewer approved it: the items and their
        /// reasons, and that a reason is not an approval.
        /// </summary>
        public string? Unapproved()
        {
            var left = Exclusions;
            if (left.Count == 0) return null;
            return $"{left.Count} item(s) were left out, each with a reason: "
                   + string.Join("; ", left.Take(NamedInLine).Select(x => $"{x.Item} ({Clip(x.Reason, 80)})"))
                   + (left.Count > NamedInLine ? $"; and {left.Count - NamedInLine} more" : "")
                   + ". Whether the request permits leaving them out is a reviewer's to judge, and no review approved it.";
        }

        /// <summary>
        /// The covered items of a criterion that asked only for "mentioned" with nothing but a mention behind them: named
        /// in a call that succeeded, and neither read, written nor run. One call naming every path covers every item
        /// (EvidenceCoverage.Gather counts any successful call whose arguments name it), so coverage that rests on this
        /// alone shows the items were in hand, not that anything was done with them - the step may stand as done only on
        /// a reviewer's approving verdict.
        /// </summary>
        public IReadOnlyList<string> OnlyMentioned => Checks
            .Where(c => c.Typed.Evidence is null or EvidenceKind.Call)
            // An item left out is not processed at all - it has no evidence to be only a mention (Exclusions holds it).
            .SelectMany(c => c.Items.Where(i => i.Covered && !i.Excluding && i.Found.All(k => k == EvidenceKind.Call)).Select(i => i.Item))
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
                    foreach (var stray in c.Foreign.Take(NamedInAdvice))
                        sb.AppendLine($"- {stray}: not an item of the list - take it out of '{c.Typed.ResultsField}' and '{EvidenceCoverage.ExcludedField}'.");
                    if (c.Foreign.Count > NamedInAdvice)
                        sb.AppendLine($"- and {c.Foreign.Count - NamedInAdvice} more that are not items of the list.");
                }
                sb.Append($"Do what each item needs, then hand the result on again with {StepOutputContract.ToolName}, with a result for "
                    + "every item: the engine counts what backs an item when the result is handed on.");
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// The coverage criteria this step's gate decides: every covers_all - derived from a consume, or the planner's - whose
    /// results step is this one. The caller passes none for a step done for each item: its coverage is decided at its join
    /// (Orchestrator's JoinAsync), over what all its items handed on - which never rejects the join, only leaves it
    /// unconfirmed.
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
        var checks = criteria.Select(c => EvidenceCoverage.Account(c.Typed!, outputs) is { } items
                ? new Checked(c, items) { Foreign = EvidenceCoverage.Foreign(c.Typed!, outputs) } : null)
            .OfType<Checked>().ToArray();
        return checks.Length == 0 ? null : new Outcome(checks);
    }

    /// <summary>
    /// The least room the account is given at all: below it, it could hold no more than its opening lines, which tell
    /// the reviewer nothing the step's calls do not. Left out instead - see Orchestrator.ReviewAttemptAsync.
    /// </summary>
    internal const int MinimumAccountChars = 600;

    /// <summary>
    /// Over the items a step that filters left out, in the account: the question only a reviewer can answer. The engine
    /// checked that each has a reason; a reason is the step's word, and does not make leaving an item out right.
    /// </summary>
    internal const string ExclusionsHeading = "Left out by this step, each with its reason - does the request permit leaving out EACH of these? "
        + "A reason is the step's word, not a permission: fail the step for any the request does not permit.";

    /// <summary>
    /// The heading of the engine's per-item account in the reviewer's input. A section of its own, not a journal entry
    /// among the calls: the evidence budget is shared out fairly between call results, so an entry there got an equal
    /// share like a read and lost its middle - the weak items - once a dozen reads filled the budget. Its room is taken
    /// out of the evidence budget instead (Orchestrator.ReviewAttemptAsync), so the reviewer is shown no more than before.
    /// </summary>
    internal const string SectionHeading = "ITEMS - the engine's account of the list(s) this step consumes (not a call; the calls it names are among the TOOL CALLS):";

    /// <summary>
    /// What the reviewer is shown of the step's coverage, item by item (Plan Phase 1.2): every item a step that filters
    /// left out, with its reason, first and under a question for the reviewer (ExclusionsHeading); the items with less
    /// than the evidence required, or with nothing but a mention behind them, in full - what was required, what was
    /// found, the recorded calls behind it, the result handed on - and the items backed as required as a count by kind. A
    /// mention meets "mentioned" but shows neither a read nor the work, and it is exactly what the reviewer's verdict is
    /// needed for (OnlyMentioned), so it is shown, not counted - as an exclusion is (Exclusions). Within
    /// <paramref name="maxChars"/>: what does not fit is named, then counted.
    ///
    /// <para>Said in the entry: the engine decided that the evidence is THERE; whether the work on each item is what was
    /// asked is the reviewer's. A count read as a verdict is how a run with twelve mentions passed as twelve reviews.</para>
    /// </summary>
    /// <param name="actions">The step's recorded calls - the concrete reads, writes and calls behind each item.</param>
    internal static string ReviewerBlock(Outcome outcome, IReadOnlyList<Enactive.Core.Execution.ExecutedAction> actions, int maxChars)
    {
        const int CallChars = 80, ResultChars = 120, ReasonChars = 200, Reserve = 160;
        var sb = new StringBuilder("Checked by the engine before this review: for each item of the list(s) this step consumes, whether "
            + "it handed a result on and what it recorded behind it. The engine decided only that the evidence is THERE; "
            + "whether the work on each item is what was asked is yours to judge.\n");
        var calls = actions.Where(a => a.Outcome == Enactive.Core.Execution.ActionOutcome.Succeeded
                                       && a.Origin != Enactive.Core.Execution.ToolCallOrigin.Engine && a.Tool != StepOutputContract.ToolName)
            .Select(a => (Action: a, Named: a.Named ?? EvidenceCoverage.StringsIn(a.Arguments))).ToArray();

        // Each entry in full while there is room; then the rest named, while there is room, and counted.
        void Emit(IReadOnlyList<(string Item, string Line)> entries, string rest)
        {
            var shown = 0;
            foreach (var (_, line) in entries)
            {
                if (sb.Length + line.Length + 1 > maxChars - Reserve) break;
                sb.AppendLine(line);
                shown++;
            }
            if (shown == entries.Count) return;
            var left = entries.Skip(shown).Select(e => e.Item).ToArray();
            var tail = new StringBuilder($"- and {left.Length} more {rest}, not shown in full for room: ");
            var named = 0;
            foreach (var item in left)
            {
                if (sb.Length + tail.Length + item.Length + 2 > maxChars - 1) break;
                tail.Append(named++ == 0 ? item : ", " + item);
            }
            if (named < left.Length && sb.Length + tail.Length + 12 <= maxChars) tail.Append(named == 0 ? "(no room to name them)" : ", …");
            sb.AppendLine(tail.ToString());
        }

        foreach (var c in outcome.Checks)
        {
            sb.AppendLine($"Step {c.Typed.SourceStep + 1}'s list '{c.Typed.SourceField}' -> this step's results '{c.Typed.ResultsField}', "
                + $"each item requiring {EvidenceCoverage.NameOf(c.Typed.Evidence ?? EvidenceKind.Call)}"
                + (c.Typed.Subset ? $", or left out in '{EvidenceCoverage.ExcludedField}' with a reason" : "") + $": {c.Line()}");
            // Every exclusion, and first: it is what the verdict is asked about. An item left out has no evidence and needs none.
            var excluded = c.Items.Where(i => i.ExcludedWithReason).ToArray();
            if (excluded.Length > 0)
            {
                sb.AppendLine(ExclusionsHeading);
                Emit(excluded.Select(i => (i.Item, $"- {i.Item}: excluded - {Clip(i.Excluded!, ReasonChars)}")).ToArray(), "item(s) left out");
            }
            bool Weak(EvidenceCoverage.ItemAccount i) => !i.Covered || (!i.Excluding && i.Found.All(k => k == EvidenceKind.Call));
            var strong = c.Items.Where(i => !Weak(i) && !i.ExcludedWithReason).ToArray();
            if (strong.Length > 0)
                sb.AppendLine($"Backed as required: {strong.Length} of {c.Items.Count - excluded.Length} - "
                    + string.Join(", ", strong.GroupBy(Strongest).OrderBy(g => g.Key).Select(g => $"{Label(g.Key)} {g.Count()}")) + ".");
            var weak = c.Items.Where(Weak).ToArray();
            if (weak.Length == 0) continue;
            sb.AppendLine("Shown in full - items with less than required, or with no more than a mention behind them (named in a call "
                + "that succeeded: neither a read nor the work):");
            Emit(weak.Select(i =>
            {
                var behind = calls.Where(x => x.Named.Any(s => EvidenceCoverage.Same(s, i.Item))
                                              || x.Action.ChangedPaths?.Any(p => EvidenceCoverage.Same(p, i.Item)) == true)
                    .Select(x => $"{x.Action.Tool} {Clip(x.Action.Arguments, CallChars)}").ToArray();
                return (i.Item, $"- {i.Item}: required {EvidenceCoverage.NameOf(i.Required)}; found {Found(i)}; "
                    + (behind.Length == 0 ? "by no recorded call" : "by " + string.Join(", ", behind.Take(3)) + (behind.Length > 3 ? $" and {behind.Length - 3} more" : ""))
                    + $"; result: {(i.HasResult ? Clip(i.Result ?? "", ResultChars) : "none")}");
            }).ToArray(), "item(s) with no more than a mention behind them, or less than required");
        }
        var text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..(maxChars - 1)] + "…";
    }

    private static EvidenceKind Strongest(EvidenceCoverage.ItemAccount item)
        => new[] { EvidenceKind.FileWrite, EvidenceKind.FileRead, EvidenceKind.Command }.FirstOrDefault(item.Found.Contains, EvidenceKind.Call);

    private static string Label(EvidenceKind kind) => kind switch
    {
        EvidenceKind.FileWrite => "written",
        EvidenceKind.FileRead => "read whole",
        EvidenceKind.Command => "named by a command that ran",
        _ => "mentioned"
    };

    private static string Clip(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    internal static string Needs(EvidenceCoverage.ItemAccount item, string resultsField)
    {
        var excluded = EvidenceCoverage.ExcludedField;
        // A step that filters puts each item in one of two places: processed, with its evidence, or left out, with a reason.
        if (item.Subset && item.HasResult && item.Excluding)
            return $"one place only - its result in '{resultsField}', or its reason in '{excluded}', not both";
        if (item.Subset && !item.HasResult && item.Excluding)
            return $"a reason in '{excluded}' for leaving it out";
        var evidence = item.Required switch
        {
            EvidenceKind.FileRead => "file_read: the file read whole, every line",
            EvidenceKind.FileWrite => "file_write: a change to the file by this step (a write, an edit, a deletion or a rename)",
            EvidenceKind.Command => "command: a command that ran successfully with the item among its arguments",
            _ => "mentioned: a call that succeeded with the item among its arguments"
        };
        return item.HasResult ? evidence
            : $"a result in '{resultsField}', and {evidence}"
              + (item.Subset ? $" - or an entry in '{excluded}' giving the reason it is left out" : "");
    }

    internal static string Found(EvidenceCoverage.ItemAccount item)
    {
        var kinds = item.Found.Select(EvidenceCoverage.NameOf).Distinct().ToArray();
        var said = kinds.Length == 0 ? "nothing" : string.Join(", ", kinds);
        return item.Gap is { } gap && item.Required == EvidenceKind.FileRead && !item.Found.Contains(EvidenceKind.FileRead)
            ? $"{said} ({gap})" : said;
    }
}

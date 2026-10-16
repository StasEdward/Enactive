namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// Whether every item a run set out to cover was covered - decided from what was DONE, item by item,
/// and not from a step's account of how much it did (Phase 5.1).
///
/// <para><b>Why.</b> A report that names twelve wiki pages is not twelve pages read. A step can list
/// every item it was given and describe each one from its title; it can read two pages and summarise
/// ten. A count in the result, or the step's own "all 12 reviewed", is a claim. What the engine can
/// check is narrower and harder to fake: for each item, a result handed on for THAT item, and, at
/// the moment it was handed on, complete evidence of the required kind for it.</para>
///
/// <para><b>Partial is not covered.</b> A file read to line 400 of 518 has not been read. The item
/// is reported as not covered, WITH the reason, and the other items are unaffected (amendment D): 9
/// of 12 is "covered 9, not covered these 3, and why", never nothing.</para>
///
/// <para><b>Evidence by kind, not by tool (5.2).</b> A file read is anything a tool reports through
/// the read-coverage protocol; a command or a call is any successful call naming the item; a write is
/// a successful call of the step that recorded a change to the item's path. Nothing here knows a tool's name.</para>
/// </summary>
public static class EvidenceCoverage
{
    /// <summary>The planner's words for the evidence kinds. "call" is the older word for "mentioned", still read.</summary>
    internal static EvidenceKind? KindNamed(string? name) => name?.Trim().ToLowerInvariant().Replace('-', '_') switch
    {
        "file_read" or "read" => EvidenceKind.FileRead,
        "file_write" => EvidenceKind.FileWrite,
        "command" => EvidenceKind.Command,
        "mentioned" or "call" or "tool_call" => EvidenceKind.Call,
        _ => null
    };

    // Call is shown as "mentioned": "call" read as if the item had been worked on, when all it says is that a
    // successful call named it - one call naming twelve paths "covers" all twelve (Plan Phase 1.2).
    internal static string NameOf(EvidenceKind kind) => kind switch
    {
        EvidenceKind.FileRead => "file_read",
        EvidenceKind.FileWrite => "file_write",
        EvidenceKind.Command => "command",
        _ => "mentioned"
    };

    /// <summary>How an item is compared: a path written either way round, with or without "./", is the same item.</summary>
    internal static string Normal(string item) => item.Trim().Replace('\\', '/').TrimStart('.', '/').TrimEnd('/');

    internal static bool Same(string a, string b) => string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The field name evidence is recorded under when it is for the step's own items, not a results field.</summary>
    internal const string OwnItems = "*";

    /// <summary>
    /// For every item of every results field in a submission, what the step had shown for it: recorded
    /// when the result is handed on, from the step's own reads, calls and recorded changes.
    /// </summary>
    /// <param name="step">The step handing on, whose own changes alone are write evidence; null for a run of one step.</param>
    internal static IReadOnlyList<ItemEvidence> Gather(StepOutputSchema schema, JsonObject values, ReadLedger reads,
        IReadOnlyList<ExecutedAction> actions, IEnumerable<ToolDefinition> tools, IReadOnlyList<string>? stepItems = null, int? step = null)
    {
        var commands = tools.Where(t => t.Kind == ToolKind.Command).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var succeeded = actions.Where(a => a.Outcome == ActionOutcome.Succeeded && a.Tool != StepOutputContract.ToolName)
            .Select(a => (a.Tool, Named: a.Named ?? StringsIn(a.Arguments))).ToArray();
        // What THIS step changed, by path, from its own recorded calls: a write, an edit, a deletion, a rename from or to
        // the path (ChangeAuthorship reads the same record to say who changed a file). Only a call that succeeded and
        // said which paths it changed; a command's writes are not recorded by file, so a command is never write
        // evidence, and neither is naming a path or reading it. Another step's write - in a journal the whole run
        // shares - is not this step's work on the item.
        var written = actions.Where(a => a.Outcome == ActionOutcome.Succeeded && a.ChangedPaths is { Count: > 0 }
                                         && (step is null || a.Step == step))
            .SelectMany(a => a.ChangedPaths!).ToArray();

        var items = new List<ItemEvidence>();
        // A step for some items: what it hands on is their result, and the evidence is recorded for them.
        var fields = schema.Fields.Where(f => f.Type == StepOutputFieldType.Results)
            .Select(f => (Name: f.Name, Items: values[f.Name] is JsonObject r ? r.Select(p => p.Key).ToArray() : []))
            .ToList();
        if (fields.Count == 0 && stepItems is { Count: > 0 })
            fields.Add((OwnItems, stepItems.ToArray()));
        foreach (var field in fields)
        {
            foreach (var item in field.Items)
            {
                var complete = new List<EvidenceKind>();
                var (whole, gap) = reads.SeenWhole(item);
                if (whole) complete.Add(EvidenceKind.FileRead);
                var naming = succeeded.Where(a => a.Named.Any(s => Same(s, item))).ToArray();
                if (naming.Any(a => commands.Contains(a.Tool))) complete.Add(EvidenceKind.Command);
                // A recorded change to the item's path names it as surely as its arguments do: every kind recorded for an
                // item implies "mentioned", a write as much as a read or a command (UpstreamCoverage.NotAlreadyChecked
                // relies on it).
                var wrote = written.Any(p => Same(p, item));
                if (naming.Length > 0 || whole || wrote) complete.Add(EvidenceKind.Call);
                if (wrote) complete.Add(EvidenceKind.FileWrite);
                items.Add(new ItemEvidence(field.Name, item, complete, gap));
            }
        }
        return items;
    }

    /// <summary>The items of a submission handed on without any complete evidence - said back to the step, so it can still read them.</summary>
    internal static string? Unbacked(IReadOnlyList<ItemEvidence> items)
    {
        var bare = items.Where(i => i.Complete.Count == 0).ToArray();
        if (bare.Length == 0) return null;
        return $"Not backed by anything this step did, so not counted as covered: "
               + string.Join("; ", bare.Take(8).Select(i => i.Gap is { } gap ? $"{i.Item} ({gap})" : i.Item))
               + (bare.Length > 8 ? $"; and {bare.Length - 8} more" : "")
               + ". Read or check them, then hand the result on again.";
    }

    /// <summary>Every string value in a call's arguments, however deep. Arguments that are not JSON name nothing.</summary>
    /// <summary>The longest string a call's arguments may name an item with, as far as the journal keeps it.</summary>
    internal const int MaxNamedChars = 512;

    /// <summary>How many of a call's strings the journal keeps as what it named.</summary>
    internal const int MaxNamed = 256;

    /// <summary>
    /// What a call's FULL arguments name, as the journal keeps it (ExecutedAction.Named): its short string values. Not
    /// the long ones - a file's content, a script - which name no item and would double what the journal holds; an item
    /// is a path or a short string. Bounded in number for the same reason.
    /// </summary>
    internal static IReadOnlyList<string> NamesIn(string arguments)
        => StringsIn(arguments).Where(s => s.Length is > 0 and <= MaxNamedChars).Distinct(StringComparer.Ordinal).Take(MaxNamed).ToArray();

    internal static IReadOnlyList<string> StringsIn(string arguments)
    {
        var found = new List<string>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: found.Add(e.GetString()!); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
            }
        }
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            Walk(doc.RootElement);
        }
        catch (JsonException) { }
        return found;
    }

    /// <summary>
    /// A covers_all criterion as the planner wrote it, checked against the plan: why the engine cannot
    /// accept it, or null. The source must hand on a list, the results step a result per item, the
    /// results step must come after the source, and a file read needs items that are paths.
    /// </summary>
    internal static string? Invalid(TypedCriterion typed, Plan? plan)
    {
        if (plan is null || plan.Steps.Count == 0) return "a run without steps has no step outputs to cover";
        if (typed.SourceStep is not { } source || source < 0 || source >= plan.Steps.Count)
            return "its source is not a step of this plan";
        if (typed.ResultsStep is not { } results || results < 0 || results >= plan.Steps.Count)
            return "its results are not a step of this plan";
        if (source == results) return "the source and the results are the same step";
        if (typed.Evidence is null) return "it names no evidence kind (file_read, file_write, command, mentioned)";

        var list = plan.Steps[source].Output?.Fields.FirstOrDefault(f => f.Name == typed.SourceField);
        if (list is null || list.Type is not (StepOutputFieldType.PathList or StepOutputFieldType.StringList))
            return $"step {source} declares no list output '{typed.SourceField}' (path[] or string[])";
        if (typed.Evidence == EvidenceKind.FileRead && list.Type != StepOutputFieldType.PathList)
            return "a file read needs items that are paths (path[])";
        var map = plan.Steps[results].Output?.Fields.FirstOrDefault(f => f.Name == typed.ResultsField);
        // A step done for each item of the same list gives each item a step of its own, so what that step
        // hands on IS the item's result, whatever type its field is (run 2508838d: "findings", text).
        var perItem = plan.Steps[results].ForEach is { } each && each.Step == source && each.Field == typed.SourceField;
        if (map is null || (map.Type != StepOutputFieldType.Results && !perItem))
            return $"step {results} declares no results output '{typed.ResultsField}'";
        if (perItem) return null;

        // The results step must be able to see the items: it depends on the source, directly or not.
        var byId = plan.Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var seen = new HashSet<int>();
        var pending = new Stack<int>([results]);
        while (pending.TryPop(out var at))
            foreach (var d in plan.Steps[at].DependsOn)
                if (byId.TryGetValue(d, out var i) && seen.Add(i)) pending.Push(i);
        return seen.Contains(source) ? null : $"step {results} does not depend on step {source}, so it never receives the items";
    }

    internal static string Describe(TypedCriterion typed)
        => $"covers_all step {typed.SourceStep}.{typed.SourceField} -> step {typed.ResultsStep}.{typed.ResultsField} "
           + $"({NameOf(typed.Evidence ?? EvidenceKind.Call)})";

    /// <summary>
    /// Decides the criterion from the run's step outputs. A step's number in <paramref name="outputs"/> is its
    /// plan position + 1 (renumbered by position after a fan-out expands the plan), not the order the steps ran in;
    /// the criterion names them from 0, as they were planned.
    /// </summary>
    public static CriterionResult Evaluate(SuccessCriterionDefinition criterion, IEnumerable<StepOutput> outputs)
    {
        var typed = criterion.Typed!;
        CriterionResult Result(CriterionOutcome outcome, string? detail)
            => new(criterion.Name, criterion.Command, criterion.Required, outcome, null, detail, criterion.Origin, criterion.AlreadyPassing);

        if (Account(typed, outputs) is not { } items)
            return Result(CriterionOutcome.Failed, $"step {typed.SourceStep + 1} handed on no list '{typed.SourceField}', so there is nothing to show covered.");
        if (items.Count == 0)
            return Result(CriterionOutcome.Passed, $"step {typed.SourceStep + 1} named no items.");

        var uncovered = items.Where(i => !i.Covered).Select(i => $"{i.Item} ({i.Why})").ToList();
        var covered = items.Count - uncovered.Count;
        return uncovered.Count == 0
            ? Result(CriterionOutcome.Passed, $"covered {covered} of {items.Count}: {AccountedFor(typed.Evidence ?? EvidenceKind.Call)}.")
            : Result(CriterionOutcome.Failed, $"covered {covered} of {items.Count}; not covered: "
                + string.Join("; ", uncovered.Take(12)) + (uncovered.Count > 12 ? $"; and {uncovered.Count - 12} more" : "") + ".");
    }

    /// <summary>
    /// What a pass says, by the kind of evidence it stood on. Only a read says the files were read: "covered 12 of 12"
    /// alone read as if every page had been read, when under "mentioned" it says only that each was named in a call
    /// (Plan Phase 1.2: the report distinguishes every item accounted for from every file read).
    /// </summary>
    private static string AccountedFor(EvidenceKind kind) => kind switch
    {
        EvidenceKind.FileRead => "every file read whole",
        EvidenceKind.FileWrite => "every item accounted for, each file changed by the step",
        EvidenceKind.Command => "every item accounted for, each named by a command that ran",
        _ => "every item accounted for, each mentioned - named in a call that succeeded, which shows neither a read nor the work"
    };

    /// <summary>One item of a consumed list: whether a result was handed on for it, and what backed it.</summary>
    /// <param name="Required">The evidence the criterion asks for.</param>
    /// <param name="Found">The complete evidence the step had for it when the result was handed on.</param>
    /// <param name="Result">What the step handed on for it, as text - null when nothing was.</param>
    internal sealed record ItemAccount(string Item, EvidenceKind Required, bool HasResult, IReadOnlyList<EvidenceKind> Found,
        string? Gap, string? Result)
    {
        public bool Covered => HasResult && Found.Contains(Required);

        /// <summary>Why it is not covered, as the criterion's detail says it.</summary>
        public string Why => !HasResult ? "no result"
            : Required == EvidenceKind.FileRead && Gap is { } gap ? gap
            : $"no {NameOf(Required)} evidence";
    }

    /// <summary>
    /// Item by item, what a covers_all criterion found in the outputs: the one account <see cref="Evaluate"/> and the
    /// step's gate both decide from, so they cannot disagree. Null when the source handed on no list.
    /// </summary>
    internal static IReadOnlyList<ItemAccount>? Account(TypedCriterion typed, IEnumerable<StepOutput> outputs)
    {
        var all = outputs.ToArray();
        var source = all.FirstOrDefault(o => o.StepNo == typed.SourceStep + 1);
        if (source?.Value(typed.SourceField!) is not { ValueKind: JsonValueKind.Array } list)
            return null;
        var items = list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)
            .DistinctBy(Normal, StringComparer.OrdinalIgnoreCase).ToArray();

        var kind = typed.Evidence ?? EvidenceKind.Call;
        var holder = all.FirstOrDefault(o => o.StepNo == typed.ResultsStep + 1);
        var evidence = holder?.Items?.Where(i => i.Field == typed.ResultsField || i.Field == OwnItems).ToArray() ?? [];
        // A results object names its items; a step done for each item has handed on for exactly the items
        // whose steps recorded evidence at the hand-over - the join carries only the steps that finished.
        var map = holder?.Value(typed.ResultsField!) is { ValueKind: JsonValueKind.Object } m ? m : (JsonElement?)null;
        var results = map is { } named
            ? named.EnumerateObject().Select(p => (p.Name, Text: (string?)(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()))).ToArray()
            : evidence.Where(e => e.Field == OwnItems).Select(e => (Name: e.Item, Text: (string?)null)).ToArray();

        return items.Select(item =>
        {
            var result = results.FirstOrDefault(r => Same(r.Name, item));
            var backed = evidence.FirstOrDefault(e => Same(e.Item, item));
            return new ItemAccount(item, kind, result.Name is not null, backed?.Complete ?? [], backed?.Gap, result.Text);
        }).ToArray();
    }
}

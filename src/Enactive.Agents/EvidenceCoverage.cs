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
    /// The results field a step that filters (a subset consume) names what it left out in, each item with the reason. One
    /// name, fixed, so the engine can tell a step's processed results from its exclusions without the plan saying which is
    /// which.
    /// </summary>
    internal const string ExcludedField = "excluded";

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

    /// <summary>
    /// The items each results field of a step is to be keyed by, as its gate counts them (StepCoverageGate.Check over
    /// <paramref name="coverage"/>): the items of every list the step consumes into that field - and, for a step that
    /// filters, into 'excluded' too. A field no consume feeds, or whose source handed on no list, has no entry: what keys
    /// it takes is not the engine's to say.
    /// </summary>
    /// <param name="outputs">What the steps before it handed on - the step's own output is not among them.</param>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Owed(IEnumerable<SuccessCriterionDefinition> coverage,
        IEnumerable<StepOutput> outputs)
    {
        var all = outputs.ToArray();
        var owed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var typed in coverage.Select(c => c.Typed).OfType<TypedCriterion>())
        {
            if (typed.ResultsField is not { } results || typed.SourceField is not { } sourceField) continue;
            // The source as Account finds it, so this and the gate name the same items.
            if (all.FirstOrDefault(o => o.StepNo == typed.SourceStep + 1)?.Value(sourceField) is not { ValueKind: JsonValueKind.Array } list)
                continue;
            var items = list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToArray();
            foreach (var field in typed.Subset ? new[] { results, ExcludedField } : [results])
            {
                if (!owed.TryGetValue(field, out var into)) owed[field] = into = [];
                into.AddRange(items.Where(i => !into.Any(x => Same(x, i))));
            }
        }
        return owed.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// A submission's results keys checked against the items they are owed for (<see cref="Owed"/>): the keys taken as an
    /// item, and the keys that are not one of them.
    /// </summary>
    internal sealed record Keying(IReadOnlyList<(string Field, string Key, string Item)> Taken, IReadOnlyList<(string Field, string Key)> Strangers)
    {
        public static readonly Keying None = new([], []);

        public bool IsStranger(ItemEvidence item) => Strangers.Contains((item.Field, item.Item));
    }

    /// <summary>
    /// Keys each owed results field of an accepted submission by the items themselves, in place: a key that names exactly
    /// one item unambiguously - the item's path in matching quotes or backticks, or its file name when no other item has
    /// that name - is renamed to the item. Any other key that is not an item is left as it is and said to be a stranger.
    ///
    /// <para><b>Why.</b> Bench wiki-drift, all 5 runs: the local worker keyed its results by "CLI.md", by
    /// "'Docs/wiki/CLI.md'", or by one summary key ("discrepancies"), not by Docs/wiki/CLI.md. Each key was looked up as a
    /// path, so a page read whole was answered "CLI.md (never read) - read them": the worker read it again, sent the same
    /// keys, and the repeat guard stopped the step - a false failure.</para>
    ///
    /// <para><b>Why renamed here, once, at acceptance.</b> The evidence is gathered from these values, the steps after this
    /// one are shown them, and the step's gate counts them (Account looks each item up by key): renamed anywhere later,
    /// one of the three would still see "CLI.md", and the gate would say "Docs/wiki/CLI.md (no result)" of a page that
    /// has one.</para>
    ///
    /// <para>Not guessed: a file name two items share, or a key that would land on an item the field already has a result
    /// for (one of the two results would be lost) - each stays a stranger, and the step is told the keys expected.</para>
    /// </summary>
    internal static Keying KeyByItems(StepOutputSchema schema, JsonObject values, IReadOnlyDictionary<string, IReadOnlyList<string>>? owed)
    {
        if (owed is not { Count: > 0 }) return Keying.None;
        var taken = new List<(string, string, string)>();
        var strangers = new List<(string, string)>();
        foreach (var field in schema.Fields.Where(f => f.Type == StepOutputFieldType.Results))
        {
            if (!owed.TryGetValue(field.Name, out var items) || items.Count == 0 || values[field.Name] is not JsonObject map) continue;
            var keys = map.Select(p => p.Key).ToArray();
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (items.Any(i => Same(i, key))) continue;
                var item = NamedItem(key, items);
                if (item is not null && !keys.Any(k => Same(k, item)) && !renamed.Values.Any(v => Same(v, item)))
                {
                    renamed[key] = item;
                    taken.Add((field.Name, key, item));
                }
                else strangers.Add((field.Name, key));
            }
            if (renamed.Count == 0) continue;
            // Rebuilt in the order the step wrote it; a node is detached from the old object before it joins the new one.
            var entries = map.ToArray();
            map.Clear();
            foreach (var (key, result) in entries)
                map[renamed.GetValueOrDefault(key, key)] = result;
        }
        return new(taken, strangers);
    }

    /// <summary>The one item a key names unambiguously (see <see cref="KeyByItems"/>), or null.</summary>
    private static string? NamedItem(string key, IReadOnlyList<string> items)
    {
        var bare = key.Trim();
        if (bare.Length >= 2 && bare[0] is '"' or '\'' or '`' && bare[^1] == bare[0])
            bare = bare[1..^1].Trim();
        if (items.FirstOrDefault(i => Same(i, bare)) is { } path) return path;
        // A file name, not the end of a path: "wiki/CLI.md" could as well be a folder's page the step was not given.
        var name = Normal(bare);
        if (name.Length == 0 || name.Contains('/')) return null;
        var named = items.Where(i => string.Equals(FileName(i), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    private static string FileName(string item)
    {
        var normal = Normal(item);
        return normal[(normal.LastIndexOf('/') + 1)..];
    }

    /// <summary>What the step is told of the keys taken as items - so it keys the next one by the item itself.</summary>
    internal static string? TakenAsItems(Keying keying)
        => keying.Taken.Count == 0 ? null
            : string.Join(" ", keying.Taken.Select(t => $"{t.Field}: '{t.Key}' was taken as the item {t.Item}."));

    /// <summary>
    /// The results keys that are not one of the items this step is responsible for, with the keys expected - in place of
    /// "never read ... read them", which was wrong about them: what is missing is not a read, it is the key (see
    /// <see cref="KeyByItems"/>). Bounded as <see cref="Unbacked"/> is.
    /// </summary>
    internal static string? NotItems(Keying keying, IReadOnlyDictionary<string, IReadOnlyList<string>>? owed)
    {
        if (keying.Strangers.Count == 0 || owed is null) return null;
        static string Named(IEnumerable<string> said, int count)
            => string.Join(", ", said.Take(8)) + (count > 8 ? $", and {count - 8} more" : "");
        var fields = keying.Strangers.GroupBy(s => s.Field).Select(g =>
        {
            var keys = g.Select(s => s.Key).ToArray();
            var expected = owed[g.Key];
            return $"'{g.Key}' is keyed by {Named(keys.Select(k => $"'{k}'"), keys.Length)} - not "
                   + (keys.Length == 1 ? "one" : "any") + " of the items this step is responsible for, so not counted as covered. "
                   + $"Its keys are the items themselves; expected: {Named(expected, expected.Count)}.";
        });
        return string.Join(" ", fields) + " Hand the result on again with one entry per item, keyed by the item.";
    }

    /// <summary>The items of a submission handed on without any complete evidence - said back to the step, so it can still read them.</summary>
    /// <param name="keying">Keys that are not items at all are said by <see cref="NotItems"/>, not here: no read would back them.</param>
    internal static string? Unbacked(IReadOnlyList<ItemEvidence> items, Keying? keying = null)
    {
        // An item left out needs a reason, not evidence: told it had nothing behind it, a filtering step was asked to read
        // the very pages it had decided to leave out.
        var bare = items.Where(i => i.Complete.Count == 0 && i.Field != ExcludedField && keying?.IsStranger(i) != true).ToArray();
        if (bare.Length == 0) return null;
        return $"Not backed by anything this step did, so not counted as covered: "
               + string.Join("; ", bare.Take(8).Select(i => i.Gap is { } gap ? $"{i.Item} ({gap})" : i.Item))
               + (bare.Length > 8 ? $"; and {bare.Length - 8} more" : "")
               + ". Read or check them, then hand the result on again.";
    }

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

    /// <summary>Every string value in a call's arguments, however deep. Arguments that are not JSON name nothing.</summary>
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
        // A step that filters says what it left out, and why, in a field of its own (the controller's ruling): without it a
        // left-out item and a forgotten one look the same, and a reason could be read out of nowhere.
        if (typed.Subset && typed.ResultsField == ExcludedField)
            return $"its results '{ExcludedField}' are where step {results} says what it leaves out, not what it processed";
        if (typed.Subset && plan.Steps[results].Output?.Fields.FirstOrDefault(f => f.Name == ExcludedField)?.Type != StepOutputFieldType.Results)
            return $"step {results} filters the list (subset) but declares no results output '{ExcludedField}' giving the reason for each item it leaves out";
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
           + $"({NameOf(typed.Evidence ?? EvidenceKind.Call)})" + (typed.Subset ? $", the rest left out in '{ExcludedField}' with reasons" : "");

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

        var all = outputs.ToArray();
        if (Account(typed, all) is not { } items)
            return Result(CriterionOutcome.Failed, $"step {typed.SourceStep + 1} handed on no list '{typed.SourceField}', so there is nothing to show covered.");
        if (typed.Subset)
        {
            // A step that filters: the partition, with the gate's words (StepCoverageGate.Checked) - the two cannot disagree.
            var foreign = Foreign(typed, all);
            var problems = PartitionProblems(typed, items, foreign, max: 12);
            return problems.Count == 0
                ? Result(CriterionOutcome.Passed, PartitionPassed(typed, items))
                : Result(CriterionOutcome.Failed, $"covered {items.Count(i => i.Covered)} of {items.Count}; " + string.Join("; ", problems) + ".");
        }
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
        /// <summary>The criterion is a step that filters: the item may be left out with a reason instead of processed.</summary>
        public bool Subset { get; init; }

        /// <summary>
        /// Under a subset criterion, the reason the item was left out (its entry in <see cref="ExcludedField"/>), or null
        /// when it was not left out. Empty when it was left out with no reason - which leaves it not covered.
        /// </summary>
        public string? Excluded { get; init; }

        public bool Excluding => Excluded is not null;

        /// <summary>A result backed as required - or, for a step that filters, left out with a reason and nothing else.</summary>
        public bool Covered => !Subset ? HasResult && Found.Contains(Required)
            : HasResult ? !Excluding && Found.Contains(Required)
            : Excluding && !string.IsNullOrWhiteSpace(Excluded);

        /// <summary>Left out with a reason, and not processed as well: an exclusion a reviewer is asked to permit.</summary>
        public bool ExcludedWithReason => Subset && !HasResult && Excluding && !string.IsNullOrWhiteSpace(Excluded);

        /// <summary>Why it is not covered, as the criterion's detail says it.</summary>
        public string Why => Subset && HasResult && Excluding ? "both processed and excluded"
            : Subset && !HasResult && !Excluding ? "neither processed nor excluded"
            : Subset && !HasResult ? "excluded without a reason"
            : !HasResult ? "no result"
            : Required == EvidenceKind.FileRead && Gap is { } gap ? gap
            : $"no {NameOf(Required)} evidence";
    }

    /// <summary>
    /// What is wrong with a filtering step's partition of the list, each fault said apart and in this order: items both
    /// processed and excluded; items in neither field that are not on the list at all; items of the list in neither; items
    /// left out with no reason; items processed without the evidence required. Empty when the partition holds. Each
    /// names at most <paramref name="max"/> items and counts the rest.
    /// </summary>
    internal static IReadOnlyList<string> PartitionProblems(TypedCriterion typed, IReadOnlyList<ItemAccount> items,
        IReadOnlyList<string> foreign, int max)
    {
        var problems = new List<string>();
        void Add(string what, IEnumerable<string> which)
        {
            var named = which.ToArray();
            if (named.Length > 0)
                problems.Add($"{what}: " + string.Join(", ", named.Take(max)) + (named.Length > max ? $" and {named.Length - max} more" : ""));
        }
        var kind = typed.Evidence ?? EvidenceKind.Call;
        Add($"both processed ('{typed.ResultsField}') and excluded", items.Where(i => i.HasResult && i.Excluding).Select(i => i.Item));
        Add($"not in step {typed.SourceStep + 1}'s list '{typed.SourceField}'", foreign);
        Add("neither processed nor excluded", items.Where(i => !i.HasResult && !i.Excluding).Select(i => i.Item));
        Add("excluded without a reason", items.Where(i => !i.HasResult && i.Excluding && !i.Covered).Select(i => i.Item));
        Add($"processed without {NameOf(kind)} evidence", items.Where(i => i.HasResult && !i.Excluding && !i.Covered)
            .Select(i => kind == EvidenceKind.FileRead && i.Gap is { } gap ? $"{i.Item} ({gap})" : i.Item));
        return problems;
    }

    /// <summary>What a filtering step's partition that holds says: how many were processed, on what, and how many left out.</summary>
    internal static string PartitionPassed(TypedCriterion typed, IReadOnlyList<ItemAccount> items)
    {
        var left = items.Count(i => i.ExcludedWithReason);
        return $"covered {items.Count(i => i.Covered)} of {items.Count}: {items.Count - left} processed - "
               + $"{AccountedFor(typed.Evidence ?? EvidenceKind.Call)} - and {left} left out, each with a reason.";
    }

    /// <summary>
    /// The items a filtering step processed or left out that are not on the list it consumes: something it was not given
    /// cannot stand for an item it was. Empty for a criterion that is not a subset - there a stray result was never read.
    /// </summary>
    internal static IReadOnlyList<string> Foreign(TypedCriterion typed, IEnumerable<StepOutput> outputs)
    {
        if (!typed.Subset) return [];
        var all = outputs.ToArray();
        var list = all.FirstOrDefault(o => o.StepNo == typed.SourceStep + 1)?.Value(typed.SourceField!) is { ValueKind: JsonValueKind.Array } l
            ? l.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToArray() : [];
        var holder = all.FirstOrDefault(o => o.StepNo == typed.ResultsStep + 1);
        return new[] { typed.ResultsField!, ExcludedField }
            .SelectMany(field => holder?.Value(field) is { ValueKind: JsonValueKind.Object } map ? map.EnumerateObject().Select(p => p.Name) : [])
            .Where(key => !list.Any(item => Same(item, key)))
            .DistinctBy(Normal, StringComparer.OrdinalIgnoreCase).ToArray();
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

        // A step that filters names what it left out, and why, in a field of its own: an item there needs a reason and no
        // evidence. A reason is text: false, 0 or {} says nothing about why, and an empty text, or none, is no reason
        // either. The output contract already refuses such values at submission (StepOutputContract.Results: text, not
        // empty), but an output can reach here without it - joined from item steps, or restored - so it is said here too.
        var excluded = typed.Subset && holder?.Value(ExcludedField) is { ValueKind: JsonValueKind.Object } left
            ? left.EnumerateObject().Select(p => (p.Name, Reason: p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "")).ToArray()
            : [];

        return items.Select(item =>
        {
            var result = results.FirstOrDefault(r => Same(r.Name, item));
            var backed = evidence.FirstOrDefault(e => Same(e.Item, item));
            var reason = excluded.FirstOrDefault(x => Same(x.Name, item));
            return new ItemAccount(item, kind, result.Name is not null, backed?.Complete ?? [], backed?.Gap, result.Text)
            {
                Subset = typed.Subset,
                Excluded = reason.Name is null ? null : reason.Reason.Trim()
            };
        }).ToArray();
    }
}

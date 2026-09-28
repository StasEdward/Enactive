namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Tasks;

/// <summary>
/// How far a plan may grow on its own (Phase 5.4). Past any of these, the engine asks rather than
/// creates - and asks rather than fails: finding forty pages is not a mistake, it is a question of
/// how to spend the run on them.
/// </summary>
/// <param name="MaxStepsPerExpansion">Steps one "for each" may be given without asking.</param>
/// <param name="MaxTotalSteps">Steps the whole plan may reach without asking.</param>
/// <param name="MaxDepth">"For each" inside "for each": how many levels without asking.</param>
public sealed record FanOutLimits(int MaxStepsPerExpansion = 12, int MaxTotalSteps = 40, int MaxDepth = 2)
{
    public static readonly FanOutLimits Default = new();
}

/// <summary>
/// A plan that grows from what its steps find (Phase 5.3): a step declared "for each" item another
/// step hands on is given one step per item once the list exists, and becomes the join that waits for
/// them and hands their results on as one.
///
/// <para><b>Why.</b> The planner cannot see how many pages a wiki has; it guessed a batch size, and a
/// batch of four pages was one conversation that grew with every page and was abandoned at its turn
/// ceiling with the next batches skipped behind it (2026-09-28, runs bad685e9 and fdc4f19b). One step
/// per item is a step boundary per item - its own review, its own output, and a failure that costs that
/// item and not a batch. Its own short CONVERSATION only when steps do not share one (more than one step
/// at a time); at one step at a time the run's conversation carries on through the items, as it does
/// through any other steps.</para>
///
/// <para><b>Only from values.</b> The items are a list the engine has already checked as a step
/// output, never a list read out of prose; that is what makes a graph built at run time safe to run.</para>
/// </summary>
public static class FanOut
{
    /// <summary>
    /// The plan's "for each" steps, checked: the source is an earlier step that hands on a list, and
    /// the step waits for it (added when the planner left it out - it cannot run before its items exist).
    /// One that cannot be honoured is made an ordinary step again, with the reason.
    /// </summary>
    public static (Plan Plan, IReadOnlyList<string> Dropped) Validate(Plan plan)
    {
        var dropped = new List<string>();
        var steps = plan.Steps.ToArray();
        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].ForEach is not { } each) continue;
            string? why = null;
            if (each.Step < 0 || each.Step >= i) why = "its items must come from an earlier step";
            else if (steps[each.Step].Output?.Fields.FirstOrDefault(f => f.Name == each.Field) is not
                     { Type: StepOutputFieldType.PathList or StepOutputFieldType.StringList })
                why = $"step {each.Step + 1} hands on no list '{each.Field}' (path[] or string[])";
            if (why is not null)
            {
                dropped.Add($"'{steps[i].Title}' will run once, not for each item - {why}.");
                steps[i] = steps[i] with { ForEach = null, Report = null };
                continue;
            }
            if (!steps[i].DependsOn.Contains(steps[each.Step].Id))
                steps[i] = steps[i] with { DependsOn = [.. steps[i].DependsOn, steps[each.Step].Id] };
            // A document to assemble must be a workspace-relative file path; one that is not is dropped,
            // and the step's items still hand their results on.
            if (steps[i].Report is { } report && (Path.IsPathRooted(report) || report.Replace('\\', '/').Split('/').Contains("..")))
            {
                dropped.Add($"'{steps[i].Title}' will not assemble '{report}' - a report is a path inside the workspace.");
                steps[i] = steps[i] with { Report = null };
            }
            // The engine writes Markdown. A report path that is not a document - source, a project, data - would
            // be overwritten with it, whoever declared it.
            else if (steps[i].Report is { } notADocument
                     && !Path.GetExtension(notADocument).ToLowerInvariant().Equals(".md")
                     && Path.GetExtension(notADocument).ToLowerInvariant() is not (".markdown" or ".txt"))
            {
                dropped.Add($"'{steps[i].Title}' will not assemble '{notADocument}' - the engine writes a Markdown document, "
                            + "and a report is a .md, .markdown or .txt file, never source or data it would overwrite.");
                steps[i] = steps[i] with { Report = null };
            }
        }
        // A step after the items of a report hands its summary on as a value; the engine puts it in the
        // document. Added to its contract whatever else it declared - see ReportDocument.
        var reporting = steps.Where(st => st.ForEach is not null && st.Report is not null).Select(st => st.Id).ToHashSet();
        for (var i = 0; i < steps.Length; i++)
            if (steps[i].DependsOn.Any(reporting.Contains))
                steps[i] = steps[i] with { Output = ReportDocument.WithSummary(steps[i].Output, i + 1) };
        return (plan with { Steps = steps }, dropped);
    }

    /// <summary>
    /// A plan that will make one document from a step's items without saying so: a step done for each item
    /// with no report, something after it building on its results, and a file the run's criteria name. The
    /// step to declare it on, the path, and what to tell the planner - or null when the plan is consistent.
    ///
    /// <para>Run dd7ca94b, 2026-09-28: the planner was told about "report" and wrote a "compile" step
    /// instead; the first page step found no report and created it; the next could not change it. A document
    /// assembled from item results has to be declared, so it can be reserved and written by the engine.</para>
    /// </summary>
    public static (int Step, string Path, string Diagnostic)? MissingReport(Plan plan, IReadOnlyList<PlannedCriterion> criteria)
    {
        var deliverables = criteria
            .Where(c => c.Kind.Trim().ToLowerInvariant() is "file_exists" or "file_contains" && !string.IsNullOrWhiteSpace(c.Path))
            .Select(c => c.Path!.Replace('\\', '/').Trim().TrimStart('.', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (deliverables.Length == 0) return null;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.ForEach is null || step.Report is not null || !plan.Steps.Any(s => s.DependsOn.Contains(step.Id))) continue;
            // A question, not an answer: which file - if any - is a document made from the items is not
            // something the engine can tell from a criterion naming it.
            return (i, deliverables[0],
                $"Step {i} (\"{step.Title}\") is done for each item and a later step builds on its results. If the items' results "
                + "together make ONE document (a report - Markdown or text, not source code), declare it on that step with "
                + "\"report\":\"<path>\": the engine then assembles it from every item's recorded result, no step edits it, and the "
                + "step after the items hands on a \"summary\". The run's criteria name "
                + $"{string.Join(", ", deliverables)} - only declare one of them if it IS that document. If the items make no such "
                + "document, return the plan unchanged.");
        }
        return null;
    }

    /// <summary>The items a source handed on, in order, each once. None when it handed nothing on.</summary>
    public static IReadOnlyList<string> Items(StepOutput? source, string field)
        => source?.Value(field) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)
                .Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>How many levels of "for each" a step is: 1 for one over a plain step's list, 2 over another's, and so on.</summary>
    public static int Depth(IReadOnlyList<PlanStep> steps, PlanStep step)
    {
        var depth = 0;
        for (var at = step; at?.ForEach is { } each && depth <= steps.Count; at = each.Step < steps.Count ? steps[each.Step] : null)
            depth++;
        return depth;
    }

    /// <summary>Items in <paramref name="groups"/> batches as even as they go, in order.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Batches(IReadOnlyList<string> items, int groups)
    {
        groups = Math.Clamp(groups, 1, Math.Max(1, items.Count));
        var size = (int)Math.Ceiling(items.Count / (double)groups);
        return items.Chunk(size).Select(c => (IReadOnlyList<string>)c).ToArray();
    }

    /// <summary>One step per group of items, made from the "for each" step: its title, work, contract and dependencies.</summary>
    public static IReadOnlyList<PlanStep> Steps(PlanStep forEach, IReadOnlyList<IReadOnlyList<string>> groups)
        => groups.Select(items => new PlanStep(Guid.NewGuid(), Title(forEach.Title, items), StepStatus.Pending, forEach.DependsOn, forEach.Complexity)
        {
            ObligationIds = forEach.ObligationIds, Output = forEach.Output, Items = items, ExpandedFrom = forEach.Id
        }).ToArray();

    private static string Title(string title, IReadOnlyList<string> items)
    {
        static string Short(string item) => item.Length <= 60 ? item : item[..57] + "...";
        return items.Count == 1 ? $"{title}: {Short(items[0])}" : $"{title}: {Short(items[0])} and {items.Count - 1} more";
    }

    /// <summary>What a step for some items is told about them: exactly these, and the rest are not its business.</summary>
    public static string Instruction(PlanStep step, IReadOnlyList<PlanStep> plan)
    {
        if (step.Items is not { } items) return "";
        var forEach = plan.FirstOrDefault(s => s.Id == step.ExpandedFrom);
        var source = forEach?.ForEach is { } each ? $" from step {each.Step + 1}'s {each.Field}" : "";
        return $"This step is for {(items.Count == 1 ? "one item" : $"{items.Count} items")}{source}: "
               + string.Join(", ", items) + ". Do the work for exactly "
               + (items.Count == 1 ? "it" : "these") + "; every other item has a step of its own. It may change "
               + (items.Count == 1 ? "that item" : "those items") + " and files it creates, and nothing shared: hand what "
               + $"it found on with {StepOutputContract.ToolName} - the engine assembles shared documents from every item's result.\n";
    }

    /// <summary>
    /// What a step for particular items is, said to whoever judges it (Phase 5.3). A request is written for
    /// the whole run - "check every page and write the findings into X" - and a reviewer holding one item's
    /// step to it asks for every page, and for X. Run d91b6a45, 2026-09-28: a page step was rejected because
    /// "the requirement mandates a persisted findings file; none exists" - a file no item's step may write,
    /// which the engine assembles from their results. Null for a step that is not for particular items.
    /// </summary>
    public static string? ScopeNote(PlanStep step, IReadOnlyList<PlanStep> plan)
    {
        if (step.Items is not { Count: > 0 } items || step.ExpandedFrom is null) return null;
        var forEach = plan.FirstOrDefault(s => s.Id == step.ExpandedFrom);
        var note = $"it is done for {(items.Count == 1 ? "one item" : $"{items.Count} items")} - {string.Join(", ", items)} - "
                   + "and every other item has a step of its own: judge its work on "
                   + (items.Count == 1 ? "that item" : "those items") + ", not on the others. ";
        if (forEach?.Report is { } report)
            note += $"The items' results make one document, {report}, which the ENGINE assembles from each item's recorded result; "
                    + "no step writes or edits it, and this one may not. For this step a requirement to write findings into "
                    + $"{report} is met by handing its item's result on with {StepOutputContract.ToolName}: do not require the "
                    + "document to exist, or to hold this item, and judge the result that was handed on.";
        else
            note += $"What it found is handed on with {StepOutputContract.ToolName}; what the run makes of all the items is the "
                    + "steps after them.";
        return note;
    }

    /// <summary>
    /// The join's output: the results of the steps it waited for, as one. A results field is the union
    /// of theirs, a list their concatenation, text theirs one after another under each step's title;
    /// a single value (a count, a flag) cannot be joined without deciding what it means, and is left out.
    /// What backed each item travels with it.
    /// </summary>
    public static StepOutput? Join(PlanStep join, int joinNo, IReadOnlyList<(PlanStep Step, StepOutput Output)> handed)
    {
        if (join.Output is not { } schema || handed.Count == 0) return null;
        var values = new JsonObject();
        foreach (var field in schema.Fields)
        {
            var parts = handed.Select(h => (h.Step, Value: JsonNode.Parse(h.Output.ValuesJson)?[field.Name])).Where(p => p.Value is not null).ToArray();
            if (parts.Length == 0) continue;
            switch (field.Type)
            {
                case StepOutputFieldType.Results:
                    var results = new JsonObject();
                    foreach (var (_, value) in parts)
                        foreach (var (item, result) in value!.AsObject())
                            results[item] = result?.DeepClone();
                    values[field.Name] = results;
                    break;
                case StepOutputFieldType.PathList or StepOutputFieldType.StringList:
                    values[field.Name] = new JsonArray(parts.SelectMany(p => p.Value!.AsArray().Select(v => v!.GetValue<string>()))
                        .Distinct(StringComparer.Ordinal).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
                    break;
                case StepOutputFieldType.Text:
                    values[field.Name] = string.Join("\n\n", parts.Select(p => $"{p.Step.Title}: {p.Value!.GetValue<string>()}"));
                    break;
            }
        }
        return new StepOutput(joinNo, join.Title, Guid.NewGuid(), schema.Id, schema.Version, DateTimeOffset.UtcNow,
            values.ToJsonString(), [], 1, [$"joined from {handed.Count} step(s)"])
        {
            Items = handed.SelectMany(h => h.Output.Items ?? []).ToArray() is { Length: > 0 } items ? items : null
        };
    }
}

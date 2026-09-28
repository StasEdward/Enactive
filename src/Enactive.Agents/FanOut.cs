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
                steps[i] = steps[i] with { ForEach = null };
                continue;
            }
            if (!steps[i].DependsOn.Contains(steps[each.Step].Id))
                steps[i] = steps[i] with { DependsOn = [.. steps[i].DependsOn, steps[each.Step].Id] };
        }
        return (plan with { Steps = steps }, dropped);
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
               + (items.Count == 1 ? "it" : "these") + "; every other item has a step of its own.\n";
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

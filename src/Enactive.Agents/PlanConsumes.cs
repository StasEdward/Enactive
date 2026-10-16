namespace Enactive.Agents;

using Enactive.Core.Tasks;

/// <summary>
/// Whether every dependency on a step that hands on a list says what it does with the list (Plan Phase 1.1): consumes
/// it - and must then account for every item - or only waits for the step ("after"). Pure: it reads a plan and
/// returns one, with what it added (notes) and what the planner must put right (defects).
///
/// <para><b>Why a dependency must be classified.</b> A step that depended on "find the wiki pages" and summarised two of
/// twelve could end green: nothing said it was meant to handle the twelve. Read as "only waits", silence is the escape
/// from coverage; so silence is a defect, never a default. A forEach over the list is a consume by definition.</para>
///
/// <para>Steps are numbered from 1 in what it says, as in every diagnostic a person or the planner reads; consumes
/// name steps from 0, as plans do.</para>
/// </summary>
public static class PlanConsumes
{
    /// <param name="Plan">The plan, with a dependency added for each consume of a step the consumer did not wait for.</param>
    /// <param name="Notes">What was added, and why.</param>
    /// <param name="Defects">What the plan leaves unsaid or contradicts - for the planner to correct.</param>
    public sealed record Result(Plan Plan, IReadOnlyList<string> Notes, IReadOnlyList<string> Defects);

    public static Result Classify(Plan plan)
    {
        var steps = plan.Steps.ToArray();
        IReadOnlyList<StepOutputField> Lists(int at) => steps[at].Output?.Fields
            .Where(f => f.Type is StepOutputFieldType.PathList or StepOutputFieldType.StringList).ToArray() ?? [];

        // Without lists there is nothing to cover - and a declaration naming one cannot be honoured anyway: step
        // outputs are taken off a plan when they are off, and the consumes left behind would only be noise.
        if (!Enumerable.Range(0, steps.Length).Any(i => Lists(i).Count > 0))
            return new(plan, [], []);

        var notes = new List<string>();
        var defects = new List<string>();
        var changed = false;
        var position = steps.Select((s, i) => (s.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);

        bool Reaches(int from, int to)
        {
            var seen = new HashSet<int>();
            var pending = new Stack<int>([from]);
            while (pending.TryPop(out var at))
                foreach (var d in steps[at].DependsOn)
                    if (position.TryGetValue(d, out var i) && seen.Add(i))
                    {
                        if (i == to) return true;
                        pending.Push(i);
                    }
            return false;
        }

        for (var s = 0; s < steps.Length; s++)
        {
            // What this step consumes, kept only where it names an earlier step's declared list.
            var consumed = new List<(int Step, string Field)>();
            foreach (var c in steps[s].Consumes ?? [])
            {
                if (c.Step < 0 || c.Step >= s)
                {
                    defects.Add($"step {s + 1} consumes from step {c.Step + 1}, which does not come before it");
                    continue;
                }
                if (c.UnknownEvidence is { } word)
                    defects.Add($"step {s + 1} asks for '{word}' evidence of step {c.Step + 1}'s {c.Field}, which is not a kind "
                                + "the engine knows (mentioned, file_read, file_write, command)");
                if (steps[c.Step].Output?.Fields.FirstOrDefault(f => f.Name == c.Field) is not { } field)
                {
                    defects.Add($"step {s + 1} consumes step {c.Step + 1}'s '{c.Field}', which step {c.Step + 1} does not declare");
                    continue;
                }
                if (field.Type is not (StepOutputFieldType.PathList or StepOutputFieldType.StringList))
                {
                    defects.Add($"step {s + 1} consumes step {c.Step + 1}'s '{c.Field}', which is {StepOutputSchema.NameOf(field.Type)}, "
                                + "not a list (path[] or string[])");
                    continue;
                }
                consumed.Add((c.Step, c.Field));

                // A consumer that does not wait for its source reads a list that does not exist yet. The dependency
                // is added - and said: FanOut adds a forEach's silently, and a plan changed without a word is one
                // nobody can check against what the planner wrote.
                if (!Reaches(s, c.Step))
                {
                    steps[s] = steps[s] with { DependsOn = [.. steps[s].DependsOn, steps[c.Step].Id] };
                    changed = true;
                    notes.Add($"step {s + 1} consumes step {c.Step + 1}'s {c.Field} but did not depend on it: the dependency "
                              + $"was added, so step {s + 1} runs after the list exists");
                }
            }
            if (steps[s].ForEach is { } each && each.Step >= 0 && each.Step < s)
                consumed.Add((each.Step, each.Field));

            var orderOnly = (steps[s].OrderOnly ?? []).Where(position.ContainsKey).Select(id => position[id]).ToHashSet();
            foreach (var p in orderOnly.Where(p => consumed.Any(c => c.Step == p)).Order())
                defects.Add($"step {s + 1} both consumes step {p + 1}'s "
                            + string.Join(", ", consumed.Where(c => c.Step == p).Select(c => c.Field).Distinct())
                            + " and says it only waits for it (after): one or the other");

            // Every direct dependency on a step with lists: an order, or a consume of every list it hands on.
            foreach (var p in steps[s].DependsOn.Where(position.ContainsKey).Select(id => position[id]).Distinct())
            {
                if (p == s || orderOnly.Contains(p)) continue;    // a step on itself is a cycle, PlanValidation's to refuse
                var lists = Lists(p).Select(f => f.Name).ToArray();
                if (lists.Length == 0) continue;
                var taken = lists.Where(l => consumed.Contains((p, l))).ToArray();
                var left = lists.Except(taken).ToArray();
                if (left.Length == 0) continue;
                defects.Add(taken.Length == 0
                    ? $"step {s + 1} depends on step {p + 1}, which hands on the list(s) {string.Join(", ", left)}, without saying "
                      + "whether it consumes them (consumes) or only waits for it (after)"
                    : $"step {s + 1} consumes step {p + 1}'s {string.Join(", ", taken)} but not its {string.Join(", ", left)}: "
                      + "a step that takes data from another accounts for every list it hands on");
            }
        }

        return new(changed ? plan with { Steps = steps } : plan, notes, defects);
    }
}

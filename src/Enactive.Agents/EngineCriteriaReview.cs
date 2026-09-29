namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Templates;

/// <summary>
/// The criteria the engine decides itself, as the contract review sees them: in their own typed form, never as
/// commands, with where each file's name came from - named by the request, chosen by the plan, or handed on by a
/// step - and what is on disk there now. Run 68f92f: the review never saw "CoverageReport.md", a name the plan made
/// up while the request named no file, and the run failed on it beside "coverage-report.md".
///
/// <para>The review may keep a criterion, drop one the plan chose (saying why), correct a path the plan chose, or
/// move it to the file a step hands on. A name the request gave is the person's, and stays. One not answered for is
/// kept, as it was: nothing here can be lost by an answer that forgets it.</para>
/// </summary>
internal static class EngineCriteriaReview
{
    internal const string NamedByRequest = "named by the request";
    internal const string ChosenByPlan = "chosen by the plan";
    internal const int MaxShownThere = 8;

    internal const string Prompt =
        " engineCriteria are file criteria the ENGINE decides from the workspace - they are not commands; never restate one as a check. "
        + "provenance says where each file name came from. Return engine_criteria:[{id,decision,reason,path,path_from}] for any you change: "
        + "decision keep; drop (only one whose name the plan chose, with the reason); path (the corrected path, for a name the plan chose wrongly); "
        + "path_from {step,field} (the result's file is the one that step hands on in a path field of its output - for a result the request names no file for). "
        + "A step is named by its index in the plan's steps as shown, counted from 0 - the same as in path_from - and its title is beside it. "
        + "A criterion named by the request is kept as it is. One you do not return is kept.";

    private static string Norm(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    private static string Stem(string name)
        => new string(Path.GetFileNameWithoutExtension(name).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Where the file's name came from, as the engine can tell.</summary>
    internal static string Provenance(TypedCriterion typed, string request, Enactive.Core.Tasks.Plan? plan = null)
    {
        if (typed.PathFromStep is { } step)
            return $"handed on by the step at index {step}" + (TitleAt(plan, step) is { } title ? $" ('{title}')" : "")
                   + $" as '{typed.PathFromField}'";
        if (typed.Path is not { } path) return ChosenByPlan;
        var norm = Norm(path);
        var text = request.Replace('\\', '/');
        return text.Contains(norm, StringComparison.OrdinalIgnoreCase)
               || text.Contains(Path.GetFileName(norm), StringComparison.OrdinalIgnoreCase) ? NamedByRequest : ChosenByPlan;
    }

    /// <summary>The criteria as the review is shown them, with the engine's facts about each.</summary>
    private static string? TitleAt(Enactive.Core.Tasks.Plan? plan, int index)
        => plan is not null && index >= 0 && index < plan.Steps.Count ? plan.Steps[index].Title : null;

    // Run 148e77, 2026-09-29: the review was shown path_from {step:3} beside a list of steps with no numbers, took
    // it as counted from 1, and "corrected" it to a step that does not exist. The index and the title, together.
    internal static IReadOnlyList<object> Show(IReadOnlyList<SuccessCriterionDefinition> criteria, string request, string? root,
        Enactive.Core.Tasks.Plan? plan = null)
        => criteria.Select((c, i) =>
        {
            var t = c.Typed!;
            object? there = null;
            if (t.Path is { } path && root is not null && TypedCriteria.Inside(root, path) is { } full)
            {
                var dir = Path.GetDirectoryName(full)!;
                string[] files = [];
                try { if (Directory.Exists(dir)) files = Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var name = Path.GetFileName(full);
                there = new
                {
                    exists = File.Exists(full),
                    similar = files.Where(f => !f.Equals(name, StringComparison.Ordinal) && Stem(f) == Stem(name)).ToArray(),
                    sameKind = files.Where(f => Path.GetExtension(f).Equals(Path.GetExtension(name), StringComparison.OrdinalIgnoreCase)).Take(MaxShownThere).ToArray()
                };
            }
            return (object)new
            {
                id = $"E{i + 1}", kind = t.Kind.ToString(), t.Path,
                path_from = t.PathFromStep is { } s ? new { step = s, stepTitle = TitleAt(plan, s), field = t.PathFromField } : null,
                t.Text, t.NonEmpty, step = c.Step, stepTitle = c.Step is { } own ? TitleAt(plan, own) : null,
                provenance = Provenance(t, request, plan), onDiskNow = there
            };
        }).ToArray();

    /// <summary>
    /// The review's answer applied: every criterion comes back, as it was or as the review changed it within what
    /// it may change, and each change or refused change is said.
    /// </summary>
    internal static (IReadOnlyList<SuccessCriterionDefinition> Criteria, IReadOnlyList<string> Notes) Apply(
        IReadOnlyList<SuccessCriterionDefinition> criteria, string answer, string request, string? root, Enactive.Core.Tasks.Plan? plan)
    {
        var notes = new List<string>();
        var result = criteria.ToList();
        JsonElement decisions;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (!doc.RootElement.TryGetProperty("engine_criteria", out var list) || list.ValueKind != JsonValueKind.Array)
                return (result, notes);
            decisions = list.Clone();
        }
        catch (JsonException) { return (result, notes); }

        string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var decided = new HashSet<int>();
        foreach (var d in decisions.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object))
        {
            var id = Text(d, "id") ?? "";
            if (!id.StartsWith('E') || !int.TryParse(id[1..], out var n) || n < 1 || n > criteria.Count || !decided.Add(n - 1)) continue;
            var c = criteria[n - 1];
            var typed = c.Typed!;
            var decision = (Text(d, "decision") ?? "keep").Trim().ToLowerInvariant();
            var reason = Text(d, "reason") ?? "";
            if (decision == "keep") continue;
            if (typed.Kind is not (TypedCriterionKind.FileExists or TypedCriterionKind.FileContains))
            {
                notes.Add($"Kept '{c.Name}': the contract review asked to {decision} it, and only a file criterion can be changed there.");
                continue;
            }
            if (Provenance(typed, request) == NamedByRequest)
            {
                notes.Add($"Kept '{c.Name}': its file is named by the request, and the contract review asked to {decision} it ({reason}).");
                continue;
            }
            switch (decision)
            {
                case "drop" when !string.IsNullOrWhiteSpace(reason):
                    result[n - 1] = null!;
                    notes.Add($"Dropped by the contract review: '{c.Name}' - {reason}");
                    break;
                case "path" when Text(d, "path") is { } path && !string.IsNullOrWhiteSpace(path):
                    if (root is not null && TypedCriteria.Inside(root, path) is null)
                    {
                        notes.Add($"Kept '{c.Name}': the corrected path '{path}' is not inside the workspace.");
                        break;
                    }
                    result[n - 1] = TypedCriteria.Rebuild(c, typed with { Path = path, PathFromStep = null, PathFromField = null });
                    notes.Add($"Path corrected by the contract review: '{c.Name}' is now '{result[n - 1].Name}' - {reason}");
                    break;
                case "path_from":
                    int? step = d.TryGetProperty("path_from", out var from) && from.ValueKind == JsonValueKind.Object
                                && from.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sn) ? sn : null;
                    var field = from.ValueKind == JsonValueKind.Object ? Text(from, "field") : null;
                    if (TypedCriteria.HandedPathInvalid(step, field, plan) is { } invalid)
                    {
                        notes.Add($"Kept '{c.Name}': the contract review asked to check the file a step hands on, but {invalid}.");
                        break;
                    }
                    result[n - 1] = TypedCriteria.Rebuild(c, typed with { Path = null, PathFromStep = step, PathFromField = field });
                    notes.Add($"Moved by the contract review to the file a step hands on: '{c.Name}' is now '{result[n - 1].Name}' - {reason}");
                    break;
                default:
                    notes.Add($"Kept '{c.Name}': the contract review's answer for it ({decision}) was incomplete.");
                    break;
            }
        }
        return (result.Where(c => c is not null).ToArray(), notes);
    }
}

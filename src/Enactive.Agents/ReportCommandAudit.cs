namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Checks the reviewer's extraction of saved command-outcome assertions against the
/// journal. Selecting all relevant assertions and interpreting prose remain semantic review.</summary>
internal static class ReportCommandAudit
{
    internal static IReadOnlyList<string> Errors(string answer, EvidenceView evidence,
        ReviewSources sources, RequestObligations obligations)
    {
        var errors = new List<string>();
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(answer));
        if (json is null) return errors; // Schema validator owns missing/malformed fields.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return errors;
            var requested = new HashSet<int>();
            if (root.TryGetProperty("need_evidence", out var need) && need.ValueKind == JsonValueKind.Array)
                foreach (var value in need.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) requested.Add(n);
            if (!root.TryGetProperty("command_reports", out var reports) || reports.ValueKind != JsonValueKind.Array)
                return errors;
            var index = 0;
            foreach (var item in reports.EnumerateArray())
            {
                var path = $"$.command_reports[{index++}]";
                if (item.ValueKind != JsonValueKind.Object) continue;
                var sourceId = Text(item, "source_id");
                var source = sourceId is null ? null : sources.Find(sourceId);
                if (sourceId is not null && source is null)
                    errors.Add(path + ".source_id: source does not exist in the displayed registry; select a listed ID, not a path or invented file");
                if (source is not null)
                {
                    if (Text(item, "source_type") is { } kind && kind != source.Kind)
                        errors.Add(path + ".source_type: source " + source.Id + " requires " + source.Kind);
                    if (Text(item, "fragment_id") is { } fragment && source.Fragment(fragment) is null)
                        errors.Add(path + ".fragment_id: select an existing nonempty numbered fragment from this source");
                }
                if (item.TryGetProperty("call", out var call) && call.ValueKind == JsonValueKind.Number && call.TryGetInt32(out var id))
                {
                    if (!evidence.ContainsAction(id))
                        errors.Add(path + ".call: command call does not exist");
                    else if (!evidence.VisibleActionIds.Contains(id) && !requested.Contains(id))
                        errors.Add(path + ".call: call was not shown; request its evidence first");
                    else if (evidence.Cited(id) is { ExitCode: null })
                        errors.Add(path + ".call: cited action has no recorded command exit code");
                }
                if (Text(item, "scope") is { } scope && scope != "run"
                    && (!obligations.Scopes.ContainsKey(scope) || !scope.StartsWith('S') || !int.TryParse(scope.AsSpan(1), out _)))
                    errors.Add(path + ".scope: use run or a declared step scope");
                // Scope/occurrence are REVIEWER annotations, not words asserted by the worker.
                // Correct their mapping rather than blaming the source for a invented "first".
                if (Text(item, "scope") is { } mappedScope
                    && (mappedScope == "run" || (obligations.Scopes.ContainsKey(mappedScope)
                        && mappedScope.StartsWith('S') && int.TryParse(mappedScope.AsSpan(1), out _)))
                    && item.TryGetProperty("call", out var citation) && citation.ValueKind == JsonValueKind.Number
                    && citation.TryGetInt32(out var number) && evidence.Cited(number) is { } action)
                {
                    int? step = mappedScope == "run" ? null : int.Parse(mappedScope.AsSpan(1));
                    if (step is not null && action.Step != step)
                        errors.Add(path + ".scope: the cited command belongs to another step; correct the annotation");
                    if (Text(item, "occurrence") is "first" or "last")
                    {
                        var last = Text(item, "occurrence") == "last";
                        if (evidence.CommandOccurrence(number, step, last) is { } actual && actual != number)
                            errors.Add(path + $".occurrence: selected {(last ? "last" : "first")} command is call {actual}, not {number}"
                                + (evidence.VisibleActionIds.Contains(actual) ? ". " : $" (call {actual} is older than the calls you were shown). ")
                                + "Use specific unless the source actually asserts an initial/final occurrence. "
                                + "If the source is false, explain that as a verdict defect using the correct reference.");
                        if (!last && evidence.HasPriorTranscript)
                            errors.Add(path + ".occurrence: cannot establish first across unavailable pre-resume history");
                    }
                }
            }
        }
        catch (JsonException) { /* Reported by the schema validator. */ }
        return errors;
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string? Contradiction(string answer, EvidenceView evidence, ReviewSources sources)
    {
        using var doc = JsonDocument.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(answer))!);
        foreach (var item in doc.RootElement.GetProperty("command_reports").EnumerateArray())
        {
            var id = item.GetProperty("call").GetInt32();
            var action = evidence.Cited(id)!;
            var file = sources.Find(item.GetProperty("source_id").GetString()!)!.Label;
            var quote = sources.Find(item.GetProperty("source_id").GetString()!)!.Fragment(item.GetProperty("fragment_id").GetString()!);
            var exit = item.GetProperty("exit_code").GetInt32();
            var scope = item.GetProperty("scope").GetString()!;
            int? step = scope == "run" ? null : int.Parse(scope.AsSpan(1));
            if (action.ExitCode != exit || action.Outcome == ActionOutcome.Refused
                || (step is not null && action.Step != step))
                return $"{file}: assertion '{quote}' claims exit {exit} in {scope}, but call {id} "
                    + $"has exit {action.ExitCode?.ToString() ?? "unknown"}, outcome {action.Outcome}, step {action.Step}. Correct the source assertion, not the tests.";
        }
        return null;
    }
}


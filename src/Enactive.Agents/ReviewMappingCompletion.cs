namespace Enactive.Agents;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Enactive.Core.Providers;

/// <summary>Complete only omitted, engine-enumerated report mappings without rewriting accepted review sections.</summary>
internal sealed class ReviewMappingCompletion
{
    private readonly JsonObject original;
    private readonly HashSet<(string, string)> pending;
    public string Instruction { get; }
    internal static readonly string Schema = MakeSchema();

    private static string MakeSchema()
    {
        var full = JsonNode.Parse(Reviewer.CombinedWireSchema)!["properties"]!;
        return new JsonObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject {
                ["report_checks"] = full["report_checks"]!.DeepClone(),
                ["repairs"] = full["repairs"]!.DeepClone()
            }, ["required"] = new JsonArray("report_checks", "repairs")
        }.ToJsonString();
    }

    public ReviewMappingCompletion(string answer, ReviewReferences references)
    {
        original = JsonNode.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(answer))!)!.AsObject();
        var existing = original["report_checks"]!.AsArray()
            .Where(c => c!["kind"]!.GetValue<string>() == "requirement-map")
            .Select(c => (c!["source_id"]!.GetValue<string>(), c["fragment_id"]!.GetValue<string>())).ToHashSet();
        var missing = references.Visible.Where(e => e.Kind != "execution-evidence"
            && SemanticReviewAudit.Ids(e.Text).Count > 0 && !existing.Contains((e.SourceId, e.FragmentId))).ToArray();
        pending = missing.Select(e => (e.SourceId, e.FragmentId)).ToHashSet();
        Instruction = "Complete ONLY these missing requirement-map assessments. Return {report_checks:[...],repairs:[...]}. "
            + "Include exactly one report_check for each listed evidence_id; kind=requirement-map. "
            + "Judge each O-ID against its ORIGINAL source-unit meaning, not the worker's labels. "
            + "Do not repeat or rewrite previous assessments/claims. Each fail needs a concrete repair. "
            + "Repair findings use patch-local paths $.report_checks[0], $.report_checks[1], etc. "
            + "The engine merges these entries and reruns ALL validators; existing failures cannot be removed.\n"
            + System.Text.Json.JsonSerializer.Serialize(missing.Select(e => new {
                evidence_id = e.Id, source_type = e.Kind, source = e.Label, fragment = e.Text,
                obligation_ids = SemanticReviewAudit.Ids(e.Text)
            }));
    }

    public string Merge(string decodedPatch)
    {
        var patch = JsonNode.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(decodedPatch)) ?? "{}")!.AsObject();
        if (patch.Count != 2 || patch["report_checks"] is not JsonArray checks || patch["repairs"] is not JsonArray repairs)
            throw new FormatException("Mapping completion must contain only report_checks and repairs arrays.");
        var seen = new HashSet<(string, string)>();
        foreach (var check in checks)
        {
            if (check is not JsonObject item || item["kind"]?.GetValue<string>() != "requirement-map"
                || item["source_id"] is null || item["fragment_id"] is null)
                throw new FormatException("Mapping completion requires requirement-map entries with evidence_id.");
            var key = (item["source_id"]!.GetValue<string>(), item["fragment_id"]!.GetValue<string>());
            if (!pending.Contains(key) || !seen.Add(key))
                throw new FormatException("Mapping completion cites an unrequested or duplicate evidence_id.");
        }
        if (!seen.SetEquals(pending)) throw new FormatException("Mapping completion omitted requested evidence IDs.");
        var merged = original.DeepClone().AsObject();
        var target = merged["report_checks"]!.AsArray();
        var offset = target.Count;
        foreach (var check in checks) target.Add(check!.DeepClone());
        foreach (var repair in repairs)
        {
            var copy = repair?.DeepClone().AsObject() ?? throw new FormatException("Expected repair object.");
            if (copy["findings"] is not JsonArray findings) throw new FormatException("Repair findings must be an array.");
            for (var i = 0; i < findings.Count; i++)
            {
                var link = findings[i]?.GetValue<string>() ?? "";
                var match = Regex.Match(link, @"^\$\.report_checks\[(\d+)\]$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index >= checks.Count)
                    throw new FormatException("Mapping repair may reference only a patch-local report_checks entry.");
                findings[i] = $"$.report_checks[{offset + index}]";
            }
            merged["repairs"]!.AsArray().Add(copy);
        }
        return merged.ToJsonString();
    }
}

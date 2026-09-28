namespace Enactive.Agents;

using System.Text.Json.Nodes;
using Enactive.Core.Providers;

/// <summary>Per-review wire references. IDs bind to exact visible source text and never get reused.</summary>
internal sealed class ReviewReferences
{
    internal sealed record Entry(int Id, string SourceId, string FragmentId, string Kind, string Label, string Text);
    private readonly Dictionary<(string, string, string), Entry> assigned = [];
    private readonly Dictionary<int, Entry> visible = [];
    private int next;
    public void BeginView() => visible.Clear();
    public string Render(ReviewSource source)
    {
        return string.Join("\n", source.Fragments.Select((text, index) => {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var key = (source.Id, $"F{index + 1}", text);
            if (!assigned.TryGetValue(key, out var entry))
                assigned.Add(key, entry = new(++next, source.Id, key.Item2, source.Kind, source.Label, text));
            visible[entry.Id] = entry;
            return $"[evidence {entry.Id}] {text}";
        }));
    }
    internal IReadOnlyCollection<Entry> Visible => visible.Values;
    public string Describe() => "\nEvidence references: use evidence_id (integer) from [evidence N]. "
        + "Numbers identify exact displayed fragments, not journal call IDs or O-IDs. "
        + "Use evidence_id=0 ONLY for repairs with target=work; source targets require a displayed positive ID. "
        + "Source types and paths are resolved by the engine; do not return source_id/source_type/fragment_id.\n"
        + "Required requirement-map assessments (one per listed evidence_id, assess meaning against ORIGINAL O-IDs):\n"
        + System.Text.Json.JsonSerializer.Serialize(visible.Values.Where(e => e.Kind != "execution-evidence")
            .Select(e => new { evidence_id = e.Id, obligation_ids = SemanticReviewAudit.Ids(e.Text) })
            .Where(e => e.obligation_ids.Count > 0));

    public string Diagnostic(string message)
    {
        foreach (var e in visible.Values.OrderByDescending(e => (e.SourceId + "/" + e.FragmentId).Length))
            message = message.Replace(e.SourceId + "/" + e.FragmentId, "evidence_id=" + e.Id, StringComparison.Ordinal);
        return message;
    }

    // Keep internal validators and persisted domain references unchanged. Legacy references remain
    // readable for existing callers, but cannot be mixed with a numeric reference in one object.
    public string Decode(string answer)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(answer));
        if (json is null) return answer;
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return answer; }
        void Visit(JsonNode? node, string path)
        {
            if (node is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("evidence_id", out var value))
                {
                    if (obj.ContainsKey("source_id") || obj.ContainsKey("fragment_id") || obj.ContainsKey("source_type"))
                        throw new FormatException(path + ": do not mix evidence_id with legacy references.");
                    if (value is not JsonValue number || !number.TryGetValue<int>(out var id))
                        throw new FormatException(path + ".evidence_id: use an integer.");
                    obj.Remove("evidence_id");
                    if (id == 0 && path.StartsWith("$.repairs[", StringComparison.Ordinal)
                        && obj["target"]?.GetValue<string>() == "work")
                    { obj["source_id"] = ""; obj["fragment_id"] = ""; }
                    else if (visible.TryGetValue(id, out var e))
                    {
                        obj["source_id"] = e.SourceId; obj["fragment_id"] = e.FragmentId;
                        if (path.StartsWith("$.command_reports[", StringComparison.Ordinal)) obj["source_type"] = e.Kind;
                    }
                    else throw new FormatException(path + $".evidence_id: {id} is not in the current displayed evidence.");
                }
                foreach (var child in obj.ToArray()) Visit(child.Value, path + "." + child.Key);
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Visit(array[i], path + $"[{i}]");
        }
        Visit(root, "$");
        return root!.ToJsonString();
    }

    internal static string WireSchema(string fullSchema)
    {
        var root = JsonNode.Parse(fullSchema)!;
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["properties"] is JsonObject properties && properties.ContainsKey("source_id")
                    && properties.ContainsKey("fragment_id"))
                {
                    properties.Remove("source_id"); properties.Remove("fragment_id"); properties.Remove("source_type");
                    properties["evidence_id"] = new JsonObject { ["type"] = "integer" };
                    var required = obj["required"]!.AsArray();
                    for (var i = required.Count - 1; i >= 0; i--)
                        if (required[i]!.GetValue<string>() is "source_id" or "fragment_id" or "source_type") required.RemoveAt(i);
                    required.Add("evidence_id");
                }
                foreach (var child in obj.ToArray()) Visit(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(root);
        return root.ToJsonString();
    }
}

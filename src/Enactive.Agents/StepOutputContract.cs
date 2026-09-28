namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;

/// <summary>
/// What one step has handed on so far: the last accepted submission, how many there have been, and
/// the last refused one (so the same refusal sent again is recognised - amendment C.5). Kept across
/// the step's retries, like its transcript.
/// </summary>
internal sealed class StepOutputSlot
{
    public JsonObject? Values { get; private set; }
    public IReadOnlyList<int> Evidence { get; private set; } = [];
    public IReadOnlyList<string> Notes { get; private set; } = [];
    public int Revision { get; private set; }
    public DateTimeOffset At { get; private set; }
    public Guid AttemptId { get; private set; }
    public string? LastRefused { get; set; }
    public bool Nudged { get; set; }

    public void Accept(StepOutputContract.Verdict verdict)
    {
        Values = verdict.Values;
        Evidence = verdict.Evidence;
        Notes = verdict.Notes;
        Revision++;
        At = DateTimeOffset.UtcNow;
        AttemptId = Guid.NewGuid();
    }

    /// <summary>The step's output with its provenance (Phase 2.3), or null when it handed nothing on.</summary>
    public StepOutput? Build(int stepNo, string title, StepOutputSchema? schema)
        => schema is null || Values is null
            ? null
            : new StepOutput(stepNo, title, AttemptId, schema.Id, schema.Version, At, Values.ToJsonString(), Evidence, Revision, Notes);
}

/// <summary>
/// <c>submit_step_output</c>: how a step hands its result on as values rather than prose (Phase 2).
///
/// <para><b>One contract, one validator (amendment C.1).</b> The tool's description and its JSON
/// schema are both made here, from the same <see cref="StepOutputSchema"/> the engine checks the
/// call against. There is no second list of what is required that could disagree with the first:
/// on 2026-09-20 01:10 an admission check said "references, not claims" while the engine demanded
/// claims, and a model alternated between the two twenty-eight times.</para>
///
/// <para><b>Limits in words (C.6).</b> Local grammar back-ends strip maxLength and maxItems from a
/// schema on the wire, so every limit the engine applies is written into the description, where
/// the model reads it. The limits themselves are the work's, declared with the schema (C.7).</para>
/// </summary>
internal static class StepOutputContract
{
    internal const string ToolName = "submit_step_output";

    /// <summary>The tool as the model sees it, made from the schema it will be checked against.</summary>
    internal static ToolDefinition Tool(StepOutputSchema schema)
    {
        var properties = new JsonObject();
        foreach (var field in schema.Fields)
            properties[field.Name] = WireType(field);
        properties["evidence"] = new JsonObject
        {
            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" },
            ["description"] = "Optional: the numbers [n] of the calls that show this result."
        };
        var json = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(schema.Fields.Where(f => f.Required).Select(f => (JsonNode)f.Name).ToArray()),
            ["additionalProperties"] = false
        };

        var fields = string.Join("; ", schema.Fields.Select(Describe));
        return new ToolDefinition(ToolName,
            "Hand over this step's result as values, for the steps after it - they receive these values, not your "
            + $"closing message. Fields: {fields}. Optionally evidence: the numbers [n] of the calls that show it. "
            + "Call it when the step's work is done; call it again to correct it - the last accepted submission counts.",
            json.ToJsonString(), WorkspaceEffect: WorkspaceEffect.None, Kind: ToolKind.Unknown);
    }

    private static string Describe(StepOutputField field)
    {
        var limit = field switch
        {
            { Type: StepOutputFieldType.Text, MaxLength: { } n } => $", up to {n} characters - longer is cut",
            { MaxLength: { } n } => $", at most {n} characters",
            { MaxItems: { } n } => $", at most {n} items",
            _ => ""
        };
        var paths = field.Type is StepOutputFieldType.Path or StepOutputFieldType.PathList
            ? ", workspace-relative, must exist" : "";
        return $"{field.Name} ({StepOutputSchema.NameOf(field.Type)}, {(field.Required ? "required" : "optional")}{paths}{limit}): "
               + field.Description;
    }

    private static JsonObject WireType(StepOutputField field)
    {
        JsonObject Of(string type) => new() { ["type"] = type, ["description"] = field.Description };
        return field.Type switch
        {
            StepOutputFieldType.Integer => Of("integer"),
            StepOutputFieldType.Boolean => Of("boolean"),
            StepOutputFieldType.PathList or StepOutputFieldType.StringList => new JsonObject
            {
                ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = field.Description
            },
            _ => Of("string")
        };
    }

    /// <summary>What checking a submission found: the values it would store, or what is wrong with it.</summary>
    internal sealed record Verdict(JsonObject? Values, IReadOnlyList<int> Evidence, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes)
    {
        public bool Accepted => Values is not null && Errors.Count == 0;
    }

    /// <summary>
    /// Checks one submission against the schema.
    ///
    /// <para>What it refuses, it names - the fields that are missing or wrong, not a list of
    /// everything required (C.2): a list that does not contain what was just sent reads as an
    /// instruction to take it out. What it can repair without losing anything, it repairs and says
    /// so: prose over its limit is cut and marked (C.4) - refusing it lost a page and sixteen
    /// checked statements on 2026-09-20 02:05. An empty field is still refused; it hands nothing on.
    /// Evidence is optional (C.3): a result that names its calls inside its text has said so already.</para>
    /// </summary>
    /// <param name="exists">Whether a workspace-relative path exists - on disk, or staged by this run.</param>
    /// <param name="callExists">Whether a call number exists in this step's evidence.</param>
    internal static Verdict Check(StepOutputSchema schema, string argumentsJson, Func<string, bool> exists, Func<int, bool> callExists)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        JsonObject submitted;
        try
        {
            submitted = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject
                        ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return new(null, [], ["The arguments are not a JSON object. Send the fields as one object."], []);
        }

        var known = schema.Fields.Select(f => f.Name).Append("evidence").ToHashSet(StringComparer.Ordinal);
        foreach (var name in submitted.Select(p => p.Key).Where(k => !known.Contains(k)))
            errors.Add($"'{name}' is not a field of this step's output (its fields: {string.Join(", ", schema.Fields.Select(f => f.Name))}).");

        var missing = schema.Fields.Where(f => f.Required && submitted[f.Name] is null).Select(f => f.Name).ToArray();
        if (missing.Length > 0)
            errors.Add($"Missing: {string.Join(", ", missing)}. Add {(missing.Length == 1 ? "it" : "them")} and send again, keeping what you already sent.");

        var values = new JsonObject();
        foreach (var field in schema.Fields)
            if (submitted[field.Name] is { } value && Field(field, value, exists, errors, notes) is { } kept)
                values[field.Name] = kept;

        var evidence = new List<int>();
        if (submitted["evidence"] is { } cited)
        {
            if (cited is not JsonArray list || list.Any(i => i is not JsonValue v || !v.TryGetValue<int>(out _)))
                errors.Add("evidence: expected a list of call numbers.");
            else
                foreach (var id in list.Select(i => i!.GetValue<int>()))
                    if (callExists(id)) evidence.Add(id);
                    else errors.Add($"evidence: call {id} does not exist - cite the [n] that begins a call.");
        }

        return errors.Count > 0 ? new(null, [], errors, notes) : new(values, evidence, [], notes);
    }

    private static JsonNode? Field(StepOutputField field, JsonNode value, Func<string, bool> exists,
        List<string> errors, List<string> notes)
    {
        string? Text()
        {
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) { errors.Add($"{field.Name}: expected text."); return null; }
            if (string.IsNullOrWhiteSpace(s)) { errors.Add($"{field.Name} is empty - an empty field hands nothing on."); return null; }
            return s;
        }

        string[]? List()
        {
            if (value is not JsonArray array || array.Any(i => i is not JsonValue v || !v.TryGetValue<string>(out _)))
            {
                errors.Add($"{field.Name}: expected a list of strings.");
                return null;
            }
            var items = array.Select(i => i!.GetValue<string>()).ToArray();
            if (field.MaxItems is { } max && items.Length > max)
            {
                // A list is not cut: which items to drop is the work's decision, not the engine's.
                errors.Add($"{field.Name}: {items.Length} items, and this step's output takes at most {max}.");
                return null;
            }
            return items;
        }

        switch (field.Type)
        {
            case StepOutputFieldType.Text:
                if (Text() is not { } text) return null;
                if (field.MaxLength is { } limit && text.Length > limit)
                {
                    notes.Add($"{field.Name} was cut to {limit} of its {text.Length} characters.");
                    text = text[..limit] + $" … [cut by the engine: {text.Length - limit} characters over this field's limit of {limit}]";
                }
                return JsonValue.Create(text);

            case StepOutputFieldType.String:
                if (Text() is not { } s) return null;
                if (field.MaxLength is { } max && s.Length > max)
                {
                    errors.Add($"{field.Name}: {s.Length} characters, and it takes at most {max}.");
                    return null;
                }
                return JsonValue.Create(s);

            case StepOutputFieldType.Integer:
                if (value is JsonValue n && n.TryGetValue<long>(out var number)) return JsonValue.Create(number);
                errors.Add($"{field.Name}: expected a whole number.");
                return null;

            case StepOutputFieldType.Boolean:
                if (value is JsonValue b && b.TryGetValue<bool>(out var flag)) return JsonValue.Create(flag);
                errors.Add($"{field.Name}: expected true or false.");
                return null;

            case StepOutputFieldType.Path:
                if (Text() is not { } path) return null;
                if (!exists(path)) { errors.Add($"{field.Name}: '{path}' does not exist in the workspace."); return null; }
                return JsonValue.Create(path);

            case StepOutputFieldType.PathList:
                if (List() is not { } paths) return null;
                var absent = paths.Where(p => !exists(p)).ToArray();
                if (absent.Length > 0)
                {
                    errors.Add($"{field.Name}: not in the workspace: {string.Join(", ", absent.Take(5))}"
                               + (absent.Length > 5 ? $", and {absent.Length - 5} more" : "") + ".");
                    return null;
                }
                return new JsonArray(paths.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());

            default:
                return List() is { } strings ? new JsonArray(strings.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()) : null;
        }
    }

    /// <summary>What the steps after this one are shown: the accepted values, as data.</summary>
    internal static string ForDependents(IEnumerable<StepOutput> outputs)
        => "Results handed on by the steps this one depends on - values checked by the engine; use them, "
           + "not a retelling of them:\n"
           + string.Join("\n", outputs.Select(o => $"- step {o.StepNo} \"{o.Step}\": {o.ValuesJson}"));
}

namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Collects response-contract errors; does not judge evidence or request a different verdict.</summary>
internal static class CombinedReviewValidation
{
    private static readonly JsonElement Schema = LoadSchema();

    private static JsonElement LoadSchema()
    {
        using var document = JsonDocument.Parse(Reviewer.CombinedSchema);
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<string> Errors(string answer, RequestObligations obligations, EvidenceView evidence)
    {
        var errors = new List<string>();
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(answer));
        // What broke, when an object was plainly meant - not a list of fields "missing" from a
        // fragment of it (see ModelText.ExtractJsonObject, 2026-09-28 11:01).
        if (json is null)
            return [ModelText.JsonProblem(ModelText.StripThink(answer)) is { } broken
                ? "$: " + broken + ". Return the whole object again, complete and valid."
                : "$: expected a combined JSON object"];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Validate(root, Schema, "$", errors);
            if (root.ValueKind != JsonValueKind.Object) return errors;
            if (Text(root, "verdict") == "fail" && string.IsNullOrWhiteSpace(Text(root, "notes")))
                errors.Add("$.notes: a rejection must explain the defect or unmet requirement");
            var requested = new HashSet<int>();
            if (root.TryGetProperty("need_evidence", out var need) && need.ValueKind == JsonValueKind.Array)
            {
                if (need.GetArrayLength() > 4)
                    errors.Add("$.need_evidence: at most four call IDs are allowed");
                var neededIndex = 0;
                foreach (var item in need.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var id))
                    {
                        requested.Add(id);
                        if (!evidence.ContainsAction(id))
                            errors.Add($"$.need_evidence[{neededIndex}]: call {id} does not exist");
                    }
                    neededIndex++;
                }
            }
            if (root.TryGetProperty("proof", out var proof))
                CheckCitations(proof, "$.proof", evidence, requested, errors);
            if (!root.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                return errors;

            var expected = obligations.Items.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var claim in claims.EnumerateArray())
            {
                var path = $"$.claims[{index++}]";
                if (claim.ValueKind != JsonValueKind.Object) continue;
                CheckCitations(claim, path, evidence, requested, errors);
                if (Text(claim, "id") is { } id)
                {
                    if (!expected.Contains(id)) errors.Add($"{path}.id: unknown obligation ID '{id}'");
                    if (!seen.Add(id)) errors.Add($"{path}.id: duplicate obligation ID '{id}'");
                }
                var claimId = Text(claim, "id") ?? "";
                var effectiveScope = EffectiveScope(claim, claimId, obligations);
                CheckScope(claim, path, claimId, obligations, errors);
                if (!claim.TryGetProperty("requirements", out var parts) || parts.ValueKind != JsonValueKind.Array)
                    continue;
                if (parts.GetArrayLength() == 0)
                {
                    if (effectiveScope != obligations.CurrentScope)
                        errors.Add($"{path}.scope: context-only source units must use current scope '{obligations.CurrentScope}'");
                    if (Text(claim, "shown") != "not-by-any-call")
                        errors.Add($"{path}.shown: context-only source units require not-by-any-call");
                    EmptyCalls(claim, path, errors);
                }
                var partIndex = 0;
                foreach (var part in parts.EnumerateArray())
                {
                    var partPath = $"{path}.requirements[{partIndex++}]";
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    CheckCitations(part, partPath, evidence, requested, errors);
                    CheckScope(part, partPath, claimId, obligations, errors);
                    var scope = Text(part, "scope");
                    if (part.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.True
                        && scope != obligations.CurrentScope)
                        errors.Add($"{partPath}.scope: a global constraint must use current scope '{obligations.CurrentScope}'");
                    if (scope == obligations.CurrentScope && effectiveScope != obligations.CurrentScope)
                        errors.Add($"{path}.scope: contains current-step requirements and cannot be deferred wholesale");
                }
                var currentParts = parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object
                    && EffectiveScope(part, claimId, obligations) == obligations.CurrentScope).ToArray();
                if (effectiveScope == obligations.CurrentScope && Text(claim, "shown") == "no"
                    && currentParts.Length > 0
                    && currentParts.All(part => Text(part, "shown") is "yes" or "not-by-any-call" or "expected-failure"))
                    errors.Add($"{path}.shown: contradicts satisfied current-scope requirements; deferred parts do not fail "
                        + "this step. Reconcile the aggregate with its parts, or identify the actual unmet current requirement.");
            }
            foreach (var id in expected.Where(id => !seen.Contains(id)))
                errors.Add($"$.claims: missing obligation ID '{id}'");
        }
        catch (JsonException ex) { errors.Add($"{ex.Path ?? "$"}: invalid JSON"); }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void CheckCitations(JsonElement claim, string path, EvidenceView evidence,
        HashSet<int> requested, List<string> errors)
    {
        if (claim.ValueKind != JsonValueKind.Object || !claim.TryGetProperty("calls", out var calls)
            || calls.ValueKind != JsonValueKind.Array) return;
        if (calls.GetArrayLength() == 0 && Text(claim, "shown") is "yes" or "nothing-to-do" or "expected-failure")
            errors.Add($"{path}.calls: this claim requires evidence IDs; cite visible evidence or report what is not shown");
        var index = 0;
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind == JsonValueKind.Number && call.TryGetInt32(out var id))
            {
                if (!evidence.ContainsAction(id))
                    errors.Add($"{path}.calls[{index}]: call {id} does not exist; cite the [n] that begins a call, not an [evidence N] line label");
                else if (!evidence.VisibleActionIds.Contains(id) && !requested.Contains(id))
                    errors.Add($"{path}.calls[{index}]: call {id} was not shown; request it through need_evidence before citing it");
                else if (Text(claim, "shown") == "expected-failure" && evidence.Cited(id) is { } action
                    && !ProofAudit.HasRecordedNonzeroExit(action))
                    errors.Add($"{path}.calls[{index}]: call {id} ({action.Tool}, {action.Outcome}, "
                        + $"exit={action.ExitCode?.ToString() ?? "not recorded"}) cannot support expected-failure. "
                        + "Every citation for this claim must have a recorded nonzero process exit and must not be refused. "
                        + "Separate setup/restoration evidence from the negative test. If the required failure was not observed, "
                        + "report the unmet requirement rather than inventing evidence.");
            }
            index++;
        }
    }

    private static string? EffectiveScope(JsonElement claim, string id, RequestObligations obligations)
    {
        var scope = Text(claim, "scope");
        var kind = Text(claim, "shown") switch
        {
            "yes" => ProofClaimKind.Shown,
            "expected-failure" => ProofClaimKind.ExpectedFailure,
            "not-by-any-call" => ProofClaimKind.NotByAnyCall,
            _ => ProofClaimKind.NotShown
        };
        return scope is null ? null : obligations.EvidenceScope(id, scope, kind);
    }

    private static void CheckScope(JsonElement claim, string path, string id, RequestObligations obligations, List<string> errors)
    {
        if (Text(claim, "scope") is not { } scope) return;
        if (!obligations.Scopes.ContainsKey(scope))
        {
            errors.Add($"{path}.scope: unknown scope '{scope}'; allowed: {string.Join(", ", obligations.Scopes.Keys)}");
            return;
        }
        if (EffectiveScope(claim, id, obligations) == obligations.CurrentScope) return;
        if (obligations.FinalReview)
        {
            errors.Add($"{path}.scope: final review requires scope run; no requirement can remain deferred");
            return;
        }
        if (Text(claim, "shown") != "no")
            errors.Add($"{path}.shown: another step's obligation must be explicitly deferred with shown=no, not claimed completed");
        EmptyCalls(claim, path, errors);
    }

    private static void EmptyCalls(JsonElement claim, string path, List<string> errors)
    {
        if (claim.TryGetProperty("calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() != 0)
            errors.Add($"{path}.calls: must be empty for a deferral or context-only source unit");
    }

    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    // Only the schema vocabulary used by CombinedSchema; schema and diagnostics share field definitions.
    private static void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var type = schema.GetProperty("type").GetString();
        var valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => throw new InvalidOperationException("Unsupported combined-review schema type: " + type)
        };
        if (!valid) { errors.Add($"{path}: expected {type}"); return; }
        if (schema.TryGetProperty("enum", out var choices)
            && !choices.EnumerateArray().Any(c => c.GetString() == value.GetString()))
            errors.Add($"{path}: expected one of {string.Join(", ", choices.EnumerateArray().Select(c => c.GetString()))}");
        if (type == "string" && (path.EndsWith(".what", StringComparison.Ordinal) || path.EndsWith(".requirement", StringComparison.Ordinal)
            || path.EndsWith(".reason", StringComparison.Ordinal))
            && string.IsNullOrWhiteSpace(value.GetString()))
            errors.Add($"{path}: must not be blank");
        if (type == "object")
        {
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                if (!value.TryGetProperty(required.GetString()!, out _))
                    errors.Add($"{path}.{required.GetString()}: required field is missing");
            var properties = schema.GetProperty("properties");
            foreach (var property in value.EnumerateObject())
            {
                if (properties.TryGetProperty(property.Name, out var child))
                    Validate(property.Value, child, path + "." + property.Name, errors);
                else errors.Add($"{path}.{property.Name}: unknown field");
            }
        }
        if (type == "array")
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                Validate(item, schema.GetProperty("items"), $"{path}[{index++}]", errors);
        }
    }
}

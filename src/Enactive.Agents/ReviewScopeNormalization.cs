namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Derives aggregate bookkeeping from explicit parts. Never changes a verdict, a current
/// unmet part, evidence IDs on parts, or a claim that another step actually completed work.</summary>
internal static class ReviewScopeNormalization
{
    internal static string Apply(string answer, RequestObligations obligations)
    {
        if (obligations.FinalReview) return answer;
        try
        {
            var json = ModelText.ExtractJsonObject(ModelText.StripThink(answer));
            if (json is null || JsonNode.Parse(json) is not JsonObject root
                || Text(root, "verdict") != "pass" || root["claims"] is not JsonArray claims) return answer;
            foreach (var claim in claims.OfType<JsonObject>())
            {
                if (claim["requirements"] is not JsonArray parts) continue;
                var id = Text(claim, "id") ?? "";
                NormalizeDeferral(claim, id);
                foreach (var part in parts.OfType<JsonObject>()) NormalizeDeferral(part, id);
                var current = parts.OfType<JsonObject>().Where(p => Effective(p, id) == obligations.CurrentScope).ToArray();
                if (Effective(claim, id) != obligations.CurrentScope || Text(claim, "shown") != "no"
                    || current.Length == 0 || current.Any(p => Text(p, "shown") is not ("yes" or "not-by-any-call" or "expected-failure"))) continue;
                var calls = current.SelectMany(p => p["calls"] is JsonArray a ? a : new JsonArray())
                    .Select(n => n!.GetValue<int>()).Distinct().ToArray();
                claim["shown"] = current.Any(p => Text(p, "shown") == "yes") ? "yes"
                    : current.Any(p => Text(p, "shown") == "expected-failure") ? "expected-failure" : "not-by-any-call";
                claim["calls"] = new JsonArray(calls.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
                claim["what"] = "Aggregate of current-scope assessments: "
                    + string.Join("; ", current.Select(p => Text(p, "what")));
            }
            return root.ToJsonString();

            string? Effective(JsonObject node, string id) => Text(node, "scope") is { } scope
                ? obligations.EvidenceScope(id, scope, Text(node, "shown") switch {
                    "yes" => ProofClaimKind.Shown, "expected-failure" => ProofClaimKind.ExpectedFailure,
                    "not-by-any-call" => ProofClaimKind.NotByAnyCall, _ => ProofClaimKind.NotShown }) : null;
            void NormalizeDeferral(JsonObject node, string id)
            {
                var scope = Text(node, "scope");
                if (scope is not null && obligations.Scopes.ContainsKey(scope) && scope != obligations.CurrentScope
                    && Text(node, "shown") == "not-by-any-call" && node["calls"] is JsonArray { Count: 0 }
                    && node["global"]?.ToJsonString() != "true"
                    && Effective(node, id) != obligations.CurrentScope)
                    node["shown"] = "no";
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException)
        { return answer; } // The schema validator reports malformed fields without hiding them.
    }

    private static string? Text(JsonObject value, string key)
        => value[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}

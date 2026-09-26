namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

public sealed partial class Reviewer
{
    internal static readonly string CombinedSchema = MakeCombinedSchema();

    private static string MakeCombinedSchema()
    {
        var schema = JsonNode.Parse(VerdictSchema)!.AsObject();
        var properties = schema["properties"]!.AsObject();
        properties["proof"] = JsonNode.Parse(ProofSchema);
        var claim = JsonNode.Parse(ProofSchema)!.AsObject();
        // nothing-to-do is a statement about the whole step, not an individual constraint.
        claim["properties"]!["shown"]!["enum"] = new JsonArray("yes", "no", "not-by-any-call", "expected-failure");
        claim["properties"]!["id"] = new JsonObject { ["type"] = "string" };
        claim["properties"]!["scope"] = new JsonObject { ["type"] = "string" };
        claim["required"]!.AsArray().Add("id");
        claim["required"]!.AsArray().Add("scope");
        var requirement = JsonNode.Parse(ProofSchema)!.AsObject();
        requirement["properties"]!["shown"]!["enum"] = new JsonArray("yes", "no", "not-by-any-call", "expected-failure");
        requirement["properties"]!["prohibitions"] = new JsonObject {
            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("file-deletion") }
        };
        requirement["properties"]!["requirement"] = new JsonObject { ["type"] = "string" };
        requirement["properties"]!["scope"] = new JsonObject { ["type"] = "string" };
        requirement["properties"]!["global"] = new JsonObject { ["type"] = "boolean" };
        foreach (var name in new[] { "requirement", "scope", "global", "prohibitions" }) requirement["required"]!.AsArray().Add(name);
        claim["properties"]!["requirements"] = new JsonObject { ["type"] = "array", ["items"] = requirement };
        claim["required"]!.AsArray().Add("requirements");
        properties["claims"] = new JsonObject { ["type"] = "array", ["items"] = claim };
        // Anthropic's structured-output subset rejects maxItems. Enforce the limit when parsing.
        properties["need_evidence"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } };
        foreach (var name in new[] { "proof", "claims", "need_evidence" }) schema["required"]!.AsArray().Add(name);
        return schema.ToJsonString();
    }

    /// <summary>One normal call for truth and proof; at most one clarification/evidence retrieval.</summary>
    public async Task<ReviewResult> ReviewWithProofAsync(
        string title, string report, EvidenceView evidence, IReadOnlyList<string> artifacts,
        IReadOnlyList<WrittenFile> files, RequestObligations obligations,
        IChatProvider provider, string model, CancellationToken ct, string? workspaceRoot = null,
        Func<int, int, string?>? beforeRetry = null, ReviewMode mode = ReviewMode.Execution)
    {
        var instruction = (mode == ReviewMode.Content ? ContentGuidance : ExecutionGuidance)
            + "\nIndependently assess whether the evidence shows the objective was met: " + ProofGuidance
            + "\nReturn ONLY one JSON object with verdict, notes, proof, claims, need_evidence. "
            + "proof is {shown,calls,what} for the step objective. claims contains {id,scope,shown,calls,what} "
            + "for EVERY request obligation ID exactly once. Each claim also requires requirements: an array of "
            + "{requirement,scope,global,shown,calls,what}. Decompose the source unit by MEANING in the context of "
            + "the entire original request, not by line count. Include every independently testable requirement; "
            + "a continued sentence/code block may span source IDs: explain the connection without inventing obligations. "
            + "Headings and context-only units have requirements=[], shown=not-by-any-call and an explanation. "
            + "For each requirement state its meaning, responsible scope and evidence separately. Global constraints "
            + "have global=true and MUST be checked in the current scope, never deferred. Mixed-scope source units "
            + "remain in the current scope when any part applies here; defer only individual other-step requirements. "
            + "The aggregate claim must cover all current-scope parts; no successful build stands in for a mutation check. "
            + "If all current-scope parts are satisfied, do not mark the aggregate shown=no merely because other parts "
            + "are deferred. Use yes with their evidence or not-by-any-call for a content assessment. "
            + "nothing-to-do is allowed ONLY in the top-level proof of the whole step. For an individual prohibition "
            + "such as no network/git/deletion, assess whether it was respected using yes and evidence (or "
            + "not-by-any-call with a content explanation), never nothing-to-do. Allowed file writes do not violate "
            + "an unrelated prohibition. If a current requirement is unmet, name that requirement and use shown=no. "
            + "Each requirement includes prohibitions: use [\"file-deletion\"] for an explicit unconditional ban on "
            + "deleting files in the original request, otherwise []. Extract this restriction independently of "
            + "whether you believe the worker's actions were useful. Do not narrow a blanket ban to infrastructure "
            + "or important files. Temporary deletion followed by restoration and scratch cleanup are still deletion. "
            + "Tool approval and autonomy authorize tool access, never waive request constraints. "
            + "Check other restrictions semantically as well; an empty prohibitions array is not evidence of compliance. "
            + "For requested negative/mutation tests use expected-failure on the specific requirement when a "
            + "recorded nonzero exit and the actual failing assertions demonstrate the requested defect. This remains "
            + "valid when the worker forgot expectedExitCodes; never rerun a completed negative test just to declare it. "
            + "Explain the required negative behavior and actual matching failure in what. A compile error, crash, "
            + "or unrelated failing assertion does not prove the mutation was detected. Cite restored passing tests "
            + "separately with yes; the aggregate must not substitute them for the negative-test requirement. "
            + "Judge ALL requirements inside each source unit, "
            + "especially each file/test and exact commands; one successful command does not establish universal coverage. "
            + "For each/every requirements, enumerate the relevant items and their supporting evidence in what; "
            + "if the inventory or any item's evidence is missing, do not claim universal coverage. "
            + "Use the current scope for its obligations. If an obligation belongs solely to another listed step, "
            + "use that scope, shown=no, calls=[], and explain the deferral in what. Do not defer a constraint that applies here. "
            + "When a shared requirement-to-step map is provided, keep that assignment. A requirement assigned to both "
            + "implementation and testing can be evidenced again by tests in the current step: that verifies earlier work, "
            + "it does not claim another step was executed here. Prefer current scope for this verification. "
            + "For content/analysis use not-by-any-call only after assessing the actual content. "
            + "If more evidence is needed, put at most four original call IDs in need_evidence; otherwise []. "
            + "Never cite a hidden call until its requested evidence has been displayed. Omitted evidence is unknown, not proof of absence. "
            + "If still uncertain after clarification, verdict=fail and explain the missing evidence. "
            + "A plain pass without both proof and obligation claims is incomplete.";
        string Prompt(EvidenceView view) => BuildExecutionUserPrompt(title, report, view.Text, artifacts, files)
            + obligations.Describe();
        var messages = new List<ChatMessage> { ChatMessage.System(instruction), ChatMessage.User(Prompt(evidence)) };
        var prompt = 0;
        var output = 0;
        int? cached = null, created = null;
        string problem = "reviewer did not return a combined verdict and proof";
        var outputTruncated = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0 && beforeRetry?.Invoke(prompt, output) is { } spent)
                return new(false, spent, prompt, output) { CachedPromptTokens = cached, CacheCreationPromptTokens = created, BudgetExhausted = spent };
            ChatCompletion completion;
            try
            {
                var request = new ChatRequest(model, messages, Temperature: 0,
                    ResponseSchema: CombinedSchema, Purpose: GenerationPurpose.Review,
                    OutputTokenLimit: (int)Math.Min(32768L, (2048L + 256L * obligations.Items.Count) * (outputTruncated ? 2 : 1)));
                completion = await provider.CompleteAsync(GenerationAllowance.Fit(request, provider), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new(false, "review error: " + ex.Message, prompt, output)
                    { CachedPromptTokens = cached, CacheCreationPromptTokens = created, IncompleteReason = "review error: " + ex.Message };
            }
            prompt += completion.PromptTokens ?? 0;
            output += completion.CompletionTokens ?? 0;
            cached = TokenCounts.Add(cached, completion.CachedPromptTokens);
            created = TokenCounts.Add(created, completion.CacheCreationPromptTokens);
            var answer = completion.Message.Content ?? "";
            outputTruncated = completion.FinishReason is "length" or "max_tokens";
            if (outputTruncated) problem = "combined review reached its output token limit; keep claim explanations concise";
            if (!outputTruncated && completion.Message.ToolCalls is not { Count: > 0 })
            {
                var errors = CombinedReviewValidation.Errors(answer, obligations, evidence);
                if (errors.Count > 0)
                {
                    problem = "Combined review response has structural errors:\n" + string.Join("\n", errors.Select(e => "- " + e));
                    messages.Add(ChatMessage.Assistant(answer));
                    messages.Add(ChatMessage.User(problem + "\nCorrect all listed fields in one complete response using the supplied schema. "
                        + "Reassess scope against the request; do not change evidence or invent citations to obtain a pass."));
                    continue;
                }
            }
            var parsed = completion.FinishReason is "length" or "max_tokens" || completion.Message.ToolCalls is { Count: > 0 }
                ? null : ReadCombined(answer);
            if (parsed is { } result)
            {
                // A historical prohibition violation cannot be repaired by another worker attempt.
                // Stop without discarding evidence or allowing a new attempt to hide the operation.
                if (result.Claims is { } checkedClaims
                    && ObligationAudit.CheckProhibitions(obligations, checkedClaims, evidence) is { Sound: false } violation)
                    return result.Review with { Pass = false, Notes = violation.Reason,
                        PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached,
                        CacheCreationPromptTokens = created, Soundness = violation, Obligations = checkedClaims,
                        IncompleteReason = violation.Reason };
                if (result.NeedEvidence.Count > 0)
                {
                    problem = "requested additional evidence is still insufficient";
                    if (attempt == 0)
                    {
                        try { evidence = evidence.Expand(result.NeedEvidence); }
                        catch (ArgumentException ex) { problem = ex.Message; }
                        messages[1] = ChatMessage.User(Prompt(evidence));
                    }
                }
                else if (!result.Review.Pass)
                    return result.Review with { PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created };
                else if (result.Proof is { } proof && result.Claims is { } claims)
                {
                    var audit = ProofAudit.Check(proof, evidence, workspaceRoot);
                    if (proof.Calls.Any(id => evidence.Cited(id) is null) && audit.Sound)
                        audit = new(false, "proof cites a call not shown in the evidence");
                    if (audit.Sound)
                    {
                        var coverage = ObligationAudit.Check(obligations, claims, evidence, workspaceRoot);
                        audit = coverage.Sound ? new(true, audit.Reason + "; " + coverage.Reason) : coverage;
                    }
                    // Contract errors (including citations) were handled above. A substantive failure
                    // remains a verdict, not an invitation to fish for a passing answer.
                    return result.Review with { PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created,
                        Soundness = audit, Obligations = claims };
                }
            }
            messages.Add(ChatMessage.Assistant(answer));
            messages.Add(ChatMessage.User(problem + ". Return the complete combined JSON object using the supplied schema. "
                + "Include proof and every obligation ID, and judge only the evidence now displayed. No more evidence requests."));
        }
        return new(false, problem + " after clarification", prompt, output)
            { CachedPromptTokens = cached, CacheCreationPromptTokens = created, IncompleteReason = problem + " after clarification" };
    }

    private sealed record Combined(ReviewResult Review, ProofClaim? Proof, IReadOnlyList<ObligationClaim>? Claims, IReadOnlyList<int> NeedEvidence);
    private static Combined? ReadCombined(string text)
    {
        var verdict = Parse(text);
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(text));
        if (verdict is null || json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var needed = new List<int>();
            if (root.TryGetProperty("need_evidence", out var need))
            {
                if (need.ValueKind != JsonValueKind.Array || need.GetArrayLength() > 4) return null;
                foreach (var item in need.EnumerateArray())
                    if (item.TryGetInt32(out var id)) needed.Add(id); else return null;
            }
            var proof = root.TryGetProperty("proof", out var p) ? StrictProof(p) : null;
            List<ObligationClaim>? claims = null;
            if (root.TryGetProperty("claims", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                claims = new();
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String
                        || StrictProof(item) is not { } claim) return null;
                    if (!item.TryGetProperty("requirements", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
                    var assessments = new List<RequirementAssessment>();
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object
                            || !part.TryGetProperty("requirement", out var meaning) || meaning.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(meaning.GetString())
                            || !part.TryGetProperty("scope", out var partScope) || partScope.ValueKind != JsonValueKind.String
                            || !part.TryGetProperty("global", out var global) || global.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                            || StrictProof(part) is not { } partProof) return null;
                        assessments.Add(new(meaning.GetString()!, partScope.GetString()!, global.GetBoolean(), partProof) {
                            Prohibitions = part.GetProperty("prohibitions").EnumerateArray().Select(p => p.GetString()!).ToArray()
                        });
                    }
                    claims.Add(new(id.GetString()!, scope.GetString()!, claim) { Requirements = assessments });
                }
            }
            return new(verdict, proof, claims, needed);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static ProofClaim? StrictProof(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("calls", out var calls) || calls.ValueKind != JsonValueKind.Array
            || !element.TryGetProperty("what", out var what) || what.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(what.GetString())) return null;
        foreach (var id in calls.EnumerateArray())
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out _)) return null;
        return ParseProof(element.GetRawText());
    }
}

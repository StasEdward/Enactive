namespace Enactive.Core.Execution;

using System.Text.Json;
using Enactive.Core.Tasks;

/// <summary>Lossless source units, not an LLM paraphrase or a claim of semantic decomposition.</summary>
public sealed record RequestObligation(string Id, string Text);

/// <summary>The same request IDs for planner, worker and reviewer, with an explicit step boundary.</summary>
public sealed record RequestObligations(
    IReadOnlyList<RequestObligation> Items, string CurrentScope, IReadOnlyDictionary<string, string> Scopes)
{
    /// <summary>Planner assignments of source units, not proof that all their parts are complete.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ScopeMap { get; init; }
    public bool FinalReview { get; init; }

    /// <summary>
    /// What the current step IS, as the engine knows it from the plan - for a reviewer judging it against a
    /// request written for the whole run. Null when the plan says nothing the request does not.
    /// </summary>
    public string? ScopeNote { get; init; }

    public RequestObligations ForFinalReview() => this with {
        CurrentScope = "run", FinalReview = true,
        Scopes = Scopes.Concat(new[] { new KeyValuePair<string, string>("run", "Entire completed run") })
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)
    };

    public static RequestObligations ForPlan(string request, Plan plan)
    {
        var obligations = Create(request, steps: plan.Steps.Select(s => s.Title).ToArray());
        var map = obligations.Items.ToDictionary(item => item.Id, item => (IReadOnlyList<string>)
            plan.Steps.Select((step, index) => (step, scope: "S" + (index + 1)))
                .Where(x => x.step.ObligationIds?.Contains(item.Id, StringComparer.Ordinal) == true)
                .Select(x => x.scope).ToArray(), StringComparer.Ordinal);
        return obligations with { ScopeMap = map };
    }

    public RequestObligations AtStep(int step) => this with { CurrentScope = "S" + step };

    /// <summary>A shared assignment permits verification here of a requirement implemented elsewhere.
    /// Deferrals remain deferrals; membership never proves completion.</summary>
    public string EvidenceScope(string id, string scope, ProofClaimKind kind)
        => !FinalReview && (kind is ProofClaimKind.Shown or ProofClaimKind.NotByAnyCall or ProofClaimKind.ExpectedFailure)
            && ScopeMap?.TryGetValue(id, out var owners) == true
            && owners.Contains(CurrentScope) && owners.Contains(scope) ? CurrentScope : scope;

    public string MappingPrompt() => ScopeMap is null ? "" :
        "\nShared requirement-to-step map (fixed by the plan):\n" + JsonSerializer.Serialize(ScopeMap)
        + (FinalReview ? "\nHistorical assignments only; all requirements are now assessed in scope run.\n" :
        "\nCurrent scope: " + CurrentScope
        + ". A source unit can apply to multiple steps: implement here or verify earlier implementation as this step requires. "
        + "Assess only the current contribution, not completion of the whole multi-step requirement. "
        + "An empty assignment means unspecified, NOT waived: use the original request and this step's objective. "
        + "Global constraints apply in every step regardless of assignment. Do not invent a different ownership map.\n"
        + (ScopeNote is { Length: > 0 } note ? "What this step is, from the plan: " + note + "\n" : ""));
    public static RequestObligations Create(string request, string title = "Whole request", int? step = null,
        IReadOnlyList<string>? steps = null)
    {
        // Keep newline separators in the source units: joining Text reconstructs the original exactly.
        var parts = new List<string>();
        var leading = "";
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(request, @"[^\n]*\n|[^\n]+$"))
        {
            var text = match.Value;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (parts.Count > 0) parts[^1] += text;
                else leading += text;
            }
            else { parts.Add(leading + text); leading = ""; }
        }
        var scopes = steps is { Count: > 0 }
            ? steps.Select((text, i) => (Id: "S" + (i + 1), text)).ToDictionary(p => p.Id, p => p.text)
            : new Dictionary<string, string> { ["run"] = title };
        return new(parts.Select((text, i) => new RequestObligation("O" + (i + 1).ToString("D3"), text)).ToArray(),
            step is { } n && scopes.ContainsKey("S" + n) ? "S" + n : "run", scopes);
    }

    /// <summary>Planner/worker view: original text once, with stable IDs mapped to source lines.
    /// No JSON escaping, paraphrase, or reviewer verdict instructions.</summary>
    public static string ExecutionPrompt(string request)
    {
        var index = new System.Text.StringBuilder();
        var line = 1;
        foreach (var item in Create(request).Items)
        {
            var breaks = item.Text.Count(c => c == '\n');
            var last = line + breaks - (item.Text.EndsWith('\n') ? 1 : 0);
            index.Append(item.Id).Append(": lines ").Append(line).Append('–').Append(last).AppendLine();
            line += breaks;
        }
        return "\nOriginal request (verbatim):\n" + request
            + "\n\nRequirement IDs (1-based source lines in the original request; a unit may contain multiple requirements):\n"
            + index + "O-IDs identify source units, NOT tests. Preserve their meanings; quote source text in coverage tables. "
            + "Apply the relevant requirements within the current scope; global constraints apply throughout.\n";
    }

    public string Describe() => "\nRequest obligations (verbatim source units; none omitted):\n"
        + JsonSerializer.Serialize(this, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        + MappingPrompt()
        + (FinalReview ? "\nFINAL RUN REVIEW: every requirement must now be resolved in scope run. "
            + "Earlier step assignments are historical context, not permission to defer. Reconcile ALL parts of every "
            + "source unit against the current files and run evidence. Previous step passes are not proof. "
            + "Call IDs below are newly numbered for this entire run; do not reuse earlier review IDs.\n" : "")
        + "\nThese units may contain several requirements. Check every requirement within each unit, "
        + "including every file/test and exact commands. Their IDs are stable within this request. "
        + "Apply each to the current step's scope; do not substitute the step title for the original requirement.\n";
}

/// <summary>A reviewer decomposition of one source unit, not an engine assertion of semantic truth.</summary>
public sealed record RequirementAssessment(string Requirement, string Scope, bool Global, ProofClaim Proof)
{
    /// <summary>Explicit, unconditional prohibitions classified from the original request.
    /// Classification remains semantic; recorded conflicting operations are checked in code.</summary>
    public IReadOnlyList<string> Prohibitions { get; init; } = [];
}
public sealed record ObligationClaim(string Id, string Scope, ProofClaim Proof)
{
    public IReadOnlyList<RequirementAssessment>? Requirements { get; init; }
}

public static class ObligationAudit
{
    /// <summary>Historical violations cannot be repaired by replaying the worker or restoring a file.</summary>
    public static ProofVerdict CheckProhibitions(RequestObligations obligations, IReadOnlyList<ObligationClaim> claims,
        EvidenceView evidence)
    {
        foreach (var claim in claims)
        foreach (var requirement in claim.Requirements ?? [])
        {
            if (!requirement.Prohibitions.Contains("file-deletion", StringComparer.Ordinal)
                || (!requirement.Global && requirement.Scope != obligations.CurrentScope)) continue;
            var deleted = evidence.Actions.Select((action, i) => (action, id: i + 1))
                .Where(x => x.action.FileDeletion && x.action.Outcome != ActionOutcome.Refused
                    && (requirement.Global || obligations.CurrentScope == "run"
                        || obligations.CurrentScope == "S" + x.action.Step))
                .Select(x => x.id).ToArray();
            if (deleted.Length > 0)
                return new(false, $"{claim.Id}: explicit prohibition of file deletion was violated by call(s) "
                    + string.Join(", ", deleted) + ". Restoration, scratch cleanup and tool approval do not waive this restriction.");
        }
        return new(true, "no recorded conflict with classified prohibitions; unknown operations still require semantic review");
    }

    /// <summary>Checks completeness of the declared coverage and citation structure, not semantic entailment.</summary>
    public static ProofVerdict Check(RequestObligations obligations, IReadOnlyList<ObligationClaim> claims,
        EvidenceView evidence, string? workspaceRoot = null)
    {
        var prohibitions = CheckProhibitions(obligations, claims, evidence);
        if (!prohibitions.Sound) return prohibitions;
        var expected = obligations.Items.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (claims.Count != expected.Count || claims.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != claims.Count
            || claims.Any(c => !expected.Contains(c.Id)))
            return new(false, "obligation coverage must name every request ID exactly once; missing, duplicate or unknown ID");
        foreach (var claim in claims)
        {
            var effectiveScope = obligations.EvidenceScope(claim.Id, claim.Scope, claim.Proof.Kind);
            if (!obligations.Scopes.ContainsKey(claim.Scope) || string.IsNullOrWhiteSpace(claim.Proof.What))
                return new(false, $"{claim.Id}: unknown step scope or missing explanation");
            if (claim.Proof.Calls.Any(id => evidence.Cited(id) is null))
                return new(false, $"{claim.Id}: citation was not shown in the review evidence");
            if (claim.Requirements is { } requirements)
            {
                if (requirements.Count == 0 && (effectiveScope != obligations.CurrentScope
                    || claim.Proof.Kind != ProofClaimKind.NotByAnyCall || claim.Proof.Calls.Count != 0))
                    return new(false, $"{claim.Id}: context-only source units require a content explanation, not a completion claim");
                foreach (var requirement in requirements)
                {
                    if (string.IsNullOrWhiteSpace(requirement.Requirement)
                        || (requirement.Global && requirement.Scope != obligations.CurrentScope))
                        return new(false, $"{claim.Id}: a global constraint cannot be deferred to another step");
                    var part = Check(obligations with { Items = [new(claim.Id, requirement.Requirement)] },
                        [new(claim.Id, requirement.Scope, requirement.Proof)], evidence, workspaceRoot);
                    if (!part.Sound) return part;
                }
                if (effectiveScope != obligations.CurrentScope && requirements.Any(r => r.Scope == obligations.CurrentScope))
                    return new(false, $"{claim.Id}: a source unit containing current-step requirements cannot be deferred wholesale");
            }
            if (effectiveScope != obligations.CurrentScope)
            {
                if (obligations.FinalReview)
                    return new(false, claim.Id + ": final review cannot defer an obligation to another step");
                // A deferral is a scope judgement, not proof of completion elsewhere.
                if (claim.Proof.Kind != ProofClaimKind.NotShown || claim.Proof.Calls.Count != 0)
                    return new(false, $"{claim.Id}: another step's obligation must be explicitly deferred, not claimed completed");
                continue;
            }
            var audited = ProofAudit.Check(claim.Proof, evidence, workspaceRoot);
            if (!audited.Sound) return new(false, claim.Id + ": " + audited.Reason);
        }
        return new(true, "all request IDs accounted for within the declared step scopes; citations checked, semantic coverage judged by reviewer");
    }
}

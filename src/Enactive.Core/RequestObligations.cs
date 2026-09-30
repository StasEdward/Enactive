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
    /// <summary>
    /// What the current step IS, as the engine knows it from the plan - for a reviewer judging it against a
    /// request written for the whole run. Null when the plan says nothing the request does not.
    /// </summary>
    public string? ScopeNote { get; init; }

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

    /// <summary>The source units the plan gives the current step, each with the other steps it is given to.</summary>
    public IReadOnlyList<(RequestObligation Unit, IReadOnlyList<string> AlsoTo)> Owned() => ScopeMap is null ? [] :
        Items.Where(i => ScopeMap.TryGetValue(i.Id, out var owners) && owners.Contains(CurrentScope))
            .Select(i => (i, (IReadOnlyList<string>)ScopeMap[i.Id].Where(s => s != CurrentScope).ToArray())).ToArray();

    /// <summary>
    /// The source units no step is given, where the plan gives any: with one short review per step and none of the whole
    /// run after them, such a unit is checked by no one. A plan that assigns nothing at all says nothing here.
    /// </summary>
    public IReadOnlyList<RequestObligation> Unassigned() => ScopeMap is null || ScopeMap.Values.All(o => o.Count == 0) ? [] :
        Items.Where(i => !ScopeMap.TryGetValue(i.Id, out var owners) || owners.Count == 0).ToArray();

    public string MappingPrompt() => ScopeMap is null ? "" :
        "\nShared requirement-to-step map (fixed by the plan):\n" + JsonSerializer.Serialize(ScopeMap)
        + "\nCurrent scope: " + CurrentScope
        + ". A source unit can apply to multiple steps: implement here or verify earlier implementation as this step requires. "
        + "Assess only the current contribution, not completion of the whole multi-step requirement. "
        + "An empty assignment means unspecified, NOT waived: use the original request and this step's objective. "
        + "Global constraints apply in every step regardless of assignment. Do not invent a different ownership map.\n"
        + (ScopeNote is { Length: > 0 } note ? "What this step is, from the plan: " + note + "\n" : "");
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
        + "\nThese units may contain several requirements. Check every requirement within each unit, "
        + "including every file/test and exact commands. Their IDs are stable within this request. "
        + "Apply each to the current step's scope; do not substitute the step title for the original requirement.\n";
}

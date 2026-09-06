namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;

/// <summary>
/// The result of the understand/plan phase. For a Task, Plan is a real dependency graph.
///
/// <para>The token counts are carried out with the result because the planning call happens outside
/// the tool loop, which is where usage events are emitted from: without this the planner's tokens
/// were spent on every single run and counted on none of them.</para>
/// </summary>
public sealed record PlanResult(
    IntentDisposition Disposition, string Title, Plan? Plan,
    int PromptTokens = 0, int CompletionTokens = 0);

/// <summary>
/// Turns an intent into a routing decision + optional DAG plan with one LLM call (PLAN_v2 §3). Steps may
/// declare dependencies (dependsOn indices); the plan is built as a graph. Falls back to a QuickAction if
/// the model's answer can't be parsed.
/// </summary>
public sealed class Planner
{
    public async Task<PlanResult> PlanAsync(
        string request, WorkContext context, IChatProvider provider, string model, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User(request)
        };

        var completion = await provider.CompleteAsync(new ChatRequest(model, messages, Temperature: 0.0), ct);
        return Parse(completion.Message.Content ?? "", request) with
        {
            PromptTokens = completion.PromptTokens ?? 0,
            CompletionTokens = completion.CompletionTokens ?? 0
        };
    }

    private static PlanResult Parse(string text, string fallbackTitle)
    {
        var json = ExtractJson(StripThink(text));
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var disposition = root.TryGetProperty("disposition", out var d)
                    && string.Equals(d.GetString(), "task", StringComparison.OrdinalIgnoreCase)
                    ? IntentDisposition.Task
                    : IntentDisposition.QuickAction;

                var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? fallbackTitle)
                    : fallbackTitle;

                var specs = new List<PlanStepSpec>();
                if (root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in s.EnumerateArray())
                    {
                        // A step is either a bare string (no deps) or an object {title, dependsOn:[int,...]}.
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            if (el.GetString() is { Length: > 0 } str)
                                specs.Add(new PlanStepSpec(str, Array.Empty<int>()));
                        }
                        else if (el.ValueKind == JsonValueKind.Object)
                        {
                            var stepTitle = el.TryGetProperty("title", out var st) && st.ValueKind == JsonValueKind.String
                                ? st.GetString()
                                : null;
                            if (string.IsNullOrWhiteSpace(stepTitle))
                                continue;

                            var deps = new List<int>();
                            // An empty "dependsOn": [] is a statement, not a silence - the step is
                            // independent and may run in parallel. Only a step object that omits the
                            // property entirely leaves the dependency question unanswered.
                            var declared = el.TryGetProperty("dependsOn", out var dep)
                                           && dep.ValueKind == JsonValueKind.Array;
                            if (declared)
                                foreach (var di in dep.EnumerateArray())
                                    if (di.ValueKind == JsonValueKind.Number && di.TryGetInt32(out var idx))
                                        deps.Add(idx);

                            specs.Add(new PlanStepSpec(stepTitle!, deps, ParseComplexity(el), declared));
                        }
                    }
                }

                // A "task" with no steps is really a quick action.
                if (disposition == IntentDisposition.Task && specs.Count == 0)
                    disposition = IntentDisposition.QuickAction;

                var plan = disposition == IntentDisposition.Task ? DagPlan.FromSpecs(specs) : null;
                return new PlanResult(disposition, Truncate(title, 80), plan);
            }
            catch (JsonException)
            {
                // fall through to the safe default
            }
        }

        return new PlanResult(IntentDisposition.QuickAction, Truncate(fallbackTitle, 80), null);
    }

    private static string StripThink(string text)
    {
        const string open = "<think>";
        const string close = "</think>";
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? text.Remove(start, end + close.Length - start) : text;
    }

    private static StepComplexity ParseComplexity(JsonElement el)
    {
        if (el.TryGetProperty("complexity", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString()?.Trim().ToLowerInvariant() switch
            {
                "trivial" => StepComplexity.Trivial,
                "complex" => StepComplexity.Complex,
                _ => StepComplexity.Normal
            };
        return StepComplexity.Normal;
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private const string SystemPrompt =
        "You are a planning assistant for a developer agent. Decide whether the request is a single action or "
        + "genuinely needs several distinct stages. Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"disposition\":\"quick_action\" or \"task\",\"title\":\"short title\",\"steps\":[{\"title\":\"...\",\"dependsOn\":[],\"complexity\":\"normal\"}]}. "
        + "STRONGLY prefer \"quick_action\" with empty steps: one action is a quick_action even when it has parts done "
        + "together - e.g. 'run script X and save its output to file Y' is ONE quick_action, not multiple steps. "
        + "For a \"task\", each step is an object with a \"title\" and \"dependsOn\": the 0-based indices of earlier "
        + "steps that must finish first ([] = can start immediately). Model the REAL dependencies as a graph — steps "
        + "that do not depend on each other must have independent dependsOn so they are not forced into a chain. Use "
        + "2-4 steps max. NEVER split one command into 'do it' / 'capture it' / 'save it'. Keep the title under 8 words. "
        + "For each step also set \"complexity\", which decides WHICH MODEL runs it: \"trivial\" (a rename, one "
        + "obvious edit, a one-line command) goes to a small fast model, \"normal\" to the standard one, and "
        + "\"complex\" to a much slower and far more expensive model. \"normal\" IS THE DEFAULT — use it unless the "
        + "step clearly does not fit. Mark a step \"complex\" when it needs reasoning across several files, a "
        + "non-obvious algorithm, judgement a competent junior developer would get wrong, OR when its output has to "
        + "ASSERT EXTERNAL FACTS the model must recall rather than read — exact command syntax and flags, package "
        + "names, port numbers, configuration keys, API names, version requirements for software that is not in this "
        + "workspace. A small model cannot tell that it is wrong about those and will invent confident, plausible "
        + "detail instead. LENGTH IS NOT COMPLEXITY: a long document, boilerplate, formatting, or writing up what a "
        + "tool actually returned stays \"normal\", however many pages it is, and so does describing files that ARE "
        + "in this workspace — those can be read instead of recalled. No more than TWO steps in a plan may be "
        + "\"complex\".";
}

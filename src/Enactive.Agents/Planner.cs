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
    int PromptTokens = 0, int CompletionTokens = 0,
    PlanReadout Readout = PlanReadout.Understood)
{
    /// <summary>
    /// How much of <see cref="PromptTokens"/> the provider served from its prompt cache. Null where
    /// it does not report one - see <c>ChatCompletion.CachedPromptTokens</c>.
    ///
    /// <para>An init property so the six-argument construction above and every <c>with</c> of it
    /// keep working. Carried for the same reason the token counts themselves are: the planning call
    /// happens outside the tool loop, so anything it does not hand back is spent and never
    /// counted.</para>
    /// </summary>
    public int? CachedPromptTokens { get; init; }
}

/// <summary>
/// On what basis this run is doing what it is doing.
///
/// <para>Three different things used to arrive as the same value — a QuickAction under a title cut
/// from the request — and nothing anywhere could tell them apart: the model DECIDING the request is
/// one action, the model answering "task" with no steps in it, and nobody being able to read the
/// answer at all. The last of those is a fallback, not a decision, and a genuine multi-step request
/// collapsing into one unplanned action is exactly the kind of thing a person needs told.</para>
///
/// <para>It is a value rather than a sentence for the usual reason: rewording a summary must not
/// change what a reader of the run can find out.</para>
/// </summary>
public enum PlanReadout
{
    /// <summary>The model answered in the shape it was asked for, and this is what it said.</summary>
    Understood,

    /// <summary>
    /// It said "task" and listed no steps. A task with nothing in it is a quick action, which is a
    /// documented rule rather than a failure - but it is still not the same as being told "one
    /// action", and a planner that keeps doing it is a planner that is not working.
    /// </summary>
    TaskWithNoSteps,

    /// <summary>
    /// Two answers, neither of them readable. This runs as a single action because the request still
    /// has to be acted on and refusing it would be worse - NOT because anything decided it has one
    /// step. The run says so out loud.
    /// </summary>
    Unreadable
}

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
        var answer = completion.Message.Content ?? "";

        var prompt = completion.PromptTokens ?? 0;
        var output = completion.CompletionTokens ?? 0;

        // Summed the same way as the tokens, and kept NULL until something reports one: adding a
        // retry's cache reads to a first call that never mentioned any would turn "nobody counted"
        // into a number, which is the one thing this field exists not to do.
        var cached = completion.CachedPromptTokens;

        if (Parse(answer, request) is { } plan)
            return plan with
            {
                PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached
            };

        // Nothing readable came back. Ask once more, showing what arrived and exactly what shape was
        // wanted - the same recovery the reviewer does, and for the same reason: a model that
        // wandered off format usually returns to it when told precisely what to produce. The cost is
        // one round trip on a path that should be rare, and what it buys is a real plan instead of a
        // multi-step request silently becoming one unplanned action.
        messages.Add(new ChatMessage(ChatRole.Assistant, answer, null));
        messages.Add(ChatMessage.User(RepairPrompt));

        var retry = await provider.CompleteAsync(new ChatRequest(model, messages, Temperature: 0.0), ct);

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;
        cached = TokenCounts.Add(cached, retry.CachedPromptTokens);

        if (Parse(retry.Message.Content ?? "", request) is { } retried)
            return retried with
            {
                PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached
            };

        // Twice with nothing readable. The request is still acted on - refusing it would be worse
        // than doing the obvious thing with it - but this is a FALLBACK and the difference travels
        // with the result instead of disappearing into a title.
        return new PlanResult(
            IntentDisposition.QuickAction, Truncate(request, 80), null,
            prompt, output, PlanReadout.Unreadable) { CachedPromptTokens = cached };
    }

    /// <summary>Returns the plan, or null when the answer carried none.</summary>
    private static PlanResult? Parse(string text, string fallbackTitle)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(text));
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // The answer has to be recognisably OUR shape. Any JSON object at all used to be
                // accepted, and one without a "disposition" became a QuickAction - so {"a":1}, or a
                // fragment of something else the model was writing, came back indistinguishable from
                // a decision. It has to SAY one of the two dispositions, or list steps, which is an
                // unambiguous statement of a plan on its own.
                var stated = root.TryGetProperty("disposition", out var d)
                             && d.ValueKind == JsonValueKind.String
                             ? d.GetString()
                             : null;

                var isTask = string.Equals(stated, "task", StringComparison.OrdinalIgnoreCase);
                var isQuick = string.Equals(stated, "quick_action", StringComparison.OrdinalIgnoreCase);

                var listsSteps = root.TryGetProperty("steps", out var statedSteps)
                                 && statedSteps.ValueKind == JsonValueKind.Array
                                 && statedSteps.GetArrayLength() > 0;

                if (!isTask && !isQuick && !listsSteps)
                    return null;

                var disposition = isTask || (!isQuick && listsSteps)
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

                // A "task" with no steps is really a quick action - a documented rule, and still
                // worth recording as what it was rather than as a decision to do one thing.
                var readout = PlanReadout.Understood;
                if (disposition == IntentDisposition.Task && specs.Count == 0)
                {
                    disposition = IntentDisposition.QuickAction;
                    readout = PlanReadout.TaskWithNoSteps;
                }

                var plan = disposition == IntentDisposition.Task ? DagPlan.FromSpecs(specs) : null;
                return new PlanResult(disposition, Truncate(title, 80), plan, Readout: readout);
            }
            catch (JsonException)
            {
                // Not readable. The caller re-asks once and, failing that, says so.
            }
        }

        return null;
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

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <summary>Shown when the first answer could not be read: what was wanted, and nothing else.</summary>
    private const string RepairPrompt =
        "That reply did not contain a plan. Reply with NOTHING but a single JSON object, no prose, "
        + "no code fences, no explanation before or after it, in exactly this shape:\n"
        + "{\"disposition\":\"quick_action\",\"title\":\"short title\",\"steps\":[]}\n"
        + "or, when the request genuinely needs several stages:\n"
        + "{\"disposition\":\"task\",\"title\":\"short title\","
        + "\"steps\":[{\"title\":\"...\",\"dependsOn\":[],\"complexity\":\"normal\"}]}";

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

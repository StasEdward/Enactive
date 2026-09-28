namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;

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
    public int? CacheCreationPromptTokens { get; init; }

    /// <summary>A bounded planning attempt could not complete; execution must not start.</summary>
    public string? IncompleteReason { get; init; }

    /// <summary>
    /// Commands the planner said would PROVE this request was carried out, written before any of
    /// the work. Empty when it proposed none, which is the right answer for a question, an
    /// explanation, or anything else nothing can be run against.
    ///
    /// <para>An init property, like the cache count above and for the same reason: the positional
    /// list is already six long and every <c>with</c> of it has to keep working.</para>
    ///
    /// <para>These are <see cref="CriterionOrigin.Proposed"/>, and the engine uses them only when
    /// the run was given no criteria of its own — see <c>Orchestrator.CriteriaFor</c>.</para>
    /// </summary>
    public IReadOnlyList<SuccessCriterionDefinition> Checks { get; init; }
        = Array.Empty<SuccessCriterionDefinition>();

    /// <summary>
    /// The criteria the planner stated as types (Phase 3), as it wrote them - not yet accepted. The
    /// engine validates them against the workspace and drops what it cannot check (TypedCriteria).
    /// </summary>
    public IReadOnlyList<PlannedCriterion> PlannedCriteria { get; init; } = [];
    internal bool RestoredChecks { get; init; }
    public Enactive.Core.Tools.TaskActionPolicy? ActionPolicy { get; init; }
    public IReadOnlyList<Enactive.Core.Tools.TaskRestriction> Restrictions { get; init; } = [];
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
    public Planner() { }
    // Allows engine fixtures focused on later stages to supply an already-reviewed plan.
    internal Planner(bool checksAuditEnabled) => ChecksAuditEnabled = checksAuditEnabled;
    internal bool ChecksAuditEnabled { get; } = true;
    /// <param name="maxSteps">
    /// The run's own step budget, when it has one — <c>ExecutionLimits.MaxSteps</c> from the
    /// template. Told to the planner rather than approximated by a number written into the prompt:
    /// a plan longer than the budget cannot finish, and <c>RunBudget</c> stops it partway with the
    /// work half done. Null when nothing limits the run, and then nothing is said.
    /// </param>
    /// <param name="proposeChecks">
    /// Whether to ask the planner how this work will be PROVED — see <see cref="ChecksPrompt"/>.
    /// Off by default so that nothing which constructs a planner directly starts paying for a
    /// question it will not read the answer to.
    /// </param>
    public async Task<PlanResult> PlanAsync(
        string request, WorkContext context, IChatProvider provider, string model,
        CancellationToken ct, int? maxSteps = null, bool proposeChecks = false,
        int? turnCeiling = null, int outputBudget = 4096,
        Func<int, int, string?>? beforeRetry = null, bool stepOutputs = false, bool typedCriteria = false,
        bool dynamicSteps = false)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPromptFor(maxSteps, proposeChecks, turnCeiling, stepOutputs, typedCriteria, dynamicSteps)),
            ChatMessage.User(Where(context) + Enactive.Core.Execution.RequestObligations.ExecutionPrompt(request))
        };

        // No ResponseSchema, deliberately - see StructuredOutputTests.The_planner_is_not_given_a_schema
        // This was changed on 2026-09-23 and changed straight
        // back when that test caught it.
        var completion = await provider.CompleteAsync(GenerationAllowance.Fit(new ChatRequest(model, messages, Temperature: 0.0,
            Purpose: GenerationPurpose.Planning, OutputTokenLimit: Math.Max(1, outputBudget)), provider), ct);
        var answer = completion.Message.Content ?? "";

        var prompt = completion.PromptTokens ?? 0;
        var output = completion.CompletionTokens ?? 0;

        // Summed the same way as the tokens, and kept NULL until something reports one: adding a
        // retry's cache reads to a first call that never mentioned any would turn "nobody counted"
        // into a number, which is the one thing this field exists not to do.
        var cached = completion.CachedPromptTokens;
        var created = completion.CacheCreationPromptTokens;

        if (ReadComplete(completion, request) is { } plan)
            return plan with
            {
                PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created
            };

        // Nothing readable came back. Ask once more, showing what arrived and exactly what shape was
        // wanted - the same recovery the reviewer does, and for the same reason: a model that
        // wandered off format usually returns to it when told precisely what to produce. The cost is
        // one round trip on a path that should be rare, and what it buys is a real plan instead of a
        // multi-step request silently becoming one unplanned action.
        messages.Add(new ChatMessage(ChatRole.Assistant, answer, null));
        messages.Add(ChatMessage.User(RepairPrompt));

        if (beforeRetry?.Invoke(prompt, output) is { } spent)
            return new(IntentDisposition.QuickAction, Truncate(request, 80), null, prompt, output, PlanReadout.Unreadable)
            { CachedPromptTokens = cached, CacheCreationPromptTokens = created, IncompleteReason = spent };

        ChatCompletion retry;
        try
        {
            retry = await provider.CompleteAsync(GenerationAllowance.Fit(new ChatRequest(model, messages, Temperature: 0.0,
                Purpose: GenerationPurpose.Planning, OutputTokenLimit: Math.Max(1, outputBudget)), provider), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(IntentDisposition.QuickAction, Truncate(request, 80), null, prompt, output, PlanReadout.Unreadable)
            { CachedPromptTokens = cached, CacheCreationPromptTokens = created, IncompleteReason = "Planner clarification failed: " + ex.Message };
        }

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;
        cached = TokenCounts.Add(cached, retry.CachedPromptTokens);
        created = TokenCounts.Add(created, retry.CacheCreationPromptTokens);

        if (ReadComplete(retry, request) is { } retried)
            return retried with
            {
                PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created
            };

        // Twice with nothing readable. The request is still acted on - refusing it would be worse
        // than doing the obvious thing with it - but this is a FALLBACK and the difference travels
        // with the result instead of disappearing into a title.
        return new PlanResult(
            IntentDisposition.QuickAction, Truncate(request, 80), null,
            prompt, output, PlanReadout.Unreadable) { CachedPromptTokens = cached, CacheCreationPromptTokens = created,
                IncompleteReason = completion.FinishReason is "length" or "max_tokens" || retry.FinishReason is "length" or "max_tokens"
                    ? "Planner reached its output limit after one clarification; no work was started." : null };
    }

    private static PlanResult? ReadComplete(ChatCompletion completion, string request)
        => completion.FinishReason is "length" or "max_tokens" || completion.Message.ToolCalls is { Count: > 0 }
            ? null : Parse(completion.Message.Content ?? "", request);

    /// <summary>One structural repair attempt. Never falls back to execution of an unplanned action.</summary>
    internal async Task<PlanResult> ReplanAsync(string request, WorkContext context, PlanResult invalid,
        string diagnostic, IChatProvider provider, string model, CancellationToken ct,
        int? maxSteps, bool proposeChecks, int turnCeiling, int outputBudget = 4096, bool stepOutputs = false,
        bool typedCriteria = false, bool dynamicSteps = false)
    {
        var steps = invalid.Plan!.Steps;
        var indices = steps.Select((step, index) => (step.Id, index)).GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First().index);
        var prior = JsonSerializer.Serialize(new
        {
            disposition = "task", title = invalid.Title,
            steps = steps.Select(s => new
            {
                title = s.Title,
                dependsOn = s.DependsOn.Select(id => indices.TryGetValue(id, out var index) ? index : -1).ToArray(),
                complexity = s.Complexity.ToString().ToLowerInvariant(),
                obligations = s.ObligationIds,
                output = s.Output is { } declared ? OutputJson(declared) : null,
                forEach = s.ForEach is { } each ? new { step = each.Step, field = each.Field } : null,
                report = s.Report
            })
        });
        ChatMessage[] messages =
        [
            ChatMessage.System(SystemPromptFor(maxSteps, proposeChecks, turnCeiling, stepOutputs, typedCriteria, dynamicSteps)),
            ChatMessage.User(Where(context) + Enactive.Core.Execution.RequestObligations.ExecutionPrompt(request)),
            ChatMessage.Assistant(prior),
            ChatMessage.User("The entire plan was rejected before execution: " + diagnostic
                + " No steps ran. Return one complete corrected task DAG as JSON, preserving the original objective. "
                + "Use valid 0-based dependency indices, no self-dependencies or cycles, and preserve necessary prerequisites. "
                + "Do not call tools or switch to quick_action. This is the only structural repair attempt.")
        ];
        var completion = await provider.CompleteAsync(GenerationAllowance.Fit(new(model, messages, Temperature: 0,
            Purpose: GenerationPurpose.Planning, OutputTokenLimit: Math.Max(1, outputBudget)), provider), ct);
        var repaired = completion.FinishReason is "length" or "max_tokens" || completion.Message.ToolCalls is { Count: > 0 }
            ? null : Parse(completion.Message.Content ?? "", request);
        if (repaired is not { Disposition: IntentDisposition.Task, Plan.Steps.Count: > 0 })
            repaired = invalid with { Readout = PlanReadout.Unreadable };
        return repaired with
        {
            PromptTokens = completion.PromptTokens ?? 0, CompletionTokens = completion.CompletionTokens ?? 0,
            CachedPromptTokens = completion.CachedPromptTokens, CacheCreationPromptTokens = completion.CacheCreationPromptTokens
        };
    }

    /// <summary>
    /// The few facts about WHERE this run happens, put in front of the request.
    ///
    /// <para><b>The planner took a <c>WorkContext</c> and never read it.</b> It decided how many
    /// steps the work has and WHICH MODEL runs each of them knowing only the sentence the person
    /// typed — not the operating system, not whether this is a git repository, not what the
    /// project is called. The worker two calls later gets all of it
    /// (<c>Orchestrator.BuildUserPrompt</c>), which is where these lines come from: the same facts,
    /// already assembled, already bounded, and thrown away by the one call that was planning the
    /// work.</para>
    ///
    /// <para>In the USER message rather than the system prompt, deliberately. The system prompt is
    /// identical for every run and every workspace, which is what lets a provider serve it from its
    /// prompt cache; folding a workspace's name into it would make each workspace a cache miss to
    /// say something that is not a rule of planning.</para>
    ///
    /// <para>Project memory is NOT here, and that is a judgement rather than an oversight. It is up
    /// to twenty entries read into the prompt of every step already, it says what the project has
    /// DECIDED rather than what it IS, and the planner's job is small. Facts about the place, not
    /// its history.</para>
    /// </summary>
    internal static string Where(WorkContext context)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(context.ProjectName))
            lines.Add($"Workspace: {context.ProjectName}");
        lines.Add("All check commands execute INSIDE the workspace root. The workspace name is a label, "
            + "not a directory prefix. Preserve exact commands and relative paths given in the request; "
            + "do not prepend the workspace name. A missing target can be a mistaken check, not broken code.");

        if (!string.IsNullOrWhiteSpace(context.GitBranch))
            lines.Add($"Git branch: {context.GitBranch}");

        if (context.Environment is { } env)
            lines.AddRange(env.Summary().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .Select(l => l.Trim()));

        // The one fact a planner cannot get for itself and cannot size a step without. "Check every
        // page" is one step for eleven pages and a dead run for three hundred, and until this block
        // existed nothing in the prompt said which of the two it was looking at. See
        // WorkspaceCensus for the measurement that put it here.
        if (context.Inventory.Count > 0)
        {
            lines.Add("What is in the workspace (build output not counted):");
            lines.AddRange(context.Inventory.Select(i => "  " + i));
        }

        if (lines.Count == 0)
            return "";

        // Labelled as not being the request, because it is about to sit immediately above one. A
        // planner that reads "Git branch: engeen_v2" as something to act on would plan to act on it.
        return "Where this runs (background, NOT the request):\n"
             + string.Join("\n", lines.Select(l => "  " + l))
             + "\n\nThe request:\n";
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

                            IReadOnlyList<string>? obligationIds = null;
                            if (el.TryGetProperty("obligations", out var assigned))
                            {
                                if (assigned.ValueKind != JsonValueKind.Array || assigned.EnumerateArray().Any(
                                    id => id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())))
                                    return null;
                                obligationIds = assigned.EnumerateArray().Select(id => id.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                                var known = Enactive.Core.Execution.RequestObligations.Create(fallbackTitle).Items.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
                                if (obligationIds.Any(id => !known.Contains(id))) return null;
                            }
                            specs.Add(new PlanStepSpec(stepTitle!, deps, ParseComplexity(el), declared)
                            {
                                ObligationIds = obligationIds,
                                Output = el.TryGetProperty("output", out var output) ? ParseOutput(output, specs.Count + 1) : null,
                                ForEach = ParseForEach(el),
                                Report = el.TryGetProperty("report", out var report) && report.ValueKind == JsonValueKind.String
                                    && !string.IsNullOrWhiteSpace(report.GetString()) ? report.GetString() : null
                            });
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
                return new PlanResult(disposition, Truncate(title, 80), plan, Readout: readout)
                {
                    Checks = ParseChecks(root, fallbackTitle),
                    PlannedCriteria = TypedCriteria.Read(root)
                };
            }
            catch (JsonException)
            {
                // Not readable. The caller re-asks once and, failing that, says so.
            }
        }

        return null;
    }

    /// <summary>How many checks one plan may propose.</summary>
    /// <remarks>
    /// Each one is a real command run at the end of the run, and — when it fails — a repair loop
    /// behind it. Four is enough to say "it builds, the tests pass, the file is there" and few
    /// enough that a model listing everything it can think of cannot turn the verdict into a
    /// second build system. Named here because <c>CapsAnnounceThemselvesTests</c> asks every size
    /// limit which test drives it.
    /// </remarks>
    internal const int MaxChecks = 4;

    /// <summary>
    /// The checks the planner proposed, read strictly.
    ///
    /// <para>Suggestions are required and expect zero. Explicit user checks carry a verbatim
    /// request passage and may describe an expected negative result. Extracting the user's intent
    /// remains the planner's semantic responsibility; source membership is checked in code.</para>
    /// </summary>
    private static IReadOnlyList<SuccessCriterionDefinition> ParseChecks(JsonElement root, string request)
    {
        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            return Array.Empty<SuccessCriterionDefinition>();

        var found = new List<SuccessCriterionDefinition>();

        foreach (var el in checks.EnumerateArray())
        {
            if (found.Count >= MaxChecks)
                break;

            if (el.ValueKind != JsonValueKind.Object)
                continue;

            var command = Text(el, "command");
            if (string.IsNullOrWhiteSpace(command))
                continue;

            var name = Text(el, "name");
            var quote = Text(el, "request_quote");
            if (!string.IsNullOrWhiteSpace(quote)
                && (!request.Contains(quote, StringComparison.Ordinal)
                    || !quote.Contains(command!.Trim(), StringComparison.Ordinal)))
                throw new JsonException("A requested check must quote its exact command from the original request.");
            var expectedExit = !string.IsNullOrWhiteSpace(quote)
                && el.TryGetProperty("expectedExitCode", out var code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var exit) ? exit : 0;
            found.Add(new SuccessCriterionDefinition(
                Name: string.IsNullOrWhiteSpace(name) ? Truncate(command!, 40) : Truncate(name!, 60),
                Command: command!.Trim(),
                ExpectedExitCode: expectedExit,
                Origin: string.IsNullOrWhiteSpace(quote) ? CriterionOrigin.Proposed : CriterionOrigin.Requested)
                { RequestQuote = string.IsNullOrWhiteSpace(quote) ? null : quote });
        }

        return found;
    }

    private static string? Text(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    /// <summary>
    /// A step's declared output, read leniently: a field with a type the engine cannot check is left
    /// out rather than taken on trust, and a declaration with no field left is no declaration.
    /// </summary>
    internal static StepOutputSchema? ParseOutput(JsonElement output, int stepNo)
    {
        if (output.ValueKind != JsonValueKind.Object) return null;
        var fields = new List<StepOutputField>();
        foreach (var property in output.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(property.Name)) continue;
            var field = property.Value;
            var type = StepOutputSchema.TypeNamed(field.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null);
            if (type is null) continue;
            int? Positive(string name) => field.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number
                && n.TryGetInt32(out var v) && v > 0 ? v : null;
            fields.Add(new StepOutputField(property.Name, type.Value,
                field.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString())
                    ? d.GetString()! : property.Name,
                Required: !(field.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.False),
                MaxItems: Positive("maxItems"), MaxLength: Positive("maxLength")));
        }
        return fields.Count == 0 ? null : new StepOutputSchema($"step{stepNo}", 1, fields);
    }

    /// <summary>A step's "forEach", read leniently; whether it can be honoured is the engine's to check (FanOut.Validate).</summary>
    internal static ForEachSource? ParseForEach(JsonElement step)
        => step.TryGetProperty("forEach", out var each) && each.ValueKind == JsonValueKind.Object
           && each.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var index)
           && each.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString())
            ? new ForEachSource(index, f.GetString()!)
            : null;

    /// <summary>A declared output as the planner wrote it, for a plan sent back to it to repair.</summary>
    private static Dictionary<string, object?> OutputJson(StepOutputSchema schema)
        => schema.Fields.ToDictionary(f => f.Name, f => (object?)new Dictionary<string, object?>
        {
            ["type"] = StepOutputSchema.NameOf(f.Type), ["description"] = f.Description, ["required"] = f.Required,
            ["maxItems"] = f.MaxItems, ["maxLength"] = f.MaxLength
        });

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

    /// <summary>
    /// How a step declares what it hands on as values (Phase 2). Short on purpose: the contract itself
    /// reaches the worker through the tool made from it, not through this prompt.
    /// </summary>
    /// <summary>How the planner states criteria as types (Phase 3); sent only when they are on (amendment E).</summary>
    internal const string TypedCriteriaPrompt =
        " Also state what finished work looks like as \"criteria\":[...] the engine checks itself: "
        + "{\"kind\":\"file_exists\",\"path\":\"relative/path\"} (add \"non_empty\":false if it may be empty), "
        + "{\"kind\":\"file_contains\",\"path\":\"...\",\"text\":\"exact text\"}, {\"kind\":\"tests_pass\"}. "
        + "Only what the request makes certain; [] when nothing is.";

    /// <summary>
    /// The coverage criterion (Phase 5.1), sent only when both criteria and step outputs are on: it
    /// is stated over declared outputs, and a planner that cannot declare them cannot use it.
    /// </summary>
    internal const string CoverageCriterionPrompt =
        " When a step names items (path[] or string[]) and a later step must handle EACH of them, that later step declares "
        + "an output of TYPE results - \"output\":{\"notes\":{\"type\":\"results\"}} (one entry per item; not \"text\") - and the criteria add "
        + "{\"kind\":\"covers_all\",\"source\":{\"step\":0,\"field\":\"pages\"},\"results\":{\"step\":1,\"field\":\"notes\"},"
        + "\"evidence\":\"file_read\"}; evidence: file_read (each file read whole), command, or call.";

    /// <summary>
    /// Steps done for each item (Phase 5.3); sent only with step outputs AND dynamic steps on, since the
    /// items are a declared output (amendment E).
    /// </summary>
    internal const string DynamicStepsPrompt =
        " When a step must be done for EACH item an earlier step names in a list output, write that step ONCE, for one item, "
        + "and add \"forEach\":{\"step\":0,\"field\":\"pages\"}: the engine gives every item its own step and hands their "
        + "results on together to the steps that depend on it. Use this instead of guessing batches. Item steps hand their "
        + "findings on as values and never edit shared files: when the results make one document, add \"report\":\"path/to/doc.md\" "
        + "to that step - the engine writes the document from every item's result, and the step after the items hands on a \"summary\".";

    internal const string StepOutputsPrompt =
        " A step whose RESULT later steps must use as data (pages to process, files found, names, counts) declares it: "
        + "\"output\":{\"<field>\":{\"type\":\"path[]\",\"description\":\"...\",\"maxItems\":12}}; types: text, string, integer, "
        + "boolean, path, path[], string[], results. The step hands the values on with a tool and its dependents receive them. "
        + "Limits are this task's (maxItems, maxLength). Declare nothing when prose is enough.";

    private const string SystemPrompt =
        """
        Plan the request. Respond ONLY with JSON, without prose or fences:
        {"disposition":"quick_action" or "task","title":"short title","steps":[{"title":"...","dependsOn":[],"complexity":"normal"}]}.
        Use "quick_action" with empty steps for one cohesive action, including running a command and saving its output.
        Use "task" for distinct stages that can succeed or fail on their own. Each step must succeed or fail on its own:
        it is a review, rollback and model-selection boundary. NEVER split one command into do/capture/save stages.
        Keep titles under 8 words. dependsOn lists 0-based indices of earlier prerequisites; [] means independent.
        Preserve real dependencies without forcing independent steps into a chain.
        Each step also has "obligations":["O001",...], using the supplied original-request IDs.
        Assign each source unit to EVERY step responsible for implementing OR verifying any part of it.
        For implement-then-test, include the same functional requirement IDs in both steps.
        Assign global constraints to all steps. Cover every source ID; never invent an ID.
        This shared map is passed unchanged to workers and reviewers. Assignment is not proof of completion.
        Complexity selects the model: "trivial" for an obvious edit or simple command; "normal" by default;
        "complex" for cross-file reasoning, non-obvious algorithms, difficult judgement or external facts
        (exact API/command syntax, versions/configuration) that cannot be read from this workspace.
        LENGTH IS NOT COMPLEXITY: boilerplate and reports based on tool results stay normal.
        No more than TWO steps may be "complex".
        """;

    /// <summary>
    /// The system prompt, plus the run's real step budget when it has one.
    ///
    /// <para>The budget is a fact about THIS run, not a rule of planning, which is why it is
    /// appended rather than written into the constant. A template that allows twelve steps and one
    /// that allows six want different plans for the same request, and a number baked into the
    /// prompt can only be wrong for one of them — which is exactly what "2-4 steps max" was: every
    /// built-in template declares six to twelve, and none of them could reach its own limit.</para>
    ///
    /// <para>Said as a consequence rather than as an order. A plan that exceeds the budget does not
    /// get trimmed; it runs until the budget is gone and stops with the work unfinished, and that
    /// is the thing worth avoiding.</para>
    /// </summary>
    /// <summary>
    /// What a step IS, at the moment it runs — the one thing the planner decides and the one thing
    /// it was never told.
    ///
    /// <para>A step is not a heading. It is a single conversation with the worker model, it carries
    /// everything said in it so far into every following turn, and it is abandoned outright when it
    /// runs too long — taking every step that depends on it with it. So the planner is making the
    /// most expensive decision in a run blind to what it costs.</para>
    ///
    /// <para><b>Both halves are arithmetic, not opinion.</b> The cost of a step grows with the
    /// SQUARE of its turns, because each turn re-sends what came before it: measured 2026-09-22 on
    /// one request, a single 250-turn step cost 30.6M prompt tokens where the same work in five
    /// steps of 20–60 turns cost 9.2M, and the average prompt fell from 117k to 64k on the split
    /// alone. And the size of a step is a fact about the WORKSPACE, not about the request: "for
    /// every project file" is one step in a repository with three and a dead run in one with three
    /// hundred — a number the planner cannot see from here.</para>
    ///
    /// <para>The ceiling is passed as DATA rather than written into the text, for the same reason
    /// <paramref name="maxSteps"/> is: a number in a prompt that does not come from the engine is a
    /// number that goes stale the first time somebody changes it.</para>
    ///
    /// <para>This says nothing about which model will run the steps, deliberately. "The model is
    /// weak, so plan smaller" is a guess about a name, and this codebase already refuses that kind
    /// of guess elsewhere — a provider's context window is declared, never inferred. What is true
    /// of every model and every workload is the arithmetic above.</para>
    /// </summary>
    /// <param name="turnCeiling">
    /// How many turns a single step may take before the engine abandons it
    /// (<c>Orchestrator.RunawayCeiling</c>). Null leaves the paragraph out entirely.
    /// </param>
    internal static string SystemPromptFor(int? maxSteps, bool proposeChecks = false, int? turnCeiling = null,
        bool stepOutputs = false, bool typedCriteria = false, bool dynamicSteps = false)
    {
        var prompt = SystemPrompt;

        if (typedCriteria)
            prompt += TypedCriteriaPrompt;

        // Sent only when step outputs are on (amendment E: every paragraph says when it is NOT sent).
        if (stepOutputs)
            prompt += StepOutputsPrompt;

        if (stepOutputs && typedCriteria)
            prompt += CoverageCriterionPrompt;

        if (stepOutputs && dynamicSteps)
            prompt += DynamicStepsPrompt;

        if (turnCeiling is > 0)
            prompt += $" A step is ONE conversation with growing history. A step running past {turnCeiling} turns is ABANDONED; "
                    + "dependent steps are skipped. For REPEATED work only, specify bounded batches and use a step per batch. "
                    + "Each batch step SAVES its own results before finishing, so another batch's failure cannot lose them. "
                    + "One cohesive action is never split into stages. A requirement attached to EVERY item a step produces "
                    + "belongs IN the step that produces it (e.g. create each test and verify it detects broken behaviour).";

        if (maxSteps is > 0)
            prompt += $" This run may take at most {maxSteps} step(s) in total — a plan longer than that "
                    + "stops partway with the work unfinished, so do not exceed it.";

        if (proposeChecks)
            prompt += ChecksPrompt;

        return prompt;
    }

    /// <summary>
    /// How the planner is asked to say what would PROVE the work — §9au, and reviewed 2026-09-23.
    ///
    /// <para><b>Three changes, and the reason each one is not just shortening.</b> An outside
    /// review called this over-prompted for current frontier models, which is true of them and the
    /// wrong audience: the planner runs on whatever model the person bound, and on this machine
    /// that includes a 4B. The parts kept are the ones no model can infer — the JSON shape, the
    /// cap, which shell runs a check, that only the exit code is read, and that a check is run
    /// BEFORE the work.</para>
    ///
    /// <para><b>1. The Windows sentence was guarding the wrong mistake.</b> It named Unix-isms —
    /// <c>test</c>, <c>grep</c>, <c>awk</c>, <c>[ ]</c> — while the failure §9au actually records is
    /// <c>Select-String</c> sent to <c>cmd.exe</c>, which is a PowerShell cmdlet and not a Unix
    /// command at all. It now names what happened.</para>
    ///
    /// <para><b>2. Two rules were each stated twice.</b> "Would fail now and pass after" and the
    /// paragraph explaining the baseline are one idea and its mechanism; "a command that cannot
    /// fail proves nothing" and the list of commands that cannot fail are a principle and its
    /// examples. Merged, with the examples kept: a named list is what a small model obeys, and the
    /// list is not enforced anywhere in code.</para>
    ///
    /// <para><b>3. What a rule COSTS stays.</b> "The run is then failed for your guess" is not
    /// padding — it is the difference between a rule a model treats as style and one it treats as
    /// consequence, and §9au records a real run failed for a check against an invented filename.
    /// </para>
    ///
    /// <para>The shape is carried by this prompt; planning deliberately does not request a
    /// response schema. Parsed output and checks are still validated in code.</para>
    /// </summary>
    private const string ChecksPrompt =
        " Also return \"checks\": up to 4 shell commands that would PROVE this request has been "
        + "carried out, or [] when nothing about it can be proved by running something. Shape: "
        + "\"checks\":[{\"name\":\"short name\",\"command\":\"...\"}]. "
        + "For a verification command EXPLICITLY REQUIRED by the user, add request_quote containing the "
        + "verbatim instruction and exact command. Do not mark examples, prohibited commands, setup actions "
        + "or your own suggestions as requested checks. Preserve requested commands exactly, including flags. "
        + "Use expectedExitCode only when that same quoted user instruction explicitly requires a nonzero "
        + "verification outcome; otherwise expectedExitCode=0. "
        + "For suggested checks omit request_quote. Every check starts in the current workspace root; "
        + "you cannot choose a different working directory. Never prefix paths with the workspace label. "
        + "EACH ONE IS RUN BY run_command, which is cmd.exe on Windows and /bin/sh elsewhere - the "
        + "host is named above the request, and a check written for the wrong one of those simply "
        + "never runs. On Windows, cmd.exe has no PowerShell cmdlets (Select-String, Get-Content, "
        + "Test-Path) and no Unix tools (grep, awk, test, [ ]): use the program itself (dotnet, "
        + "git, npm), or findstr, or wrap PowerShell explicitly as "
        + "powershell -NoProfile -Command \"...\". "
        + "THE EXIT CODE IS THE WHOLE VERDICT: 0 means done, anything else means not done, and "
        + "nothing reads the output. Requested checks run only after work. Proposed checks run BEFORE the work as well as after, so write "
        + "each one to FAIL now and PASS once the request is satisfied - one that already passes is "
        + "recorded as proving nothing. That also rules out commands that cannot fail: never "
        + "propose echo, cd, dir, ls, type, cat or exit. Use the project's own real commands, the "
        + "ones this workspace actually has. CHECK ONLY WHAT THE REQUEST ITSELF NAMES - a file, a "
        + "command, a target it actually mentions - and NEVER invent a file name: you cannot see "
        + "this workspace, so a check against a path you made up fails when the work lands under "
        + "the real name, and the run is then failed for your guess. If the request does not say "
        + "where the result goes, check something else or return []. [] IS THE RIGHT ANSWER for a "
        + "question, an explanation, a document, a review, a summary, or anything whose result a "
        + "person has to read - do not invent a check in order to have one.";
}

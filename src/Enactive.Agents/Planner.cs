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
        int? turnCeiling = null)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPromptFor(maxSteps, proposeChecks, turnCeiling)),
            ChatMessage.User(Where(context) + request)
        };

        // No ResponseSchema, deliberately - see StructuredOutputTests.The_planner_is_not_given_a_schema
        // and the note on PlanWithChecksSchema. This was changed on 2026-09-23 and changed straight
        // back when that test caught it.
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
                return new PlanResult(disposition, Truncate(title, 80), plan, Readout: readout)
                {
                    Checks = ParseChecks(root)
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
    /// <para><b>Only the name and the command are taken from the model.</b> The other two fields a
    /// criterion has are exactly the two ways to write a check that cannot fail — an expected exit
    /// code that is not zero, and <c>required: false</c> — so they are not read at all. A proposed
    /// check passes on 0 and is required, or it is not a check.</para>
    /// </summary>
    private static IReadOnlyList<SuccessCriterionDefinition> ParseChecks(JsonElement root)
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
            found.Add(new SuccessCriterionDefinition(
                Name: string.IsNullOrWhiteSpace(name) ? Truncate(command!, 40) : Truncate(name!, 60),
                Command: command!.Trim(),
                Origin: CriterionOrigin.Proposed));
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
        + "that do not depend on each other must have independent dependsOn so they are not forced into a chain. "
        // "Use 2-4 steps max" was here, and it was the wrong quantity to limit - the same mistake
        // Orchestrator.StallLimit describes having made with a flat cap of 12 turns: "Work is not
        // the thing to limit; a project with a hundred files needs a hundred turns and no setting
        // should have to say so." Nothing in FIX_PLAN records an incident that earned the number,
        // and it contradicted the product outright: every built-in template declares a step budget
        // of 6 to 12, so none of them could ever reach its own limit. It also worked against the
        // rest of this prompt - forcing work into four steps makes each one a mixture of trivial
        // and complex, which must then be routed as complex, so the expensive model does the
        // trivial parts; and it capped the parallelism the dependsOn graph above exists to express.
        //
        // What is left is the rule that was doing the actual work: no ceremonial stages. The real
        // ceiling is ExecutionLimits.MaxSteps - data, visible in the template editor, enforced by
        // RunBudget - and it is now told to the planner rather than guessed at (see SystemPromptFor).
        + "A step is one thing that can succeed or fail on its own: that is the unit a reviewer judges, the unit a "
        + "rejection undoes, and the unit a model is chosen for. Use as many steps as the work genuinely has, and "
        + "do not invent stages that only name parts of one action - NEVER split one command into 'do it' / "
        + "'capture it' / 'save it'. Keep the title under 8 words. "
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
    internal static string SystemPromptFor(int? maxSteps, bool proposeChecks = false, int? turnCeiling = null)
    {
        var prompt = SystemPrompt;

        if (turnCeiling is > 0)
            prompt += $" HOW BIG A STEP MAY BE: a step runs as ONE conversation with the worker, and "
                    + "everything said in it is re-sent on every turn of it — so one step of 200 "
                    + "turns costs several times what two steps of 100 cost, and a step that runs "
                    + $"past {turnCeiling} turns is ABANDONED, with every step that depends on it "
                    + "skipped. Give each step a size that is known before it starts. When work "
                    + "repeats over many items — files, pages, records, tickets — do not write one "
                    + "step for all of them: say how many at a time and use a step per batch "
                    + "(\"the first 5 …\", \"the next 5 …\"), which is what the step budget is for. "
                    // Measured 2026-09-24, run ae2015: four batch steps verified pages and wrote
                    // nothing, and a fifth "Write findings" step depended on all four. One batch got
                    // stuck and was stopped - and the fifth was skipped, so the three batches that
                    // finished lost their work too. Nothing about that is particular to a model or to
                    // pages: any plan whose only writing is its last step is as strong as its weakest
                    // batch.
                    + "Each batch step SAVES its own results before it finishes - added to the output "
                    + "the request names, or to a file of its own - rather than leaving them for a "
                    + "later step to write: a final step that writes for all the batches depends on "
                    + "every one of them, and a single batch that fails then loses the work of all. "
                    + "This is about REPEATED work only: the rule above still holds for one action, "
                    + "which is never split into stages. "
                    // Measured 2026-09-24, run 4f779e: "write new tests" and "mutation-check new
                    // tests" were two steps for a request that said, of every test, "it must FAIL if
                    // the behaviour it describes is broken - check that by breaking it temporarily
                    // and putting it back". Step 2 wrote 4 tests and broke-and-restored the source
                    // 17 times confirming them; step 3 then did the SAME 17 breaks again, because
                    // the check the request attached to each test was not part of the step that
                    // created it. Same shape as the batch rule above, one level down: the unit the
                    // request names its requirement about is the unit that requirement stays with.
                    + "A CHECK the request attaches to EVERY item a step produces - \"each test must fail if its "
                    + "behaviour is broken\", \"every page must cite its source\" - belongs IN the step that "
                    + "produces the item, not in a step of its own: a later step re-doing the same check per item "
                    + "is the item's own work, done twice.";

        if (maxSteps is > 0)
            prompt += $" This run may take at most {maxSteps} step(s) in total — a plan longer than that "
                    + "stops partway with the work unfinished, so do not exceed it.";

        if (proposeChecks)
            prompt += ChecksPrompt;

        return prompt;
    }

    /// <summary>
    /// Asking the planner how the work will be PROVED, not just what it is.
    ///
    /// <para><b>This is the moment to ask, and the only one.</b> A check written now cannot be
    /// fitted to the result, because there is no result yet — the planner has seen the request and
    /// where it runs, and nothing else. Asked afterwards, "did it work" is answered by the same
    /// model that did the work, which is the thing this engine exists not to rely on.</para>
    ///
    /// <para><b>Empty is a real answer and is said twice.</b> Most of what people ask for cannot
    /// be proved by running something, and a model that feels obliged to produce a check will
    /// produce <c>echo done</c>. That is worse than nothing: it looks like verification in the run
    /// report.</para>
    ///
    /// <para>Appended rather than written into <see cref="SystemPrompt"/> so that a host which
    /// turns proposed checks off pays nothing for them — no tokens, and no invitation the engine
    /// will then ignore.</para>
    /// </summary>
    /// <summary>
    /// The shape a plan-with-checks would be asked for in — WRITTEN, NOT WIRED UP.
    ///
    /// <para>An outside review suggested on 2026-09-23 that this call should stop describing its
    /// JSON in prose and send a schema instead, the machinery having existed since §9c. It was
    /// wired up, and <c>StructuredOutputTests.The_planner_is_not_given_a_schema</c> refused it
    /// within the minute, holding a decision already taken and already argued:</para>
    ///
    /// <para><i>"the planner is the one place where the quality of the REASONING matters more than
    /// the shape of the answer, and constrained decoding on a 12-14B model can eat exactly what we
    /// go there for. The reviewer has nothing to reason about, which is why it went first. Measure
    /// before changing this."</i></para>
    ///
    /// <para>Which is the whole answer to the suggestion, and it is not an argument about token
    /// counts: on the machine this runs on, the planner may be a 4B. It is kept here so the
    /// measurement that comment asks for has something to measure — bind it, run the same request
    /// fifteen times on each planner, and compare the share of runs that produce provable checks.
    /// A constant nobody calls is normally debt; this one is an experiment waiting for its
    /// evidence, and it says so.</para>
    /// </summary>
    internal const string PlanWithChecksSchema = """
        {
          "type": "object",
          "properties": {
            "checks": {
              "type": "array",
              "maxItems": 4,
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "command": { "type": "string" }
                },
                "required": ["name", "command"],
                "additionalProperties": false
              }
            }
          }
        }
        """;

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
    /// <para>Sent alongside <see cref="ChecksSchema"/>, never instead of it: §9c's rule is that a
    /// schema may never be the thing correctness rests on, because "OpenAI-compatible" is a family
    /// rather than a specification and the models most in need of the text are the likeliest to
    /// ignore the schema.</para>
    /// </summary>
    private const string ChecksPrompt =
        " Also return \"checks\": up to 4 shell commands that would PROVE this request has been "
        + "carried out, or [] when nothing about it can be proved by running something. Shape: "
        + "\"checks\":[{\"name\":\"short name\",\"command\":\"...\"}]. "
        + "EACH ONE IS RUN BY run_command, which is cmd.exe on Windows and /bin/sh elsewhere - the "
        + "host is named above the request, and a check written for the wrong one of those simply "
        + "never runs. On Windows, cmd.exe has no PowerShell cmdlets (Select-String, Get-Content, "
        + "Test-Path) and no Unix tools (grep, awk, test, [ ]): use the program itself (dotnet, "
        + "git, npm), or findstr, or wrap PowerShell explicitly as "
        + "powershell -NoProfile -Command \"...\". "
        + "THE EXIT CODE IS THE WHOLE VERDICT: 0 means done, anything else means not done, and "
        + "nothing reads the output. Every check is run BEFORE the work as well as after, so write "
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

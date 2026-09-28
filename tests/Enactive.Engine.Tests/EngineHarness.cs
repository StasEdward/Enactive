namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Memory;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Core.Mail;
using Enactive.Tools;
using Enactive.Workspace;

/// <summary>
/// One scripted assistant turn. The engine is driven entirely by what a provider streams back, so a
/// turn is the unit a test writes: either plain text, or one or more structured tool calls.
/// </summary>
/// <summary>
/// A request as it was SENT - its messages, their tool calls and its tools copied at that moment.
///
/// <para>The engine hands a provider its live conversation and goes on appending to it, replacing
/// messages when it trims and clearing it when it hands over. A fake that kept the request object
/// kept a VIEW of that list: a check on what the model saw on turn 3 read the conversation as it
/// stood after the run, with replies from later turns in it and trimmed ones gone. Measured
/// 2026-09-24 (Docs/PROVIDERS_AGENTS_TOOLS_TESTS_REVIEW_2026-09-24.md #2): a first request sent with
/// two messages held five by the end of the run, including a reply that did not exist yet.</para>
/// </summary>
internal static class RequestSnapshot
{
    public static ChatRequest Of(ChatRequest request)
        => request with
        {
            Messages = request.Messages.Select(m => m with { ToolCalls = m.ToolCalls?.ToArray() }).ToArray(),
            Tools = request.Tools?.ToArray()
        };
}

public sealed record Turn(
    string? Text = null, IReadOnlyList<ToolCall>? Calls = null, string? FinishReason = "stop",
    // Tokens this turn reports. Null on both = a provider that does not count, which is a real case
    // and a different fact from zero. Set them and the turn emits a UsageDelta, which is the only
    // way anything about token accounting can be tested at all.
    int? PromptTokens = null, int? CompletionTokens = null,
    // A reasoning model's own deliberation, which some providers return in a field of its own. A
    // turn that has ONLY this is the shape that used to arrive as an inexplicable silence.
    string? Thinking = null)
{
    public static Turn Says(string text) => new(text);

    /// <summary>A turn spent entirely on reasoning: tokens generated, nothing delivered.</summary>
    public static Turn Thinks(string reasoning, int tokens = 15)
        => new(null, null, "stop", PromptTokens: 100, CompletionTokens: tokens, Thinking: reasoning);

    /// <summary>A turn that produced nothing at all - no text, no call, no reasoning we can see.</summary>
    public static Turn Silent(int tokens = 15)
        => new(null, null, "stop", PromptTokens: 100, CompletionTokens: tokens);

    public static Turn Calls1(string name, string argsJson, string id = "call_1")
        => new(null, new[] { new ToolCall(id, name, argsJson) });

    /// <summary>The same turn, reporting tokens.</summary>
    public Turn Reporting(int prompt = 10, int completion = 5)
        => this with { PromptTokens = prompt, CompletionTokens = completion };
}

/// <summary>
/// A provider that replays a script instead of calling a model. Every turn it hands out is recorded,
/// and so is every request it received — which is what lets a test assert on what the engine SENT
/// (the tool list a role was offered, the repair message after a non-call reply) and not only on
/// what it did afterwards.
/// </summary>
public sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<Turn> _script;

    public FakeChatProvider(params Turn[] script) => _script = new Queue<Turn>(script);

    /// <summary>
    /// What every call past the end of the script returns. A settable property rather than a second
    /// constructor parameter on purpose: with a `params` tail, an overload taking a leading Turn
    /// silently swallows the FIRST scripted turn, and the test then fails for the wrong reason.
    /// </summary>
    public Turn WhenExhausted { get; set; } = Turn.Says("done");

    /// <summary>Every request the engine made, in order.</summary>
    public List<ChatRequest> Requests { get; } = new();

    /// <summary>
    /// The hard prompt+generation window this provider claims, as Ollama's num_ctx is one. Null -
    /// the default - is every cloud provider: no stated window, so the context guard does not apply.
    /// </summary>
    public int? Window { get; set; }

    public int? ContextWindow(ChatRequest request) => Window;

    /// <summary>What ProviderConfig.AnswerReserveTokens would say. Null = the engine's default.</summary>
    public int? Reserve { get; set; }

    /// <summary>What ProviderConfig.HandoverAtPercent would say. Null = hand over by turns.</summary>
    public int? HandoverAt { get; set; }

    public int? AnswerReserve(ChatRequest request) => Reserve;

    public int? HandoverAtPercent(ChatRequest request) => HandoverAt;

    /// <summary>What ProviderConfig.WorkingContextTokens would say. Null = derived from the window, as before.</summary>
    public int? Working { get; set; }

    public int? WorkingContext(ChatRequest request) => Working;
    public int ReasoningTokens { get; set; }
    public int ReasoningAllowance(ChatRequest request) => ReasoningTokens;

    public int TurnsLeft => _script.Count;

    /// <summary>
    /// Answers a request by what it ASKS, before the script is consulted - null to fall through.
    /// For the requests a test cannot place by position: a handover's note is asked for whenever the
    /// engine decides, and a script that guesses the turn tests the guess.
    /// </summary>
    public Func<ChatRequest, Turn?>? Answering { get; set; }

    private Turn Next(ChatRequest request)
        => Answering?.Invoke(request) ?? (_script.Count > 0 ? _script.Dequeue() : WhenExhausted);

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        request = RequestSnapshot.Of(request);
        Requests.Add(request);
        var turn = Next(request);

        if (turn.Thinking is { Length: > 0 } reasoning)
            yield return new ReasoningDelta(reasoning);

        if (turn.Text is { Length: > 0 } text)
            yield return new TextDelta(text);

        if (turn.Calls is { Count: > 0 } calls)
            for (var i = 0; i < calls.Count; i++)
                yield return new ToolCallDelta(i, calls[i].Id, calls[i].Name, calls[i].ArgumentsJson);

        if (turn.PromptTokens is not null || turn.CompletionTokens is not null)
            yield return new UsageDelta(turn.PromptTokens, turn.CompletionTokens);

        yield return new FinishDelta(turn.FinishReason);
        await Task.CompletedTask;
    }

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        request = RequestSnapshot.Of(request);
        Requests.Add(request);
        var turn = Next(request);

        // The turn's tokens are reported here too, not only on the streaming path. Planning and
        // review are the two phases that go through CompleteAsync, and returning null here made
        // their cost untestable — which is precisely how it went uncounted in the first place.
        return Task.FromResult(new ChatCompletion(
            new ChatMessage(ChatRole.Assistant, turn.Text, turn.Calls), turn.FinishReason,
            turn.PromptTokens, turn.CompletionTokens, turn.Thinking));
    }
}

/// <summary>A provider whose every call throws — for the failure paths.</summary>
public sealed class ThrowingChatProvider : IChatProvider
{
    private readonly string _message;

    public ThrowingChatProvider(string message = "provider exploded") => _message = message;

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException(_message);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        => throw new InvalidOperationException(_message);
}

/// <summary>Hands the same provider to every provider id.</summary>
public sealed class SingleProviderFactory : IChatProviderFactory
{
    private readonly IChatProvider _provider;
    public SingleProviderFactory(IChatProvider provider) => _provider = provider;
    public IChatProvider Create(string providerId) => _provider;
}

/// <summary>
/// Routes each provider id to its own provider, with a fallback. Lets a test give the reviewer a
/// different script — or a broken endpoint — from the one the worker is running on.
/// </summary>
public sealed class MapProviderFactory : IChatProviderFactory
{
    private readonly Dictionary<string, IChatProvider> _byId;
    private readonly IChatProvider _fallback;

    public MapProviderFactory(IChatProvider fallback, params (string Id, IChatProvider Provider)[] map)
    {
        _fallback = fallback;
        _byId = map.ToDictionary(x => x.Id, x => x.Provider, StringComparer.OrdinalIgnoreCase);
    }

    public IChatProvider Create(string providerId)
        => _byId.TryGetValue(providerId, out var provider) ? provider : _fallback;
}

/// <summary>A reviewer script: the verdict shape <see cref="Reviewer"/> parses.</summary>
public static class Verdicts
{
    public static Turn Combined(Turn proof, string scope = "S1", params string[] ids)
    {
        var p = System.Text.Json.Nodes.JsonNode.Parse(proof.Text!)!;
        var claims = new System.Text.Json.Nodes.JsonArray();
        foreach (var id in ids.Length == 0 ? new[] { "O001" } : ids)
        {
            var claim = p.DeepClone().AsObject();
            if (claim["shown"]!.GetValue<string>() == "nothing-to-do")
                claim["shown"] = "yes"; // Whole-step no-op proof is separate from requirement evidence.
            claim["id"] = id;
            claim["scope"] = scope;
            var requirement = claim.DeepClone().AsObject();
            requirement.Remove("id");
            requirement["requirement"] = "Requirement for " + id;
            requirement["global"] = false;
            requirement["verification"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {"verdict":"not-applicable","reason":"This fixture reviews work without a separate test obligation","detects":"","assertions":[]}
                """);
            requirement["prohibitions"] = new System.Text.Json.Nodes.JsonArray();
            claim["requirements"] = new System.Text.Json.Nodes.JsonArray(requirement);
            claims.Add(claim);
        }
        return Turn.Says(new System.Text.Json.Nodes.JsonObject {
            ["verdict"] = "pass", ["notes"] = "checked", ["proof"] = p,
            ["assessments"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {"implementation":{"verdict":"pass","reason":"Implementation checked"},
                 "verification":{"verdict":"pass","reason":"Verification checked"},
                 "report":{"verdict":"pass","reason":"Report checked"}}
                """),
            ["claims"] = claims, ["need_evidence"] = new System.Text.Json.Nodes.JsonArray(),
            ["repairs"] = new System.Text.Json.Nodes.JsonArray(),
            ["report_checks"] = new System.Text.Json.Nodes.JsonArray(),
            ["command_reports"] = new System.Text.Json.Nodes.JsonArray()
        }.ToJsonString());
    }
    public static System.Text.Json.Nodes.JsonObject Repair(string finding, string defect,
        string change = "Correct the identified requirement", string source = "", string fragment = "")
        => new() {
            ["findings"] = new System.Text.Json.Nodes.JsonArray(finding),
            ["target"] = source == "" ? "work" : "source", ["source_id"] = source, ["fragment_id"] = fragment,
            ["defect"] = defect, ["change"] = change,
            ["obligation_ids"] = new System.Text.Json.Nodes.JsonArray("O001")
        };
    public const string ProviderId = "review";
    public const string Model = "reviewer-model";

    public static Turn Fail(string notes = "the evidence does not support the claim")
        => Turn.Says($$"""{"verdict":"fail","notes":"{{notes}}"}""");

    public static Turn Pass(string notes = "looks right")
        => Turn.Says($$"""{"verdict":"pass","notes":"{{notes}}"}""");

    // ── the proof pass ──────────────────────────────────────────────────────
    //
    // A second turn on the SAME provider, asked only of a step the verdict above already passed.
    // A test that switches soundness on scripts a Pass and then one of these.

    /// <summary>"The evidence shows it, and here is which call shows it."</summary>
    public static Turn Shown(string what = "the command ran and succeeded", params int[] calls)
        => Turn.Says($$"""
            {"shown":"yes","calls":[{{string.Join(",", calls)}}],"what":"{{what}}"}
            """);

    /// <summary>"A call could have shown it, and none of these does." The defect this pass exists for.</summary>
    public static Turn NotShown(string what = "the test named in the report is still failing")
        => Turn.Says($$"""{"shown":"no","calls":[],"what":"{{what}}"}""");

    /// <summary>"No tool call settles this." An analysis, a document, a judgement.</summary>
    public static Turn NotByAnyCall(string what = "this step's work was reading and reasoning")
        => Turn.Says($$"""{"shown":"not-by-any-call","calls":[],"what":"{{what}}"}""");

    /// <summary>
    /// "The objective was conditional and the calls show it did not need doing." Takes call numbers
    /// like <see cref="Shown"/> and for the same reason: unlike NotByAnyCall this answer is audited,
    /// because nothing needing doing is a finding.
    /// </summary>
    public static Turn NothingToDo(string what = "the file already says what the code does",
                                   params int[] calls)
        => Turn.Says($$"""
            {"shown":"nothing-to-do","calls":[{{string.Join(",", calls)}}],"what":"{{what}}"}
            """);
}

/// <summary>Model routing for a test: by default nothing is bound, so there is no reviewer.</summary>
public static class Routers
{
    /// <summary>Binds the Review phase, which is what switches the reviewer on at all.</summary>
    public static IModelRouter WithReviewer()
        => new ModelRouter(
            new ModelResolver(),
            new Dictionary<ModelPurpose, ModelRef>
            {
                [ModelPurpose.Review] = new ModelRef(Verdicts.ProviderId, Verdicts.Model)
            });

    public const string PlannerProviderId = "planner";

    /// <summary>
    /// Puts the planner on its own provider id so a test can break the EXECUTING model without also
    /// breaking planning — which is what it takes to watch the execute-side fallback on its own.
    /// </summary>
    public static IModelRouter WithPlannerOn(string providerId = PlannerProviderId)
        => new ModelRouter(
            new ModelResolver(),
            new Dictionary<ModelPurpose, ModelRef>
            {
                [ModelPurpose.Plan] = new ModelRef(providerId, "plan-model")
            });

    public const string LightProviderId = "light";
    public const string HeavyProviderId = "heavy";

    /// <summary>
    /// Binds the per-complexity Execute models, which is the lever the planner's complexity rating
    /// actually pulls: rate a step "complex" and it runs on the expensive model.
    /// </summary>
    public static IModelRouter WithComplexityRouting()
        => new ModelRouter(
            new ModelResolver(),
            new Dictionary<ModelPurpose, ModelRef>(),
            executeLight: new ModelRef(LightProviderId, "small-model"),
            executeHeavy: new ModelRef(HeavyProviderId, "expensive-model"));
}

/// <summary>Answers every approval request the same way. Records what it was shown.</summary>
public sealed class ScriptedDecisionHandler : IDecisionHandler
{
    public ScriptedDecisionHandler(string answer = "approve") => Answer = answer;

    /// <summary>
    /// What it says every time. Settable so a test can put the fixture's own handler on "allow" or
    /// "deny" — the option id the orchestrator actually checks is "allow", and anything else is a
    /// refusal, which is a distinction worth being able to exercise.
    /// </summary>
    public string Answer { get; set; }

    /// <summary>
    /// Answers for the first few asks, in order, before falling back to <see cref="Answer"/>. One
    /// run can then be refused and then allowed, which is what it takes to check that a refusal
    /// stops counting once the same call goes through.
    /// </summary>
    public Queue<string> Script { get; } = new();

    public List<DecisionRequest> Requests { get; } = new();

    /// <summary>
    /// How the answer was reached, when it was not reached by a person just now - what a real UI
    /// puts here when a standing approval answered instead of a click.
    /// </summary>
    public string? Because { get; set; }

    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(
            new DecisionOutcome(Script.Count > 0 ? Script.Dequeue() : Answer, Because));
    }
}

/// <summary>
/// A throwaway workspace folder plus the wiring an Orchestrator needs. Everything is a real
/// component except the provider: the point of the harness is to exercise the ENGINE, so the tools,
/// the artifact store, the permission engine and the DAG scheduler are the shipping ones.
/// </summary>
public sealed class EngineFixture : IDisposable
{
    public EngineFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Workspace = WorkspaceInfo.For(Root);
        Artifacts = new DiskArtifactStore(Workspace);
    }

    public string Root { get; }
    public WorkspaceInfo Workspace { get; }
    public DiskArtifactStore Artifacts { get; }
    public ScriptedDecisionHandler Decisions { get; } = new();
    public GenerationBudgets? GenerationBudgetsOverride { get; set; }
    // Most fixtures isolate execution/review using pre-reviewed plans; contract integration tests use the production planner.
    public bool ProposeChecks { get; set; } = true;
    public Planner? PlannerOverride { get; set; }
    public RepairConsultation? RepairConsultationOverride { get; set; }

    public IWorkspaceChangesFactory? WorkspaceChangesOverride { get; set; }

    public ITool[]? ToolsOverride { get; set; }

    /// <summary>
    /// Invokes a tool the way the engine does: a fresh context over this fixture's workspace, with
    /// this fixture's journalled artifact store unless the caller hands in a step's own.
    ///
    /// <para>It lives on the fixture rather than on whichever test class needed it first. It used
    /// to be a private helper on <c>FileToolsTests</c>, which was fine right up until a second and
    /// third file tool arrived with test classes of their own and could not reach it.</para>
    /// </summary>
    public Task<ToolResult> Invoke(ITool tool, string argumentsJson, IArtifactStore? store = null)
        => tool.InvokeAsync(argumentsJson, ContextFor(store), CancellationToken.None);

    /// <summary>The context a tool is called with here: permissive policy, this workspace, this root.</summary>
    public ToolContext ContextFor(IArtifactStore? store = null)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: Root, Artifacts: store ?? Artifacts, Services: null!);

    public string PathOf(string relative) => Path.Combine(Root, relative);
    public bool Exists(string relative) => File.Exists(PathOf(relative));
    public string Read(string relative) => File.ReadAllText(PathOf(relative));
    public void Write(string relative, string content)
    {
        var full = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Writes the same text with the line endings a test asks for.
    ///
    /// <para>Every test but a handful handed <see cref="Write"/> a string full of <c>\n</c>, so the
    /// world the suite exercised was LF-only — and the product ships on Windows, where most text
    /// files are not. <c>edit_file</c> was therefore never tried against a CRLF file until the day a
    /// user found it refusing every edit on one. A test that wants an ending now says which, and
    /// <see cref="Endings"/> makes running a test over both a one-line change.</para>
    /// </summary>
    public void Write(string relative, string content, Newline endings)
        => Write(relative, Normalize(content, endings));

    /// <summary>The same text with exactly one kind of line ending, whatever it arrived with.</summary>
    public static string Normalize(string content, Newline endings)
    {
        var lf = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return endings == Newline.Lf ? lf : lf.Replace("\n", "\r\n");
    }

    /// <summary>Both, for a <c>[Theory]</c>: a file tool has to work on either.</summary>
    public static TheoryData<Newline> Endings => new() { Newline.Lf, Newline.Crlf };

    /// <summary>A worker with the given tool allowlist; everything else is the Developer role.</summary>
    ///
    /// <remarks>
    /// Prefer <see cref="Role"/> for anything that could depend on what a role may actually DO. A
    /// hand-written allowlist here is a world of the test's own making, and a tool missing from a
    /// SHIPPING role is invisible to it by construction — which is exactly how edit_file shipped
    /// registered, documented, and reachable by nobody.
    /// </remarks>
    public static Worker WorkerWith(params string[] tools)
        => new("developer", "Developer", "You are a developer.", tools,
               PermissionLevel.Execute, new ModelPolicy(new ModelRef("fake", "fake-model")));

    /// <summary>
    /// The run's worker, plus every role this build ships, as the TEAM the orchestrator sees.
    ///
    /// <para>The worker under test is still the one that runs and still the one the role gate is
    /// applied with. Only <c>IWorkerProvider.All</c> grows, and in the engine that is read by
    /// exactly one thing: <c>ToolReach.Unnamed</c>, which asks "does ANY role name this tool".</para>
    ///
    /// <para><b>Why the team has to be a team.</b> A fixture worker is usually a stripped-down
    /// allowlist built to exercise the role gate — <c>WorkerWith("write_file", "read_file")</c> —
    /// and against a team of one, "no role names send_email" is true of it, which is a fact about
    /// the fixture and not about the product. Handing the engine the shipped roles alongside is
    /// what makes the question mean in a test what it means in a run.</para>
    ///
    /// <para><c>WorkerWith</c> reuses the id "developer", so a shipped role it displaces is kept
    /// under a suffixed id. Nothing resolves workers by id here except <c>Get</c>, which is given
    /// the worker under test, so the suffix is invisible — and dropping the displaced role instead
    /// would delete an allowlist the real configuration has.</para>
    /// </summary>
    private static IWorkerProvider TeamAround(Worker worker)
    {
        var team = new List<Worker> { worker };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { worker.Id };

        foreach (var shipped in DefaultWorkers.Seed(new ModelRef("fake", "fake-model")))
        {
            var id = taken.Add(shipped.Id) ? shipped.Id : shipped.Id + "-as-shipped";
            taken.Add(id);
            team.Add(shipped with { Id = id });
        }

        return new StaticWorkerProvider(team, worker.Id);
    }

    /// <summary>A role exactly as this build ships it — allowlist, level and instructions.</summary>
    public static Worker Role(string id)
        => DefaultWorkers.Seed(new ModelRef("fake", "fake-model")).Single(w => w.Id == id);

    /// <summary>Every role this build ships, by id — for a <c>[Theory]</c> that must cover all of them.</summary>
    public static TheoryData<string> ShippingRoles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var worker in DefaultWorkers.Seed(new ModelRef("fake", "fake-model")))
                data.Add(worker.Id);
            return data;
        }
    }

    public Orchestrator Build(
        IChatProvider provider,
        Worker? worker = null,
        PermissionPolicy? policy = null,
        IArtifactStore? artifacts = null,
        bool allowImplicitToolCalls = false,
        IModelRouter? router = null,
        IChatProvider? reviewProvider = null,
        int reviewRetries = 1,
        bool reviewContent = true,
        bool revertRejectedSteps = true,
        IReadOnlyList<SuccessCriterionDefinition>? successCriteria = null,
        ExecutionLimits? limits = null,
        int maxParallelSteps = 1,
        int evidenceBudget = ExecutionJournal.DefaultBudget,
        IDecisionHandler? decisions = null,
        int successRetries = 0,
        IRunCheckpointStore? checkpoints = null,
        RunSettings? settings = null,
        bool checkSoundness = false,
        IReadOnlyList<Worker>? team = null)
        => Build(
            reviewProvider is null
                ? new SingleProviderFactory(provider)
                : new MapProviderFactory(provider, (Verdicts.ProviderId, reviewProvider)),
            worker, policy, artifacts, allowImplicitToolCalls, router, reviewRetries, reviewContent,
            revertRejectedSteps, successCriteria, limits, maxParallelSteps, evidenceBudget, successRetries,
            decisions, checkpoints, settings, checkSoundness, team);

    public Orchestrator Build(
        IChatProviderFactory providers,
        Worker? worker = null,
        PermissionPolicy? policy = null,
        IArtifactStore? artifacts = null,
        bool allowImplicitToolCalls = false,
        IModelRouter? router = null,
        int reviewRetries = 1,
        bool reviewContent = true,
        bool revertRejectedSteps = true,
        IReadOnlyList<SuccessCriterionDefinition>? successCriteria = null,
        ExecutionLimits? limits = null,
        // The parallel dispatcher had no way to be built here at all, which is the whole reason it
        // shipped covered by nothing: every test in the suite ran the maxParallel == 1 branch
        // because that was the only branch this harness could reach.
        int maxParallelSteps = 1,
        int evidenceBudget = ExecutionJournal.DefaultBudget,
        // 0 by default here, not the shipping 1: a test that says nothing about repair should get
        // the shape it was written for - check the criteria once and stop. The repair loop has its
        // own tests, which ask for it.
        int successRetries = 0,
        // Supplied only when a test needs to watch the handler itself — the approval gate can only
        // be checked from inside the thing the gate protects.
        IDecisionHandler? decisions = null,
        // Null by default: an orchestrator with no checkpoint store writes none, which is what every
        // test written before resume existed expects.
        IRunCheckpointStore? checkpoints = null,
        RunSettings? settings = null,
        // OFF by default here, unlike the shipping default. The proof pass is a SECOND call on the
        // review provider, so leaving it on would silently add a turn to every review script in the
        // suite - and a test that says nothing about soundness should get the shape it was written
        // for. Same reasoning as successRetries above.
        bool checkSoundness = false,
        // The whole TEAM, when a test needs to say what it is. Left out, the worker under test is
        // surrounded by the shipped roles - see TeamAround for why that is the honest default.
        IReadOnlyList<Worker>? team = null)
    {
        // The set a host registers, not a convenient subset: a role's allowlist can only be
        // exercised against the tools that actually exist, and git/docker were missing here while
        // both shipping hosts register them.
        var tools = new ToolRegistry(ToolsOverride ?? ShippedTools());

        return new Orchestrator(WorkspaceChangesOverride ?? new Enactive.Workspace.WorkspaceChangesFactory(),
            providers,
            new ModelResolver(),
            team is { Count: > 0 }
                ? new StaticWorkerProvider(team, (worker ?? team[0]).Id)
                : TeamAround(worker ?? WorkerWith("write_file", "read_file", "list_dir", "run_command")),
            tools,
            artifacts ?? Artifacts,
            Workspace,
            PlannerOverride ?? new Planner(checksAuditEnabled: false),
            new PermissionEngine(),
            decisions ?? Decisions,
            policy ?? PermissionPolicy.PermissiveDefault,
            new EmptyServices(),
            proposeChecks: ProposeChecks,
            generationBudgets: GenerationBudgetsOverride,
            repairConsultation: RepairConsultationOverride,
            router: router,
            reviewRetries: reviewRetries,
            allowImplicitToolCalls: allowImplicitToolCalls,
            reviewContent: reviewContent,
            revertRejectedSteps: revertRejectedSteps,
            successCriteria: successCriteria,
            limits: limits,
            maxParallelSteps: maxParallelSteps,
            evidenceBudget: evidenceBudget,
            successRetries: successRetries,
            checkpoints: checkpoints,
            settings: settings,
            checkSoundness: checkSoundness);
    }

    /// <summary>Runs one intent to completion and returns every event it produced.</summary>
    /// <summary>
    /// Runs one intent to completion and returns every event it produced.
    ///
    /// <para>Pass <paramref name="memory"/> to run the way a host does: the context is assembled by
    /// a real <see cref="ContextProvider"/> over that store, and the stream goes through a
    /// <see cref="RunRecorder"/> that folds the result back into it. That is the whole loop - read
    /// the project's memory into the run, write the run's conclusion back - and testing either half
    /// on its own would miss the join.</para>
    /// </summary>
    public async Task<List<WorkEvent>> RunAsync(
        Orchestrator orchestrator, string request, IMemoryStore? memory = null)
    {
        var context = memory is null
            ? new WorkContext(
                Workspace.Id, Workspace.Name, null, null, null,
                Array.Empty<string>(), Array.Empty<string>())
            : await new ContextProvider(Workspace, null, memory)
                .BuildAsync(new IntentFocus(Workspace.Id), CancellationToken.None);

        var intent = new Intent(Guid.NewGuid(), request, IntentSource.CommandBar, context, DateTimeOffset.UtcNow);

        var events = new List<WorkEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var stream = orchestrator.SubmitIntentAsync(intent, cts.Token);
        if (memory is not null)
            stream = new RunRecorder(new NowhereRunStore(), memory, Workspace.Id).RecordAsync(stream, cts.Token);

        await foreach (var ev in stream)
            events.Add(ev);
        return events;
    }

    /// <summary>
    /// Picks a checkpoint up, the way a host does: a FRESH context, because what is on the machine
    /// and what the project has decided are facts about now rather than about the run that stopped.
    /// </summary>
    public async Task<List<WorkEvent>> ResumeAsync(Orchestrator orchestrator, RunCheckpoint checkpoint)
    {
        var context = new WorkContext(
            Workspace.Id, Workspace.Name, null, null, null,
            Array.Empty<string>(), Array.Empty<string>());

        var events = new List<WorkEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var ev in orchestrator.ResumeRunAsync(checkpoint, context, cts.Token))
            events.Add(ev);
        return events;
    }

    /// <summary>A run store that keeps nothing. These tests are about the MEMORY half.</summary>
    private sealed class NowhereRunStore : IRunStore
    {
        public Task SaveAsync(RunRecord record, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());

        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Every tool a shipping host registers. Kept here so one list serves the whole suite, and so
    /// "a tool nobody can reach" is a question a test can ask.
    /// </summary>
    /// <summary>
    /// One shipped tool by the name a model would call it by — so a test can sweep the whole set
    /// without naming each type, and a tool added to ShippedTools is covered the day it is added.
    /// </summary>
    public static ITool ToolNamed(string name)
        => ShippedTools().Single(t => t.Definition.Name == name);

    public static ITool[] ShippedTools() => BuiltInTools.Create(MailAccount.None);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* a temp folder that outlives a test is not a failure */ }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>Assertion helpers over an event list — the engine's only public output.</summary>
public static class EventAssertions
{
    public static bool Has(this IEnumerable<WorkEvent> events, EventKind kind)
        => events.Any(e => e.Kind == kind);

    public static string Text(this IEnumerable<WorkEvent> events)
        => string.Join("\n", events.Select(e => $"{e.Kind}: {e.Summary}"));

    public static IEnumerable<WorkEvent> OfKind(this IEnumerable<WorkEvent> events, EventKind kind)
        => events.Where(e => e.Kind == kind);

    /// <summary>
    /// A check somebody asked for - a person, the planner, the request - and not the engine's own
    /// look at the files it produced, which every run that writes a file now ends with. Tests that
    /// count the checks a run was given count these.
    /// </summary>
    public static bool IsCheck(this WorkEvent e)
        => e.Kind == EventKind.CriterionEvaluated
           && !(e.PayloadJson?.Contains("\"origin\":\"System\"", StringComparison.Ordinal) ?? false);
}

/// <summary>Which line endings a file is written with. The product ships on Windows; both are real.</summary>
public enum Newline
{
    Lf,
    Crlf
}

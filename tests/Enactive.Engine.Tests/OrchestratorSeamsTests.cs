namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Tools;
using Xunit;

public sealed class OrchestratorSeamsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Common_factory_uses_injected_success_evaluator_for_quick_and_dag(bool dag)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(dag
            ? """{"disposition":"task","title":"work","steps":[{"title":"answer","dependsOn":[]}]}"""
            : """{"disposition":"quick_action","title":"answer","steps":[]}"""), Turn.Says("answer"));
        var success = new SpySuccess();
        var worker = EngineFixture.WorkerWith();
        var criteria = new[] { new SuccessCriterionDefinition("check", "must never execute") };
        var resources = fx.Resources(new SingleProviderFactory(provider), worker, [worker]) with
        {
            Agents = new OrchestratorServices(success), SuccessCriteria = criteria
        };
        var options = EngineOptions.Default with { ProposeChecks = false, SuccessRetries = 0, ReviewRetries = 0 };
        var events = await fx.RunAsync(new Orchestrator(resources, options), "answer");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.True(success.Calls > 0);
        Assert.Equal(criteria, success.Criteria);
        // Only planning and worker use the real provider; injected components handle their phases.
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Common_factory_uses_injected_handover_when_the_worker_reaches_the_turn_boundary()
    {
        using var fx = new EngineFixture();
        var turns = new List<Turn>
        {
            Turn.Says("""{"disposition":"quick_action","title":"inspect","steps":[]}""")
        };
        for (var i = 0; i < 60; i++)
        {
            fx.Write($"f{i}.txt", $"data {i}");
            turns.Add(Turn.Calls1("read_file", $"{{\"path\":\"f{i}.txt\"}}", $"call{i}"));
        }
        turns.Add(Turn.Says("done"));
        var provider = new FakeChatProvider(turns.ToArray());
        var handover = new SpyHandover();
        var worker = EngineFixture.WorkerWith("read_file");
        var resources = fx.Resources(new SingleProviderFactory(provider), worker, [worker]) with
        {
            Agents = new OrchestratorServices(Handover: handover)
        };
        var options = EngineOptions.Default with { ProposeChecks = false };
        var events = await fx.RunAsync(new Orchestrator(resources, options), "Inspect the files");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(1, handover.Calls);
        Assert.Contains(provider.Requests.Last().Messages,
            m => m.Content?.Contains("injected handover facts", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("stop", false, "note")]
    [InlineData("length", false, null)]
    [InlineData("max_tokens", false, null)]
    [InlineData("stop", true, null)]
    public async Task Handover_rejects_unfinished_notes_and_accounts_for_the_response(
        string finish, bool tool, string? expected)
    {
        var turn = new Turn("  note  ", tool ? [new ToolCall("id", "read_file", "{}")] : null,
            finish, PromptTokens: 17, CompletionTokens: 3);
        var provider = new FakeChatProvider(turn);
        var request = new ChatRequest("model", [ChatMessage.User("original")], MaxTokens: 2048);
        var budget = RunBudget.Unlimited();
        Assert.Equal(expected, (await new Handover().GenerateAsync(provider, request, budget, default)).Note);
        Assert.Equal(20, budget.TokensSpent);
        Assert.Single(request.Messages);
        Assert.Equal(2048, provider.Requests[0].MaxTokens);
        Assert.Equal("original", provider.Requests[0].Messages[0].Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handover_provider_failure_is_optional_but_cancellation_propagates(bool cancel)
    {
        var provider = new FailingProvider(cancel ? new OperationCanceledException() : new IOException("offline"));
        var task = new Handover().GenerateAsync(provider, new ChatRequest("model", []), RunBudget.Unlimited(), default);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else
        {
            var result = await task;
            Assert.Null(result.Note);
            Assert.Equal(HandoverFailure.ProviderError, result.Failure);
            Assert.Contains("IOException: offline", result.Describe(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Each way a handover can come back without a note says which way it was, with what was measured -
    /// six faults used to come back as one indistinguishable null.
    /// </summary>
    [Theory]
    [InlineData("tool", HandoverFailure.ToolCall, "the model called read_file instead of writing it")]
    [InlineData("cut", HandoverFailure.Truncated, "the note was cut at the output limit (finish=length; 2048 output token(s) of 2048")]
    [InlineData("thinking", HandoverFailure.Truncated, "the output limit was reached while the model was still reasoning, before any note")]
    [InlineData("empty", HandoverFailure.Empty, "the model finished without writing anything")]
    public async Task A_handover_without_a_note_says_why(string shape, HandoverFailure failure, string said)
    {
        var turn = shape switch
        {
            "tool" => new Turn("I will read it", [new ToolCall("id", "read_file", "{}")], "tool_calls", 100, 20),
            "cut" => new Turn("Findings so far: the pa", null, "length", 100, 2048),
            "thinking" => new Turn(null, null, "length", 100, 2048, Thinking: "Let me think about what I found..."),
            _ => new Turn("   ", null, "stop", 100, 1)
        };
        var result = await new Handover().GenerateAsync(new FakeChatProvider(turn),
            new ChatRequest("model", [ChatMessage.User("work")], OutputTokenLimit: 2048), RunBudget.Unlimited(), default);

        Assert.Null(result.Note);
        Assert.Equal(failure, result.Failure);
        Assert.StartsWith(said, result.Describe(), StringComparison.Ordinal);
    }

    // ── what an orchestrator is built from ──────────────────────────────────

    /// <summary>
    /// An orchestrator is built from what its run is given and the engine's switches, and nothing else. It took
    /// twenty-three parameters until 2026-10-08, six nullable ones in a row, and the one composition that builds it
    /// for a host unpacked a record field by field into them. A new collaborator is a property of the resources.
    /// </summary>
    [Fact]
    public void An_orchestrator_is_built_from_its_resources_and_the_engine_s_switches()
    {
        var constructor = Assert.Single(typeof(Orchestrator).GetConstructors());

        Assert.Equal(new[] { typeof(RunEngineResources), typeof(EngineOptions) },
            constructor.GetParameters().Select(p => p.ParameterType));
    }

    /// <summary>
    /// The folders a workspace was given for good are read from the store the run is given - not the machine's
    /// own - so a test, or a host with a store of its own, is not answered by another. Kept for this workspace,
    /// a command writing there is not asked about again; the person here would have said no.
    /// </summary>
    [Fact]
    public async Task A_folder_kept_in_the_store_the_run_was_given_is_not_asked_about()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "deny";
        var outside = Path.Combine(Path.GetTempPath(), "enactive-tests", "kept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var roots = new WritableRoots(outside + "-roots.json");
        Assert.Null(roots.Add(fx.Root, outside));
        var target = Path.Combine(outside, "out.txt");
        var provider = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"write","steps":[]}"""),
            Turn.Calls1("run_command", JsonSerializer.Serialize(new { command = $"echo hello> \"{target}\"" })), Turn.Says("done"));
        var worker = EngineFixture.Role("developer");

        try
        {
            await fx.RunAsync(new Orchestrator(fx.Resources(new SingleProviderFactory(provider), worker) with
            {
                WritableRoots = roots,
                Policy = new PermissionPolicy(PermissionLevel.Execute, ["*"], [])
            }, EngineOptions.Default with { ProposeChecks = false }), "write the file");

            Assert.DoesNotContain(fx.Decisions.Requests, r => r.Topic.Contains("outside the workspace", StringComparison.Ordinal));
            Assert.True(File.Exists(target));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); File.Delete(outside + "-roots.json"); } catch { /* a temp folder */ }
        }
    }

    private sealed class SpySuccess : ISuccessEvaluator
    {
        public int Calls;
        public IReadOnlyList<SuccessCriterionDefinition>? Criteria;
        public Task<SuccessReport> EvaluateAsync(IReadOnlyList<SuccessCriterionDefinition> criteria,
            IToolRegistry tools, IPermissionEngine permissions, PermissionPolicy policy,
            IDecisionHandler decisions, ToolContext context, Guid taskId, CancellationToken ct)
        { Calls++; Criteria = criteria; return Task.FromResult(SuccessReport.NothingToCheck); }
    }

    private sealed class SpyHandover : IHandover
    {
        public int Calls;
        public Task<HandoverResult> GenerateAsync(IChatProvider provider, ChatRequest step, RunBudget budget, CancellationToken ct, int? promptTokens = null)
        { Calls++; return Task.FromResult(HandoverResult.Written("injected handover facts")); }
    }
    private sealed class FailingProvider(Exception error) : IChatProvider
    {
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct) => Task.FromException<ChatCompletion>(error);
        public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

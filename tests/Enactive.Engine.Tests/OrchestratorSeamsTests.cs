namespace Enactive.Engine.Tests;

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
    public async Task Common_factory_uses_injected_reviewer_and_success_evaluator_for_quick_and_dag(bool dag)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(dag
            ? """{"disposition":"task","title":"work","steps":[{"title":"answer","dependsOn":[]}]}"""
            : """{"disposition":"quick_action","title":"answer","steps":[]}"""), Turn.Says("answer"));
        var reviewer = new SpyReviewer();
        var success = new SpySuccess();
        var worker = EngineFixture.WorkerWith();
        var models = new ModelResolver();
        var resources = new RunEngineResources(new SingleProviderFactory(provider), models,
            new StaticWorkerProvider([worker], worker.Id), new ToolRegistry(EngineFixture.ShippedTools()),
            fx.Artifacts, fx.Workspace, new Planner(), new PermissionEngine(), fx.Decisions,
            PermissionPolicy.PermissiveDefault, new Services(), Routers.WithReviewer(),
            new OrchestratorServices(reviewer, success));
        var criteria = new[] { new SuccessCriterionDefinition("check", "must never execute") };
        var options = RunEngineOptions.Capture(new AppSettings
            { ProposeChecks = false, CheckSoundness = false, SuccessRetries = 0, ReviewRetries = 0 });
        var events = await fx.RunAsync(RunEngineComposition.Build(resources, options, successCriteria: criteria), "answer");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(1, reviewer.Calls);
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
        var models = new ModelResolver();
        var resources = new RunEngineResources(new SingleProviderFactory(provider), models,
            new StaticWorkerProvider([worker], worker.Id), new ToolRegistry(EngineFixture.ShippedTools()),
            fx.Artifacts, fx.Workspace, new Planner(), new PermissionEngine(), fx.Decisions,
            PermissionPolicy.PermissiveDefault, new Services(), new ModelRouter(models),
            new OrchestratorServices(Handover: handover));
        var options = RunEngineOptions.Capture(new AppSettings { ProposeChecks = false, CheckSoundness = false });
        var events = await fx.RunAsync(RunEngineComposition.Build(resources, options), "Inspect the files");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(1, handover.Calls);
        Assert.Contains(provider.Requests.Last().Messages,
            m => m.Content?.Contains("injected handover facts", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Review_mode_uses_current_step_but_evidence_keeps_prior_calls()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", "build", ActionOutcome.Succeeded, "built");
        journal.Record(2, "write_file", "a.txt", ActionOutcome.Succeeded, "saved");
        var reviewer = new SpyReviewer();
        var stage = Stage(reviewer);
        var result = await stage.ExecuteAsync("write", "done", journal, 0, 1, ["a.txt"],
            [new WrittenFile("a.txt", "text")], new FakeChatProvider(), "model", default, "original request");
        Assert.Equal(ReviewMode.Content, result.Mode);
        Assert.False(reviewer.Combined);
        Assert.Contains("built", reviewer.Evidence);
        Assert.Equal("original request", reviewer.Request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Relocation_is_execution_review_and_soundness_switch_is_respected(bool soundness)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "copy_file", "a.txt", ActionOutcome.Succeeded, "copied");
        var reviewer = new SpyReviewer();
        var result = await Stage(reviewer, soundness).ExecuteAsync("copy", "done", journal, 0, 0, ["a.txt"],
            [new WrittenFile("a.txt", "existing text")], new FakeChatProvider(), "model", default);
        Assert.Equal(ReviewMode.Execution, result.Mode);
        Assert.Equal(soundness, reviewer.Combined);
    }

    [Fact]
    public async Task Combined_review_receives_visible_evidence_obligations_and_retry_budget_callback()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 100; i++)
            journal.Record(i + 1, "run_command", "build", ActionOutcome.Succeeded, new string('x', 150));
        var reviewer = new SpyReviewer();
        var obligations = RequestObligations.Create("Run exact command", "build");
        Func<int, int, string?> retry = (_, _) => "budget reached";
        await Stage(reviewer).ExecuteAsync("build", "done", journal, 0, 0, [], [],
            new FakeChatProvider(), "model", default, obligations: obligations, beforeRetry: retry);
        Assert.NotNull(reviewer.View);
        Assert.DoesNotContain(1, reviewer.View.VisibleActionIds);
        Assert.Same(obligations, reviewer.Obligations);
        Assert.Same(retry, reviewer.Retry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_exception_is_incomplete_but_cancellation_propagates(bool cancelled)
    {
        var reviewer = new SpyReviewer { Error = cancelled ? new OperationCanceledException() : new IOException("offline") };
        var operation = Stage(reviewer).ExecuteAsync("work", "done", new ExecutionJournal(), 0, 0, [], [],
            new FakeChatProvider(), "model", default);
        if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        else
        {
            var result = await operation;
            Assert.False(result.Result.Pass);
            Assert.Contains("offline", result.Result.IncompleteReason);
        }
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
        Assert.Equal(expected, await new Handover().GenerateAsync(provider, request, budget, default));
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
        else Assert.Null(await task);
    }

    private static StepReview Stage(IReviewer reviewer, bool soundness = true)
        => new(reviewer, new ToolRegistry(EngineFixture.ShippedTools()), "workspace", 1200, true, soundness);

    private sealed class Services : IServiceProvider { public object? GetService(Type type) => null; }

    private sealed class SpySuccess : ISuccessEvaluator
    {
        public int Calls;
        public IReadOnlyList<SuccessCriterionDefinition>? Criteria;
        public Task<SuccessReport> EvaluateAsync(IReadOnlyList<SuccessCriterionDefinition> criteria,
            IToolRegistry tools, IPermissionEngine permissions, PermissionPolicy policy,
            IDecisionHandler decisions, ToolContext context, Guid taskId, CancellationToken ct)
        { Calls++; Criteria = criteria; return Task.FromResult(SuccessReport.NothingToCheck); }
    }

    private sealed class SpyReviewer : IReviewer
    {
        public int Calls;
        public bool Combined;
        public Exception? Error;
        public string? Evidence, Request;
        public EvidenceView? View;
        public RequestObligations? Obligations;
        public Func<int, int, string?>? Retry;
        private Task<ReviewResult> Answer()
        {
            Calls++;
            return Error is null ? Task.FromResult(new ReviewResult(true, "verified")) : Task.FromException<ReviewResult>(Error);
        }
        public Task<ReviewResult> ReviewAsync(string stepTitle, string coderOutput, string executionEvidence,
            IReadOnlyList<string> artifacts, IChatProvider provider, string model, CancellationToken ct,
            ReviewMode mode = ReviewMode.Execution, IReadOnlyList<WrittenFile>? writtenFiles = null,
            string? request = null, RequestObligations? obligations = null)
        { Evidence = executionEvidence; Request = request; Obligations = obligations; return Answer(); }
        public Task<ReviewResult> ReviewWithProofAsync(string title, string report, EvidenceView evidence,
            IReadOnlyList<string> artifacts, IReadOnlyList<WrittenFile> files, RequestObligations obligations,
            IChatProvider provider, string model, CancellationToken ct, string? workspaceRoot = null,
            Func<int, int, string?>? beforeRetry = null, ReviewMode mode = ReviewMode.Execution)
        { Combined = true; View = evidence; Obligations = obligations; Retry = beforeRetry; return Answer(); }
    }
    private sealed class SpyHandover : IHandover
    {
        public int Calls;
        public Task<string?> GenerateAsync(IChatProvider provider, ChatRequest step, RunBudget budget, CancellationToken ct)
        { Calls++; return Task.FromResult<string?>("injected handover facts"); }
    }
    private sealed class FailingProvider(Exception error) : IChatProvider
    {
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct) => Task.FromException<ChatCompletion>(error);
        public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

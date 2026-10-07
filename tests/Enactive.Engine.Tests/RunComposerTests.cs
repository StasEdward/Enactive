namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Workers;
using Enactive.Mcp.TestServer;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Tools.Mcp;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Every run is composed by one module, wherever it was started - asked here the way a host asks it.
///
/// <para>Until 2026-10-08 a run was assembled by hand in the window, in the unattended path and in
/// the console, and the copies had drifted: the console connected no MCP servers, recorded runs
/// without their settings and never honoured a remembered approval; a template run started in the
/// background dropped its template; a background or console resume ran under today's slider instead
/// of the permissions it was started with. Each of these is asked below of the one composition.</para>
/// </summary>
public sealed class RunComposerTests
{
    private const string QuickAnswer = """{"disposition":"quick_action","title":"answer","steps":[]}""";

    private const string TwoStepPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]}]}
        """;

    /// <summary>A store of "allow for this workspace" outside the workspace, as the real one is - and not the real one.</summary>
    private static ApprovalStore TempApprovals()
        => new(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-permissions.json"));

    private static RunEnvironment Environment(EngineFixture fx, FakeChatProvider provider, Worker worker, int autonomy,
        ApprovalStore approvals, IReadOnlyList<McpServerConfig>? mcp = null)
    {
        var models = new ModelResolver();
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0 } };
        return new RunEnvironment(new SingleProviderFactory(provider), models, new StaticWorkerProvider([worker], worker.Id),
            new ToolRegistry(EngineFixture.ShippedTools()), mcp ?? [], new Planner(checksAuditEnabled: false),
            new PermissionEngine(), new ModelRouter(models), new LogHub(), settings,
            EngineComposition.PolicyFor(settings, autonomy),
            new RunSettings(autonomy, AutonomyTiers.Describe(autonomy), worker.Id, Staged: false), worker.Id,
            Approvals: approvals);
    }

    private static async Task<List<WorkEvent>> RunAsync(RunEnvironment environment, RunRequest request, IDecisionHandler decisions)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var composed = await RunComposer.ComposeAsync(environment, request, decisions, cts.Token);
        var events = new List<WorkEvent>();
        await foreach (var ev in composed.Events(cts.Token))
            events.Add(ev);
        return events;
    }

    private static async Task<RunRecord> OnlyRecordAsync(EngineFixture fx)
    {
        var store = RunStoreFactory.Create(fx.Workspace);
        var header = Assert.Single(await store.LoadSummariesAsync(CancellationToken.None));
        return (await store.LoadAsync(header.RunId, CancellationToken.None))!;
    }

    // ── what every run is, wherever it came from ────────────────────────────

    /// <summary>
    /// The console recorded its runs without the settings they ran under, so a run's history could not
    /// say what it had been allowed to do. Recording is the composer's, for every source.
    /// </summary>
    [Theory]
    [InlineData(IntentSource.CommandBar)]
    [InlineData(IntentSource.Schedule)]
    [InlineData(IntentSource.Inbox)]
    public async Task Every_run_is_recorded_with_the_settings_it_ran_under(IntentSource source)
    {
        using var fx = new EngineFixture();
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer), Turn.Says("done")),
            EngineFixture.WorkerWith(), autonomy: 2, TempApprovals());

        await RunAsync(environment, new RunRequest(fx.Workspace, "answer", source), new ScriptedDecisionHandler("allow"));

        Assert.Equal(environment.RunSettings, (await OnlyRecordAsync(fx)).Settings);
    }

    /// <summary>
    /// A template run started in the background was handed only its goal: its limits, checks,
    /// permissions and role went nowhere, and the run looked like the saved task while being a typed one.
    /// </summary>
    [Fact]
    public async Task A_template_run_keeps_its_limits_and_is_recorded_as_that_template()
    {
        using var fx = new EngineFixture();
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            EngineFixture.WorkerWith(), autonomy: 2, TempApprovals());
        var spec = new ResolvedTaskSpec("nightly-mail", 3, "Nightly mail digest", fx.Workspace.Id, fx.Workspace.Name, fx.Root,
            "Write the mail digest", new Dictionary<string, string>(), environment.Policy, [],
            new ExecutionLimits(MaxSteps: 1), WorkerId: null, ReviewRequired: false);

        var events = await RunAsync(environment, new RunRequest(fx.Workspace, spec.Goal, IntentSource.Inbox, Spec: spec),
            new ParkingDecisionHandler());

        Assert.Contains("limit of 1 step(s)", events.Last().OutcomeReason());
        Assert.Equal("nightly-mail", ResolvedTaskSpec.Parse((await OnlyRecordAsync(fx)).Spec!)!.TemplateId);
    }

    /// <summary>
    /// A resumed run continues under what it was started with. Background and console resumes ran under
    /// whatever the slider or the command line said now - here, Autonomous over a run started at Observe.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_keeps_the_permissions_it_was_started_with()
    {
        using var fx = new EngineFixture();
        var observe = new RunSettings(0, AutonomyTiers.Describe(0), "developer", Staged: false);
        var checkpoints = new KeepingCheckpointStore();
        await fx.RunAsync(fx.Build(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            checkpoints: checkpoints, settings: observe), "write the report");
        var afterFirst = checkpoints.Saved.First(c => c.Finished == 1);

        var worker = EngineFixture.WorkerWith("write_file");
        var environment = Environment(fx, new FakeChatProvider(
                Turn.Calls1("write_file", """{"path":"report.md","content":"done"}"""), Turn.Says("done"))
            { WhenExhausted = Turn.Says("done") }, worker, autonomy: 3, TempApprovals());
        var decisions = new ScriptedDecisionHandler("deny");

        await RunAsync(environment, new RunRequest(fx.Workspace, afterFirst.Request, IntentSource.Inbox, Resume: afterFirst), decisions);

        // Autonomous would have written without asking; the Observe the run was started under asks.
        Assert.Contains(decisions.Requests, r => r.Subject == "write_file");
        Assert.False(fx.Exists("report.md"));
    }

    // ── staging ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(IntentSource.Inbox)]
    [InlineData(IntentSource.Remote)]
    [InlineData(IntentSource.Schedule)]
    public async Task A_run_nobody_is_watching_cannot_stage_and_says_so_before_anything_starts(IntentSource source)
    {
        using var fx = new EngineFixture();
        var request = new RunRequest(fx.Workspace, "answer", source, Stage: true);
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer)), EngineFixture.WorkerWith(), 2, TempApprovals());

        Assert.Contains("cannot stage", RunComposer.Refusal(request));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunComposer.ComposeAsync(environment, request, new ParkingDecisionHandler(), CancellationToken.None));
        Assert.Equal(RunComposer.Refusal(request), refused.Message);
    }

    [Fact]
    public async Task A_watched_run_stages_and_a_resumed_one_never_does()
    {
        using var fx = new EngineFixture();
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer)), EngineFixture.WorkerWith(), 2, TempApprovals());

        await using (var staged = await RunComposer.ComposeAsync(environment,
                         new RunRequest(fx.Workspace, "answer", IntentSource.CommandBar, Stage: true), fx.Decisions, default))
            Assert.IsType<StagingArtifactStore>(staged.Artifacts);

        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "answer", "answer", null, null, [], [], [], [], 0, 0, null);
        await using var resumed = await RunComposer.ComposeAsync(environment,
            new RunRequest(fx.Workspace, "answer", IntentSource.CommandBar, Resume: checkpoint, Stage: true), fx.Decisions, default);
        Assert.IsType<DiskArtifactStore>(resumed.Artifacts);
    }

    // ── answers given for good ──────────────────────────────────────────────

    /// <summary>
    /// "Allow for this workspace" was honoured only inside the window's approval card, so a background
    /// or scheduled run stopped at the very question its owner had answered for good - or, where nobody
    /// can approve, was never even offered the tool.
    /// </summary>
    [Theory]
    [InlineData(IntentSource.Schedule)]
    [InlineData(IntentSource.Inbox)]
    public async Task A_tool_allowed_for_this_workspace_is_used_by_a_run_nobody_is_watching(IntentSource source)
    {
        using var fx = new EngineFixture();
        var approvals = TempApprovals();
        approvals.Approve(fx.Root, "write_file");
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            EngineFixture.WorkerWith("write_file"), autonomy: 0, approvals);

        await RunAsync(environment, new RunRequest(fx.Workspace, "write the digest", source), new UnattendedDecisionHandler());

        Assert.True(fx.Exists("digest.md"));
    }

    /// <summary>
    /// The console's <c>--approve deny</c> is the answer for that invocation, given on purpose - it is
    /// how a refusal is checked - and a standing approval must not overrule it.
    /// </summary>
    [Fact]
    public async Task An_explicit_answer_for_this_invocation_is_not_overruled_by_a_remembered_one()
    {
        using var fx = new EngineFixture();
        var approvals = TempApprovals();
        approvals.Approve(fx.Root, "write_file");
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            EngineFixture.WorkerWith("write_file"), autonomy: 0, approvals);
        var deny = new ScriptedDecisionHandler("deny");

        await RunAsync(environment, new RunRequest(fx.Workspace, "write the digest", IntentSource.CommandBar, Remembered: false), deny);

        Assert.Contains(deny.Requests, r => r.Subject == "write_file");
        Assert.False(fx.Exists("digest.md"));
    }

    /// <summary>A task from a phone is asked afresh every time: it must not inherit the desktop's standing grants.</summary>
    [Fact]
    public async Task A_task_from_a_phone_is_asked_even_what_was_allowed_for_the_workspace()
    {
        using var fx = new EngineFixture();
        var approvals = TempApprovals();
        approvals.Approve(fx.Root, "write_file");
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            EngineFixture.WorkerWith("write_file"), autonomy: 0, approvals);
        var phone = new ScriptedDecisionHandler("deny");

        await RunAsync(environment, new RunRequest(fx.Workspace, "write the digest", IntentSource.Remote), phone);

        Assert.Contains(phone.Requests, r => r.Subject == "write_file");
        Assert.False(fx.Exists("digest.md"));
    }

    // ── tools ───────────────────────────────────────────────────────────────

    /// <summary>The console connected no MCP servers, so a role that reached one in the window reached nothing there.</summary>
    [Theory]
    [InlineData(IntentSource.CommandBar)]
    [InlineData(IntentSource.Schedule)]
    public async Task The_configured_mcp_servers_are_connected_whatever_started_the_run(IntentSource source)
    {
        using var fx = new EngineFixture();
        var echo = McpConnection.ToolName("test", "echo");
        var worker = EngineFixture.WorkerWith("mcp__test__*") with { DefaultLevel = PermissionLevel.Autonomous };
        var environment = Environment(fx, new FakeChatProvider(Turn.Says(QuickAnswer),
                Turn.Calls1(ToolBudget.LoadToolName, "{\"names\":[\"" + echo + "\"]}", "load_1"),
                Turn.Calls1(echo, "{\"value\":\"mail\"}"), Turn.Says("done")),
            worker, autonomy: 3, TempApprovals(),
            [new McpServerConfig { Id = "test", Enabled = true, Command = "dotnet",
                Arguments = new() { typeof(Responses).Assembly.Location }, TimeoutSeconds = 10 }]);

        var events = await RunAsync(environment, new RunRequest(fx.Workspace, "use the server", source),
            new ScriptedDecisionHandler("allow"));

        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains(echo, StringComparison.Ordinal));
    }

    /// <summary>Keeps every checkpoint it is handed, so a test can resume from a boundary the run passed.</summary>
    private sealed class KeepingCheckpointStore : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = new();

        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
        {
            lock (Saved) Saved.Add(checkpoint);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct)
        {
            lock (Saved) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        }

        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
        {
            lock (Saved) return Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }
}

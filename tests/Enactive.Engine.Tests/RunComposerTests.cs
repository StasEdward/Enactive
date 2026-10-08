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

    /// <summary>An engine as EngineComposition.Build makes one, with a scripted model and the given team in it.</summary>
    private static ComposedEngine Engine(FakeChatProvider provider, ApprovalStore approvals, params Worker[] team)
        => Engine(provider, approvals, mcp: [], team);

    private static ComposedEngine Engine(FakeChatProvider provider, ApprovalStore approvals,
        IReadOnlyList<McpServerConfig> mcp, params Worker[] team)
    {
        var models = new ModelResolver();
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0 } };
        return new ComposedEngine(new SingleProviderFactory(provider), new StaticWorkerProvider(team, team[0].Id),
            new ModelRouter(models), models, new ToolRegistry(EngineFixture.ShippedTools()), mcp, settings, new LogHub(),
            new SessionApprovals())
        {
            Planner = new Planner(checksAuditEnabled: false),
            Approvals = approvals
        };
    }

    /// <summary>A request as a host makes one, with the level of the workspace it is in.</summary>
    private static RunRequest Request(EngineFixture fx, string prompt, IntentSource source, int autonomy = 2)
        => new(fx.Workspace, prompt, source) { Autonomy = autonomy };

    private static async Task<List<WorkEvent>> RunAsync(ComposedEngine engine, RunRequest request, IDecisionHandler decisions)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var composed = await RunComposer.ComposeAsync(engine, request, decisions, cts.Token);
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
        var worker = EngineFixture.WorkerWith();
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer), Turn.Says("done")), TempApprovals(), worker);

        await RunAsync(engine, Request(fx, "answer", source, autonomy: 2), new ScriptedDecisionHandler("allow"));

        Assert.Equal(new RunSettings(2, AutonomyTiers.Describe(2), worker.Role, Staged: false), (await OnlyRecordAsync(fx)).Settings);
    }

    /// <summary>
    /// A template run started in the background was handed only its goal: its limits, checks,
    /// permissions and role went nowhere, and the run looked like the saved task while being a typed one.
    /// </summary>
    [Fact]
    public async Task A_template_run_keeps_its_limits_and_is_recorded_as_that_template()
    {
        using var fx = new EngineFixture();
        var engine = Engine(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            TempApprovals(), EngineFixture.WorkerWith());
        var spec = new ResolvedTaskSpec("nightly-mail", 3, "Nightly mail digest", fx.Workspace.Id, fx.Workspace.Name, fx.Root,
            "Write the mail digest", new Dictionary<string, string>(), EngineComposition.PolicyFor(engine.Settings, 2), [],
            new ExecutionLimits(MaxSteps: 1), WorkerId: null, ReviewRequired: false);

        var events = await RunAsync(engine, new RunRequest(fx.Workspace, spec.Goal, IntentSource.Inbox, Spec: spec) { Autonomy = 2 },
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
        var engine = Engine(new FakeChatProvider(
                Turn.Calls1("write_file", """{"path":"report.md","content":"done"}"""), Turn.Says("done"))
            { WhenExhausted = Turn.Says("done") }, TempApprovals(), worker);
        var decisions = new ScriptedDecisionHandler("deny");

        await RunAsync(engine, new RunRequest(fx.Workspace, afterFirst.Request, IntentSource.Inbox, Resume: afterFirst) { Autonomy = 3 },
            decisions);

        // Autonomous would have written without asking; the Observe the run was started under asks.
        Assert.Contains(decisions.Requests, r => r.Subject == "write_file");
        Assert.False(fx.Exists("report.md"));
    }

    // ── what governs a run: its workspace's level and worker ────────────────
    //
    // Hosts used to work these out themselves - the policy, the level's name, which worker - and their
    // copies differed: the window named the worker by its role, the console by its id, and the history
    // said the host's pick even where a template had named another.

    private static readonly Worker Writer = EngineFixture.WorkerWith("write_file") with { Id = "writer", Role = "Technical writer" };

    /// <summary>The level the request carries is the one the run acts under - here, whether a write is asked about.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public async Task A_run_acts_under_the_level_of_its_workspace(int autonomy, bool asked)
    {
        using var fx = new EngineFixture();
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            TempApprovals(), EngineFixture.WorkerWith("write_file"));
        var deny = new ScriptedDecisionHandler("deny");

        await RunAsync(engine, Request(fx, "write the digest", IntentSource.CommandBar, autonomy), deny);

        Assert.Equal(asked, deny.Requests.Any(r => r.Subject == "write_file"));
        Assert.Equal(!asked, fx.Exists("digest.md"));
    }

    /// <summary>
    /// A level outside the tiers is the nearest tier, not Autonomous: AutonomyTiers reads anything it has no case
    /// for as the last one, and a -1 from a damaged registry entry is not a request to run everything unasked.
    /// </summary>
    [Fact]
    public async Task A_level_below_the_tiers_is_the_most_careful_one()
    {
        using var fx = new EngineFixture();
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            TempApprovals(), EngineFixture.WorkerWith("write_file"));
        var deny = new ScriptedDecisionHandler("deny");

        await RunAsync(engine, Request(fx, "write the digest", IntentSource.CommandBar, autonomy: -1), deny);

        Assert.False(fx.Exists("digest.md"));
        Assert.Equal(AutonomyTiers.Describe(0), (await OnlyRecordAsync(fx)).Settings!.AutonomyName);
    }

    /// <summary>The history names the worker the run was on - the template's, over the workspace's - by its role.</summary>
    [Fact]
    public async Task The_history_names_the_worker_the_template_chose()
    {
        using var fx = new EngineFixture();
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer), Turn.Says("done")), TempApprovals(),
            EngineFixture.WorkerWith(), Writer);
        var spec = new ResolvedTaskSpec("notes", 1, "Release notes", fx.Workspace.Id, fx.Workspace.Name, fx.Root,
            "Write the notes", new Dictionary<string, string>(), EngineComposition.PolicyFor(engine.Settings, 2), [],
            new ExecutionLimits(), WorkerId: "writer", ReviewRequired: false);

        await RunAsync(engine, new RunRequest(fx.Workspace, spec.Goal, IntentSource.Inbox, Spec: spec)
            { Autonomy = 2, WorkspaceWorkerId = "developer" }, new ParkingDecisionHandler());

        Assert.Equal("Technical writer", (await OnlyRecordAsync(fx)).Settings!.Worker);
    }

    /// <summary>
    /// A workspace the window saved before 2026-10-08 names its worker by role. Looked up as an id that found
    /// nobody, and the run went to the default worker while the workspace said otherwise.
    /// </summary>
    [Fact]
    public async Task A_worker_saved_by_its_role_name_is_still_the_one_that_runs()
    {
        using var fx = new EngineFixture();
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer), Turn.Says("done")), TempApprovals(),
            EngineFixture.WorkerWith(), Writer);

        await using var composed = await RunComposer.ComposeAsync(engine,
            Request(fx, "write the notes", IntentSource.CommandBar) with { WorkspaceWorkerId = "technical WRITER" },
            fx.Decisions, default);

        Assert.Equal("writer", composed.Intent.WorkerId);
    }

    /// <summary>
    /// A task from a phone is governed by what is saved against the folder IT names - not by the desktop's
    /// slider, which once ran a remote task at whatever level an unrelated project was sitting at.
    /// </summary>
    [Fact]
    public void A_task_from_a_phone_carries_the_settings_saved_for_its_own_workspace()
    {
        using var fx = new EngineFixture();
        var saved = new WorkspaceEntry("notes", fx.Root, DateTimeOffset.UtcNow, Autonomy: 0, WorkerId: "writer", StageChanges: true);

        var request = RunRequest.FromPhone(fx.Workspace, saved, "write the notes");

        Assert.Equal((IntentSource.Remote, 0, "writer", true),
            (request.Source, request.Autonomy, request.WorkspaceWorkerId, request.Stage));
    }

    // ── asking a resume for something else ──────────────────────────────────

    private static RunCheckpoint Stopped(int? autonomy, string? workerId)
        => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "check the disks", "disks",
            workerId, null, [], [], [], [], 1, 0,
            autonomy is { } level ? new RunSettings(level, AutonomyTiers.Describe(level), "Developer", Staged: false) : null);

    /// <summary>
    /// A resume is refused a level or a worker other than the one the run was started with - and only those. Any level
    /// or role given with a resume was refused, which broke the scheduler line that resumes after every reboot and names
    /// the level it always ran under.
    /// </summary>
    [Theory]
    [InlineData(2, null, 3, null, true)]             // another level
    [InlineData(2, null, 2, null, false)]            // the same level
    [InlineData(2, "developer", null, "writer", true)]    // another worker
    [InlineData(2, "developer", null, "DEVELOPER", false)] // the same worker
    [InlineData(2, null, null, "developer", false)]  // the default worker, when the run named none
    [InlineData(null, null, 3, null, false)]         // a run that recorded no level: the one asked for is used
    public void A_resume_is_refused_only_what_contradicts_how_the_run_was_started(
        int? startedAt, string? startedWith, int? askedLevel, string? askedWorker, bool refused)
        => Assert.Equal(refused, RunComposer.ResumeRefusal(Stopped(startedAt, startedWith), askedLevel, askedWorker, "developer") is not null);

    // ── staging ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(IntentSource.Inbox)]
    [InlineData(IntentSource.Remote)]
    [InlineData(IntentSource.Schedule)]
    public async Task A_run_nobody_is_watching_cannot_stage_and_says_so_before_anything_starts(IntentSource source)
    {
        using var fx = new EngineFixture();
        var request = new RunRequest(fx.Workspace, "answer", source, Stage: true) { Autonomy = 2 };
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer)), TempApprovals(), EngineFixture.WorkerWith());

        Assert.Contains("cannot stage", RunComposer.Refusal(request));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunComposer.ComposeAsync(engine, request, new ParkingDecisionHandler(), CancellationToken.None));
        Assert.Equal(RunComposer.Refusal(request), refused.Message);
    }

    [Fact]
    public async Task A_watched_run_stages_and_a_resumed_one_never_does()
    {
        using var fx = new EngineFixture();
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer)), TempApprovals(), EngineFixture.WorkerWith());

        await using (var staged = await RunComposer.ComposeAsync(engine,
                         new RunRequest(fx.Workspace, "answer", IntentSource.CommandBar, Stage: true) { Autonomy = 2 }, fx.Decisions, default))
            Assert.IsType<StagingArtifactStore>(staged.Artifacts);

        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "answer", "answer", null, null, [], [], [], [], 0, 0, null);
        await using var resumed = await RunComposer.ComposeAsync(engine,
            new RunRequest(fx.Workspace, "answer", IntentSource.CommandBar, Resume: checkpoint, Stage: true) { Autonomy = 2 },
            fx.Decisions, default);
        Assert.IsType<DiskArtifactStore>(resumed.Artifacts);
    }

    // ── answers given for good ──────────────────────────────────────────────

    /// <summary>
    /// "Allow (workspace, unwatched runs too)" is used by a run in the background or on a schedule. Allowing for good was
    /// honoured only inside the window's approval card, so such a run stopped at the very question its owner had answered
    /// - or, where nobody can approve, was never even offered the tool.
    /// </summary>
    [Theory]
    [InlineData(IntentSource.Schedule)]
    [InlineData(IntentSource.Inbox)]
    public async Task A_tool_allowed_for_unwatched_runs_is_used_by_a_run_nobody_is_watching(IntentSource source)
    {
        using var fx = new EngineFixture();
        var approvals = TempApprovals();
        approvals.Approve(fx.Root, "write_file", unwatched: true);
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            approvals, EngineFixture.WorkerWith("write_file"));

        await RunAsync(engine, Request(fx, "write the digest", source, autonomy: 0), new UnattendedDecisionHandler());

        Assert.True(fx.Exists("digest.md"));
    }

    /// <summary>
    /// A plain "Allow (workspace)" holds where somebody is watching, and not for a run in the background or on a
    /// schedule: every such approval was given on a card where that was all it meant, and for a while after 468b283 it
    /// let unwatched runs use the tool too - a widening nobody had agreed to.
    /// </summary>
    [Theory]
    [InlineData(IntentSource.Schedule)]
    [InlineData(IntentSource.Inbox)]
    public async Task A_plain_approval_is_not_used_by_a_run_nobody_is_watching(IntentSource source)
    {
        using var fx = new EngineFixture();
        var approvals = TempApprovals();
        approvals.Approve(fx.Root, "write_file");
        var model = new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done"));
        var engine = Engine(model, approvals, EngineFixture.WorkerWith("write_file"));

        await RunAsync(engine, Request(fx, "write the digest", source, autonomy: 0), new UnattendedDecisionHandler());

        Assert.False(fx.Exists("digest.md"));
        // Not offered at all: a tool the run cannot be allowed is not one its model is told of.
        Assert.All(model.Requests.Skip(1), r => Assert.DoesNotContain(r.Tools ?? [], t => t.Name == "write_file"));
    }

    /// <summary>...and is used, unasked, by a run somebody is watching.</summary>
    [Fact]
    public async Task A_plain_approval_answers_for_a_run_somebody_is_watching()
    {
        using var fx = new EngineFixture();
        var approvals = TempApprovals();
        approvals.Approve(fx.Root, "write_file");
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            approvals, EngineFixture.WorkerWith("write_file"));
        var person = new ScriptedDecisionHandler("deny");

        await RunAsync(engine, Request(fx, "write the digest", IntentSource.CommandBar, autonomy: 0), person);

        Assert.True(fx.Exists("digest.md"));
        Assert.DoesNotContain(person.Requests, r => r.Subject == "write_file");
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
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            approvals, EngineFixture.WorkerWith("write_file"));
        var deny = new ScriptedDecisionHandler("deny");

        await RunAsync(engine, new RunRequest(fx.Workspace, "write the digest", IntentSource.CommandBar, Remembered: false) { Autonomy = 0 },
            deny);

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
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
            Turn.Calls1("write_file", """{"path":"digest.md","content":"mail"}"""), Turn.Says("done")),
            approvals, EngineFixture.WorkerWith("write_file"));
        var phone = new ScriptedDecisionHandler("deny");

        await RunAsync(engine, Request(fx, "write the digest", IntentSource.Remote, autonomy: 0), phone);

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
        var engine = Engine(new FakeChatProvider(Turn.Says(QuickAnswer),
                Turn.Calls1(ToolBudget.LoadToolName, "{\"names\":[\"" + echo + "\"]}", "load_1"),
                Turn.Calls1(echo, "{\"value\":\"mail\"}"), Turn.Says("done")),
            TempApprovals(),
            [new McpServerConfig { Id = "test", Enabled = true, Command = "dotnet",
                Arguments = new() { typeof(Responses).Assembly.Location }, TimeoutSeconds = 10 }], worker);

        var events = await RunAsync(engine, Request(fx, "use the server", source, autonomy: 3),
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

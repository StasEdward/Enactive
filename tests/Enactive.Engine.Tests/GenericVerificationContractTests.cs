namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.History;

public sealed class GenericVerificationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requested_contract_survives_resume_even_if_host_defaults_change(bool conflict)
    {
        using var fx = new EngineFixture();
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var store = new Checkpoints();
        const string plan = """
            {"disposition":"task","title":"work","steps":[{"title":"one","dependsOn":[]},{"title":"two","dependsOn":[0]}],
             "checks":[{"name":"user check","command":"custom-verify --strict","request_quote":"Run custom-verify --strict."}]}
            """;
        var worker = new FakeChatProvider(Turn.Says(plan),
            Turn.Calls1("write_file", """{"path":"one.txt","content":"done"}"""), Turn.Says("done"),
            Turn.Calls1("write_file", """{"path":"two.txt","content":"done"}"""), Turn.Says("done"));
        await fx.RunAsync(fx.Build(worker, checkpoints: store), "Run custom-verify --strict.");
        var saved = store.Saved.First(c => c.Finished == 1);
        var restored = JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(saved))!;
        var criterion = Assert.Single(restored.Checks!);
        Assert.Equal(CriterionOrigin.Requested, criterion.Origin);
        Assert.Equal("Run custom-verify --strict.", criterion.RequestQuote);
        commands.Seen.Clear();
        fx.PlannerOverride = new Planner();
        var contractReviewer = new FakeChatProvider(Turn.Says(JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Assess the saved command against the original request" } },
            checks = new[] { new { name = "user check", command = "custom-verify --strict", origin = "requested",
                request_quote = "Run custom-verify --strict.", expectedExitCode = 0, reason = "Original requested check" } },
            action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = conflict ? "Saved criterion cannot be safely verified" : null
        })));
        var resumed = new FakeChatProvider(Turn.Calls1("read_file", """{"path":"two.txt"}"""), Turn.Says("done"));
        var events = await fx.ResumeAsync(fx.Build(new MapProviderFactory(resumed, (Routers.PlannerProviderId, contractReviewer)),
            router: Routers.WithPlannerOn(), checkpoints: store,
            successCriteria: [new("changed host setting", "do-not-run")]), restored);
        Assert.Equal(conflict ? RunOutcomeKind.Incomplete : RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(contractReviewer.Requests);
        if (conflict) { Assert.Empty(commands.Seen); Assert.Empty(resumed.Requests); }
        else Assert.Equal("custom-verify --strict", Assert.Single(commands.Seen).Command);
    }

    private sealed class Checkpoints : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved);
    }

    [Theory]
    [InlineData("python -m pytest --quiet")]
    [InlineData("npm test -- --runInBand")]
    [InlineData("bash scripts/verify.sh 'a b'")]
    [InlineData("powershell -NoProfile -File checks.ps1")]
    public async Task Planner_repairs_its_check_without_dispatching_worker_repair(string requested)
    {
        using var fx = new EngineFixture();
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(Plan(requested)), Turn.Says("""
            {"decisions":[{"index":0,"kind":"check","reason":"The proposed target is incorrect; the request names report.txt.","command":"verify report.txt"}]}
            """), Turn.Says(JsonSerializer.Serialize(new {
                sources = new[] { new { id = "O001", assessment = "Both final checks are permitted" } },
                checks = new object[] {
                    new { name = "original", command = requested, origin = "requested", request_quote = "Verify using " + requested + ".", expectedExitCode = 0, reason = "Explicitly requested" },
                    new { name = "proposal", command = "verify report.txt", origin = "proposed", request_quote = (string?)null, expectedExitCode = 0, reason = "Permitted local check" }
                }, action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = (string?)null
            })));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"done"}"""), Turn.Says("done"));
        var providers = new MapProviderFactory(worker, (Routers.PlannerProviderId, planner));
        var events = await fx.RunAsync(fx.Build(providers, router: Routers.WithPlannerOn(), successRetries: 1),
            "Verify using " + requested + ". Produce report.txt.");
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(2, worker.Requests.Count); // No worker turn for a planner mistake.
        Assert.Equal(3, planner.Requests.Count);
        Assert.Equal(2, commands.Seen.Count(c => c.Command == requested)); // Before and after check revision.
        Assert.All(commands.Seen, c => Assert.Equal(fx.Root, c.Root));
        Assert.Contains(events, e => e.Summary.Contains("Planner revised proposed check"));
    }

    [Fact]
    public async Task Forbidden_replacement_is_stopped_before_execution_even_with_permissive_permissions()
    {
        using var fx = new EngineFixture();
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(Plan("custom-verify")), Turn.Says("""
            {"decisions":[{"index":0,"kind":"check","reason":"Try a remote check","command":"forbidden-upload"}]}
            """), Turn.Says("""
            {"sources":[{"id":"O001","assessment":"Remote verification conflicts with the no-network restriction"}],
             "checks":[],"action_policy":null,"forbidden_effects":[],"unresolved":"forbidden-upload violates the request"}
            """));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"keep"}"""), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successRetries: 1), "Verify using custom-verify. Produce report.txt. Do not use network.");
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.DoesNotContain(commands.Seen, c => c.Command == "forbidden-upload");
        Assert.Equal(2, worker.Requests.Count);
        Assert.Equal("keep", fx.Read("report.txt"));
        Assert.Contains("Do not use network", planner.Requests[2].Messages[1].Content!);
    }

    [Fact]
    public async Task Confirmed_work_defect_is_fixed_by_worker_then_all_checks_repeat()
    {
        using var fx = new EngineFixture();
        var commands = new Commands { WorkDefect = true };
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(Plan("custom-verifier --strict")), Turn.Says("""
            {"decisions":[{"index":0,"kind":"work","reason":"Required output fixed.txt is missing.","command":"wrong-check"}]}
            """));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"done"}"""), Turn.Says("done"),
            Turn.Calls1("write_file", """{"path":"fixed.txt","content":"fixed"}"""), Turn.Says("fixed"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successRetries: 1), "Verify using custom-verifier --strict. Produce report.txt.");
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(4, worker.Requests.Count);
        Assert.Equal(2, commands.Seen.Count(c => c.Command == "custom-verifier --strict"));
    }

    [Fact]
    public async Task Uncertain_diagnosis_does_not_change_files_or_dispatch_worker()
    {
        using var fx = new EngineFixture();
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(Plan("custom-verify")), Turn.Says("""
            {"decisions":[{"index":0,"kind":"unknown","reason":"Insufficient evidence","command":""}]}
            """));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"done"}"""), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successRetries: 1), "Verify using custom-verify. Produce report.txt.");
        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(2, worker.Requests.Count);
        Assert.Equal(2, planner.Requests.Count);
    }

    [Fact]
    public async Task Invented_request_quote_is_not_accepted_as_user_provenance()
    {
        var provider = new FakeChatProvider(Turn.Says(Plan("invented-check")),
            Turn.Says("""{"disposition":"quick_action","title":"t","checks":[]}"""));
        var context = new WorkContext(null, "project", null, null, null, [], []);
        var result = await new Planner().PlanAsync("Write a document", context, provider, "model", default, proposeChecks: true);
        Assert.Empty(result.Checks);
        Assert.Equal(2, provider.Requests.Count);
    }

    private static string Plan(string command) => JsonSerializer.Serialize(new {
        disposition = "quick_action", title = "work",
        checks = new object[] {
            new { name = "original", command, request_quote = "Verify using " + command + "." },
            new { name = "proposal", command = "wrong-check" }
        }
    });

    private sealed class Commands : ITool
    {
        public bool WorkDefect;
        public List<(string Command, string Root)> Seen { get; } = [];
        public ToolDefinition Definition => new("run_command", "verify", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var command = doc.RootElement.GetProperty("command").GetString()!;
            Seen.Add((command, ctx.WorkspaceRoot));
            var exit = command == "wrong-check" && !(WorkDefect && File.Exists(Path.Combine(ctx.WorkspaceRoot, "fixed.txt"))) ? 1 : 0;
            return Task.FromResult(new ToolResult(exit == 0, "exit " + exit, null, [], new Dictionary<string, object?> { ["exitCode"] = exit }));
        }
    }
}

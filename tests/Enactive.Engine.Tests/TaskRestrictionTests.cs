namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class TaskRestrictionTests
{
    private static WorkContext Restricted => new(null, "workspace", null, null, null, [], [])
    {
        Restrictions = [new(ForbiddenTaskEffect.FileDeletion, "Do not delete files.")]
    };

    [Theory]
    [InlineData("delete_file", "{\"path\":\"a.txt\"}")]
    [InlineData("run_command", "{\"command\":\"del a.txt\"}")]
    [InlineData("run_command", "{\"command\":\"rm a.txt\"}")]
    [InlineData("run_powershell", "{\"script\":\"Remove-Item a.txt\"}")]
    public async Task Permissive_approval_cannot_override_task_and_refusal_changes_nothing(string name, string arguments)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "original");
        var registry = new ToolRegistry(EngineFixture.ShippedTools());
        var ctx = fx.ContextFor() with { Context = Restricted };
        var before = registry.WorkspaceVersion(ctx.WorkspaceId);
        var result = await registry.InvokeAsync(new("1", name, arguments), ctx, default);
        Assert.False(result.Success);
        Assert.True(result.DidNotRun);
        Assert.True(result.Metadata.ContainsKey("taskConstraintRefusal"));
        Assert.Equal(WorkspaceEffect.None, result.WorkspaceEffect);
        Assert.Equal(before, registry.WorkspaceVersion(ctx.WorkspaceId));
        Assert.Equal("original", fx.Read("a.txt"));
        var restored = await registry.InvokeAsync(new("2", "write_file", """{"path":"a.txt","content":"restored"}"""), ctx, default);
        Assert.True(restored.Success);
        Assert.Equal("restored", fx.Read("a.txt"));
        var unrestricted = ctx with { Context = Restricted with { Restrictions = [] } };
        Assert.True((await registry.InvokeAsync(new("3", "delete_file", """{"path":"a.txt"}"""), unrestricted, default)).Success);
        Assert.False(fx.Exists("a.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Planner_restriction_requires_real_provenance_and_cannot_be_removed(bool omit)
    {
        var answer = JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Preserve files" } },
            checks = Array.Empty<object>(),
            action_policy = (object?)null, forbidden_effects = omit ? Array.Empty<object>() : new object[] { new { effect = "file-deletion", source_quote = "Invented ban" } },
            unresolved = (string?)null
        });
        var provider = new FakeChatProvider(Turn.Says(answer), Turn.Says(answer));
        var plan = new PlanResult(IntentDisposition.QuickAction, "work", null) { Restrictions = Restricted.Restrictions };
        var result = await PlanCheckReview.RunAsync(plan, "Do not delete files.", Restricted, provider, "strong",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default);
        Assert.NotNull(result.IncompleteReason);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Saved_restriction_survives_serialization_and_resume()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        var store = new Checkpoints();
        const string contract = """{"sources":[{"id":"O001","assessment":"Restore without deletion"}],"checks":[],"action_policy":{"allowed_tools":["read_file","write_file","run_command"],"command_prefixes":["dotnet run"],"source_quote":"Do not delete files.","reason":"File tools and dotnet run only"},"forbidden_effects":[{"effect":"file-deletion","source_quote":"Do not delete files."}],"unresolved":null}""";
        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"work","steps":[{"title":"one","dependsOn":[]},{"title":"two","dependsOn":[0]}]}"""),
            Turn.Says(contract));
        var worker = new FakeChatProvider(
            Turn.Calls1("write_file", """{"path":"a.txt","content":"one"}"""), Turn.Says("done"),
            Turn.Calls1("write_file", """{"path":"b.txt","content":"two"}"""), Turn.Says("done"));
        await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), checkpoints: store), "Do not delete files. Restore contents.");
        var saved = JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(store.Saved.First(c => c.Finished == 1)))!;
        Assert.Equal(ForbiddenTaskEffect.FileDeletion, Assert.Single(saved.Restrictions).Effect);
        Assert.Equal("dotnet run", Assert.Single(saved.ActionPolicy!.CommandPrefixes));
        var resumed = new FakeChatProvider(
            Turn.Calls1("run_command", """{"command":"echo forbidden"}"""),
            Turn.Calls1("run_command", """{"command":"del a.txt"}"""),
            Turn.Calls1("read_file", """{"path":"a.txt"}"""), Turn.Says("done"));
        await fx.ResumeAsync(fx.Build(new MapProviderFactory(resumed,
            (Routers.PlannerProviderId, new FakeChatProvider(Turn.Says(contract)))),
            router: Routers.WithPlannerOn(), checkpoints: store), saved);
        Assert.Equal("one", fx.Read("a.txt"));
        Assert.Contains(resumed.Requests.SelectMany(r => r.Messages), m => m.Content?.Contains("Refused by the task action policy") == true);
        Assert.Contains(resumed.Requests.SelectMany(r => r.Messages), m => m.Content?.Contains("no-deletion restriction") == true);
    }

    private sealed class Checkpoints : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<RunCheckpoint?> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == id));
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_contract_reaches_worker_dispatch_and_allows_correction(bool proposeChecks)
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner(), ProposeChecks = proposeChecks };
        fx.Write("a.txt", "original");
        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"restore"}"""),
            Turn.Says("""{"sources":[{"id":"O001","assessment":"Restore without deletion"}],"checks":[],"action_policy":null,"forbidden_effects":[{"effect":"file-deletion","source_quote":"Do not delete files."}],"unresolved":null}"""));
        var worker = new FakeChatProvider(
            Turn.Calls1("run_command", """{"command":"del a.txt"}"""),
            Turn.Calls1("write_file", """{"path":"a.txt","content":"restored"}"""), Turn.Says("restored"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            worker: EngineFixture.WorkerWith("run_command", "write_file"), router: Routers.WithPlannerOn()),
            "Do not delete files. Restore a.txt.");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal("restored", fx.Read("a.txt"));
        Assert.Contains(worker.Requests.SelectMany(r => r.Messages), m => m.Content?.Contains("no-deletion restriction") == true);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
    }
}

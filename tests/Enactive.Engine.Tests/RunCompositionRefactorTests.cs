namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class RunCompositionRefactorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Common_factory_preserves_host_artifact_store_and_captured_output_budget(bool staged)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write","steps":[]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"kept"}"""), Turn.Says("done"));
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, GenerationBudgets = new(Action: 1234) } };
        var options = settings.Engine;
        // The editor changes the settings after the run took its switches: a new value, not the one the run holds.
        settings.Engine = settings.Engine with { GenerationBudgets = new(Action: 9999) };
        var artifacts = staged ? (Enactive.Core.Artifacts.IArtifactStore)new StagingArtifactStore(fx.Root) : fx.Artifacts;
        var models = new ModelResolver();
        var worker = EngineFixture.WorkerWith("write_file");
        var resources = new RunEngineResources(new SingleProviderFactory(provider), models,
            new StaticWorkerProvider([worker], worker.Id),
            new ToolRegistry(EngineFixture.ShippedTools()), artifacts, fx.Workspace, new Planner(checksAuditEnabled: false),
            new PermissionEngine(), fx.Decisions, PermissionPolicy.PermissiveDefault,
            new Services(), new ModelRouter(models));
        var events = await fx.RunAsync(RunComposer.Engine(resources, options), "Write result.txt");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(!staged, fx.Exists("result.txt"));
        Assert.Equal(staged ? "kept" : null, await artifacts.TryReadPendingAsync("result.txt", default));
        Assert.Contains(provider.Requests, r => r.OutputTokenLimit == 1234);
        Assert.DoesNotContain(provider.Requests, r => r.OutputTokenLimit == 9999);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_keeps_evidence_with_its_conversation_and_never_shares_between_runs(bool shared)
    {
        using var fx = new EngineFixture();
        RunSession NewSession() => new(new RunScope(Guid.NewGuid(), Guid.NewGuid(),
            new RunBudget(ExecutionLimits.None, DateTimeOffset.UtcNow), []), []);
        var session = NewSession();
        session.ConfigurePlan(shared, null);
        var first = session.BeginStep([], fx.Artifacts.BeginStep());
        first.Journal.Record(1, "read_file", "a.txt", ActionOutcome.Succeeded, "first evidence");
        var next = session.BeginStep([], fx.Artifacts.BeginStep());
        Assert.Equal(shared ? 1 : 0, next.StepStart);
        Assert.Equal(0, next.EvidenceStart);
        Assert.Equal(shared, ReferenceEquals(first.Journal, next.Journal));
        Assert.Equal(shared, ReferenceEquals(first.Reads, next.Reads));
        var other = NewSession();
        other.ConfigurePlan(shared, null);
        var separate = other.BeginStep([], fx.Artifacts.BeginStep());
        Assert.Equal(0, separate.StepStart);
        Assert.NotSame(first.Journal, separate.Journal);
        Assert.NotSame(first.Reads, separate.Reads);
    }

    private sealed class Services : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

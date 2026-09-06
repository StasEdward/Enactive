namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Workers;
using Xunit;

/// <summary>
/// The fallback model configured on a worker has to be used when the primary one cannot be reached.
///
/// <see cref="ModelResolver.NextOnFailure"/> was implemented, tested by nothing, and called by
/// nothing: a person could fill in "Fallback model" in the worker editor, save it, and every run
/// against a stopped Ollama or an expired key would still fail outright. That is the same class of
/// defect as the missing provider headers — a setting that is offered and not applied.
/// </summary>
public sealed class ModelFallbackTests
{
    private const string Primary = "primary";
    private const string Backup = "backup";

    private static Worker WorkerWithFallback()
        => new("developer", "Developer", "You are a developer.",
               new[] { "write_file", "read_file", "list_dir", "run_command" },
               PermissionLevel.Execute,
               new ModelPolicy(
                   new ModelRef(Primary, "primary-model"),
                   Fallback: new ModelRef(Backup, "backup-model")));

    private static Worker WorkerWithoutFallback()
        => new("developer", "Developer", "You are a developer.",
               new[] { "write_file", "read_file", "list_dir", "run_command" },
               PermissionLevel.Execute,
               new ModelPolicy(new ModelRef(Primary, "primary-model")));

    private static FakeChatProvider Planner(string dispositionJson)
        => new(Turn.Says(dispositionJson)) { WhenExhausted = Turn.Says(dispositionJson) };

    private const string QuickAction =
        """{"disposition":"quick_action","title":"do the thing"}""";

    private const string OneStepPlan = """
        {"disposition":"task","title":"one step",
         "steps":[{"title":"do the thing","dependsOn":[],"complexity":"normal"}]}
        """;

    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    private static bool MentionsFallback(WorkEvent e)
        => e.Kind == EventKind.Routed
           && e.Summary.Contains("fallback", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task A_quick_action_whose_model_is_unreachable_runs_on_the_fallback()
    {
        using var fx = new EngineFixture();
        var backup = new FakeChatProvider { WhenExhausted = Turn.Says("did it on the backup") };

        var orchestrator = fx.Build(
            new MapProviderFactory(
                Planner(QuickAction),
                (Routers.PlannerProviderId, Planner(QuickAction)),
                (Primary, new ThrowingChatProvider("connection refused")),
                (Backup, backup)),
            worker: WorkerWithFallback(),
            router: Routers.WithPlannerOn());

        var events = await fx.RunAsync(orchestrator, "do the thing");

        Assert.NotEmpty(backup.Requests);
        Assert.Contains(events, MentionsFallback);
        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
    }

    [Fact]
    public async Task A_plan_step_whose_model_is_unreachable_runs_on_the_fallback()
    {
        using var fx = new EngineFixture();
        var backup = new FakeChatProvider { WhenExhausted = Turn.Says("did the step on the backup") };

        var orchestrator = fx.Build(
            new MapProviderFactory(
                Planner(OneStepPlan),
                (Routers.PlannerProviderId, Planner(OneStepPlan)),
                (Primary, new ThrowingChatProvider("connection refused")),
                (Backup, backup)),
            worker: WorkerWithFallback(),
            router: Routers.WithPlannerOn());

        var events = await fx.RunAsync(orchestrator, "do the thing");

        Assert.NotEmpty(backup.Requests);
        Assert.Contains(events, MentionsFallback);
        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
    }

    // The fallback is one shot. If the backup is down too, the run has to end — an endpoint that is
    // simply gone must not become an infinite ping-pong between two dead addresses.
    [Fact]
    public async Task When_the_fallback_is_also_down_the_run_fails_after_one_switch()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            new MapProviderFactory(
                Planner(QuickAction),
                (Routers.PlannerProviderId, Planner(QuickAction)),
                (Primary, new ThrowingChatProvider("primary is down")),
                (Backup, new ThrowingChatProvider("backup is down too"))),
            worker: WorkerWithFallback(),
            router: Routers.WithPlannerOn());

        var events = await fx.RunAsync(orchestrator, "do the thing");

        Assert.Single(events, MentionsFallback);
        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
    }

    // No fallback configured means the old behaviour, unchanged: the failure surfaces immediately
    // instead of the engine inventing a second model to try.
    [Fact]
    public async Task No_fallback_configured_means_the_failure_surfaces_at_once()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            new MapProviderFactory(
                Planner(QuickAction),
                (Routers.PlannerProviderId, Planner(QuickAction)),
                (Primary, new ThrowingChatProvider("connection refused"))),
            worker: WorkerWithoutFallback(),
            router: Routers.WithPlannerOn());

        var events = await fx.RunAsync(orchestrator, "do the thing");

        Assert.DoesNotContain(events, MentionsFallback);
        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
    }

    // A fallback pointing at the same model as the primary is not a fallback — resolving it would
    // just re-run the identical failing call.
    [Fact]
    public void A_fallback_equal_to_the_failed_model_is_not_offered()
    {
        var policy = new ModelPolicy(
            new ModelRef(Primary, "m"), Fallback: new ModelRef(Primary, "m"));

        Assert.Null(new Enactive.Agents.ModelResolver().NextOnFailure(policy, new ModelRef(Primary, "m")));
    }
}

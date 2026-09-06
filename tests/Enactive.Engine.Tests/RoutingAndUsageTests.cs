namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Core.Permissions;
using Enactive.Core.Workers;
using Xunit;

/// <summary>
/// Two facts a run could not previously state about itself.
///
/// <para>WHAT IT COST. Token usage was emitted from inside the tool loop, and only from there. The
/// planner and the reviewer call the provider directly, outside that loop, so their tokens were
/// spent on every run and counted on none: the total shown was execute-only. That is not a rounding
/// error — a content review sends whole documents to the most expensive model bound, and on a
/// writing task the uncounted part can be the larger one.</para>
///
/// <para>WHERE IT RAN. The routing panel could only ever show the three BINDINGS, and a per-step
/// routing decision was announced only when it differed from the worker's model. So a run bound to a
/// local worker whose every step the planner rated "complex" displayed the local binding it never
/// used, and the only way to find out was to read the log. Each step now says which model ran it and
/// which complexity sent it there, as values in the payload rather than as a sentence.</para>
/// </summary>
public sealed class RoutingAndUsageTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"do the thing"}""";

    private static Worker WorkerOn(string providerId, string model = "worker-model")
        => new("developer", "Developer", "You are a developer.",
               new[] { "write_file", "read_file", "list_dir", "run_command" },
               PermissionLevel.Execute,
               new ModelPolicy(new ModelRef(providerId, model)));

    private static WorkEvent[] Usage(IEnumerable<WorkEvent> events, string purpose)
        => events.OfKind(EventKind.UsageReported).Where(e => e.Purpose() == purpose).ToArray();

    // ── what it cost ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_planner_call_reports_its_tokens_against_the_planner_model()
    {
        using var fx = new EngineFixture();

        var planner = new FakeChatProvider(Turn.Says(QuickPlan).Reporting(700, 40));
        var worker = new FakeChatProvider(Turn.Says("Done."));

        var providers = new MapProviderFactory(worker, (Routers.PlannerProviderId, planner));

        var events = await fx.RunAsync(
            fx.Build(providers, router: Routers.WithPlannerOn()), "do the thing");

        var planning = Assert.Single(Usage(events, WorkEventPayload.WorkPurpose.Plan));
        Assert.Equal((700, 40), planning.Usage());
        Assert.Equal(Routers.PlannerProviderId, planning.ProviderId());
        Assert.Equal("plan-model", planning.ModelName());
    }

    [Fact]
    public async Task The_review_call_reports_its_tokens_against_the_reviewer_model()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(QuickPlan), Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass().Reporting(5000, 60));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer),
            "write something");

        var review = Assert.Single(Usage(events, WorkEventPayload.WorkPurpose.Review));
        Assert.Equal((5000, 60), review.Usage());
        Assert.Equal(Verdicts.ProviderId, review.ProviderId());
        Assert.Equal(Verdicts.Model, review.ModelName());
    }

    // A reviewer that wanders off format is asked once more. Two calls were made and two calls are
    // paid for, so counting only the first would understate exactly the runs that went worst.
    [Fact]
    public async Task A_reviewer_that_had_to_be_asked_twice_reports_both_calls()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(QuickPlan), Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider(
            Turn.Says("I think it is fine, broadly speaking.").Reporting(4000, 30),
            Verdicts.Pass().Reporting(4200, 20));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer),
            "write something");

        var review = Assert.Single(Usage(events, WorkEventPayload.WorkPurpose.Review));
        Assert.Equal((8200, 50), review.Usage());
    }

    // A provider that reports nothing is a real case and must not become a run of zero-token events:
    // "did not say" and "said zero" are different facts.
    [Fact]
    public async Task A_planner_that_reports_no_tokens_produces_no_usage_event()
    {
        using var fx = new EngineFixture();

        var planner = new FakeChatProvider(Turn.Says(QuickPlan));
        var worker = new FakeChatProvider(Turn.Says("Done."));

        var events = await fx.RunAsync(
            fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
                     router: Routers.WithPlannerOn()),
            "do the thing");

        Assert.Empty(Usage(events, WorkEventPayload.WorkPurpose.Plan));
    }

    // The tool loop's own usage says what it is too, so a consumer can tell execute from the phases
    // around it instead of assuming every usage event is a worker turn.
    [Fact]
    public async Task Tokens_from_the_tool_loop_are_marked_as_execute()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Says("Done.").Reporting(30, 12));

        var events = await fx.RunAsync(fx.Build(provider, worker: WorkerOn("local")), "do the thing");

        var execute = Assert.Single(Usage(events, WorkEventPayload.WorkPurpose.Execute));
        Assert.Equal((30, 12), execute.Usage());
        Assert.Equal("local", execute.ProviderId());
    }

    // ── where it ran ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_route_payload_carries_the_decision_as_values()
    {
        var payload = WorkEventPayload.RoutePayload(
            "step", "anthropic", "claude-sonnet-4-6", stepNo: 2, complexity: "complex");

        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                               EventKind.Routed, "anything at all", payload);

        Assert.Equal("step", ev.Route());
        Assert.Equal(2, ev.StepNo());
        Assert.Equal("anthropic", ev.ProviderId());
        Assert.Equal("claude-sonnet-4-6", ev.ModelName());
        Assert.Equal("complex", ev.RouteComplexity());
    }

    [Fact]
    public async Task The_worker_planner_and_reviewer_bindings_are_routed_as_values()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(Turn.Says(QuickPlan), Turn.Says("Done."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(
            fx.Build(worker, worker: WorkerOn("local"),
                     router: Routers.WithReviewer(), reviewProvider: reviewer),
            "do the thing");

        var routes = events.OfKind(EventKind.Routed).ToArray();

        var workerRoute = Assert.Single(routes, e => e.Route() == "worker");
        Assert.Equal("local", workerRoute.ProviderId());

        var reviewRoute = Assert.Single(routes, e => e.Route() == "review");
        Assert.Equal(Verdicts.ProviderId, reviewRoute.ProviderId());
        Assert.Equal(Verdicts.Model, reviewRoute.ModelName());
    }

    // The one that matters: the step routing has to be stated even when it lands on the worker's own
    // model. Announcing it only on a difference is what let an all-cloud run display a local worker.
    [Fact]
    public async Task Every_step_says_which_model_ran_it_even_when_it_is_the_workers_own()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"two steps",
             "steps":[{"title":"first","dependsOn":[]},
                      {"title":"second","dependsOn":[0]}]}
            """;

        var provider = new FakeChatProvider(Turn.Says(plan));

        var events = await fx.RunAsync(
            fx.Build(provider, worker: WorkerOn("local", "gemma:12b")), "do two things");

        var steps = events.OfKind(EventKind.Routed)
            .Where(e => e.Route() == "step")
            .OrderBy(e => e.StepNo())
            .ToArray();

        Assert.Equal(2, steps.Length);
        Assert.Equal(new int?[] { 1, 2 }, steps.Select(e => e.StepNo()).ToArray());
        Assert.All(steps, e => Assert.Equal("local", e.ProviderId()));
        Assert.All(steps, e => Assert.Equal("gemma:12b", e.ModelName()));
        Assert.All(steps, e => Assert.Equal("normal", e.RouteComplexity()));
    }

    // And the complexity travels with it, because that IS the reason the step went where it did —
    // the answer to "why did a trivial-looking request cost cloud tokens".
    [Fact]
    public async Task A_complex_step_names_the_expensive_model_and_says_why()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"two steps",
             "steps":[{"title":"recall external facts","dependsOn":[],"complexity":"complex"},
                      {"title":"rename a thing","dependsOn":[],"complexity":"trivial"}]}
            """;

        var providers = new MapProviderFactory(
            new FakeChatProvider(Turn.Says(plan)),
            (Routers.LightProviderId, new FakeChatProvider()),
            (Routers.HeavyProviderId, new FakeChatProvider()));

        var events = await fx.RunAsync(
            fx.Build(providers, router: Routers.WithComplexityRouting()), "do two things");

        var steps = events.OfKind(EventKind.Routed)
            .Where(e => e.Route() == "step")
            .ToDictionary(e => e.StepNo()!.Value);

        Assert.Equal(Routers.HeavyProviderId, steps[1].ProviderId());
        Assert.Equal("complex", steps[1].RouteComplexity());

        Assert.Equal(Routers.LightProviderId, steps[2].ProviderId());
        Assert.Equal("trivial", steps[2].RouteComplexity());
    }

    // After a fallback the step's routing must name the model that actually ran it. Keeping the
    // unreachable one there would report a binding, which is the habit this whole panel replaces.
    [Fact]
    public async Task A_step_that_fell_back_is_routed_to_the_fallback_model()
    {
        using var fx = new EngineFixture();

        var worker = new Worker("developer", "Developer", "You are a developer.",
            new[] { "write_file", "read_file" }, PermissionLevel.Execute,
            new ModelPolicy(new ModelRef("primary", "m1"), Fallback: new ModelRef("standby", "m2")));

        var providers = new MapProviderFactory(
            new FakeChatProvider(Turn.Says(QuickPlan)),
            (Routers.PlannerProviderId, new FakeChatProvider(Turn.Says(QuickPlan))),
            ("primary", new ThrowingChatProvider("down")),
            ("standby", new FakeChatProvider(Turn.Says("Done."))));

        var events = await fx.RunAsync(
            fx.Build(providers, worker: worker, router: Routers.WithPlannerOn()), "do it");

        var last = events.OfKind(EventKind.Routed)
            .Where(e => e.Route() == "worker")
            .Last();

        Assert.Equal("standby", last.ProviderId());
        Assert.Equal("m2", last.ModelName());
    }
}

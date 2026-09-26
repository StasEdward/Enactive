namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;

public sealed class PlanRepairTests
{
    private const string Self = """{"disposition":"task","title":"bad","steps":[{"title":"write","dependsOn":[0]}]}""";
    private const string Disconnected = """{"disposition":"task","title":"bad","steps":[{"title":"write","dependsOn":[]},{"title":"A","dependsOn":[2]},{"title":"B","dependsOn":[1]}]}""";
    private const string Valid = """{"disposition":"task","title":"fixed","steps":[{"title":"write","dependsOn":[]}]}""";

    [Theory]
    [InlineData(Self)]
    [InlineData(Disconnected)]
    public async Task Corrected_DAG_is_validated_before_any_worker_action(string bad)
    {
        using var fx = new EngineFixture();
        fx.GenerationBudgetsOverride = new(Planner: 901);
        var provider = new FakeChatProvider(Turn.Says(bad), Turn.Says(Valid),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"ok"}"""), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(provider), "write result.txt");
        Assert.All(provider.Requests.Take(2), r => Assert.Equal(901, r.OutputTokenLimit));
        Assert.Equal("ok", fx.Read("result.txt"));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains("dependency cycle", provider.Requests[1].Messages[^1].Content);
        Assert.Contains(provider.Requests[1].Messages, m => m.Content?.Contains("write result.txt") == true);
        Assert.Single(events, e => e.Kind == EventKind.StepStarted);
        Assert.Single(events, e => e.Kind == EventKind.ToolInvoked);
    }

    [Theory]
    [InlineData(Self, "stop")]
    [InlineData(Disconnected, "stop")]
    [InlineData("invalid", "stop")]
    [InlineData(Valid, "length")]
    [InlineData("{\"disposition\":\"quick_action\",\"title\":\"bypass\"}", "stop")]
    public async Task Failed_repair_never_falls_back_to_execution_or_a_third_plan(string answer, string finish)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(Self), new Turn(answer, FinishReason: finish), Turn.Says(Valid));
        var events = await fx.RunAsync(fx.Build(provider), "write");
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.StepStarted or EventKind.ToolInvoked);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(15, 2)]
    public async Task Planning_and_repair_usage_both_count_before_dispatch(int maxTokens, int expectedCalls)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(Self).Reporting(5, 5), Turn.Says(Valid).Reporting(5, 5), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(provider, limits: new(MaxTokens: maxTokens)), "write");
        Assert.Equal(expectedCalls, provider.Requests.Count);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.StepStarted or EventKind.ToolInvoked);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public async Task Cancellation_at_replan_boundary_prevents_the_second_request()
    {
        using var fx = new EngineFixture();
        using var stop = new CancellationTokenSource();
        var provider = new FakeChatProvider(Turn.Says(Self), Turn.Says(Valid));
        var engine = fx.Build(provider);
        var intent = new Enactive.Core.Intents.Intent(Guid.NewGuid(), "write", Enactive.Core.Intents.IntentSource.CommandBar,
            new Enactive.Core.Context.WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []), DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ev in engine.SubmitIntentAsync(intent, stop.Token))
                if (ev.Kind == EventKind.ErrorObserved && ev.Summary.Contains("requesting one corrected plan")) stop.Cancel();
        });
        Assert.Single(provider.Requests);
    }
}

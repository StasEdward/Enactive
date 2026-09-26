namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

public sealed class GenerationBudgetTests
{
    private const string Plan = """{"disposition":"quick_action","title":"write"}""";

    [Theory]
    [InlineData("length")]
    [InlineData("stop")]
    public async Task Incomplete_call_is_preserved_but_only_the_complete_retry_executes(string finish)
    {
        using var fx = new EngineFixture();
        fx.GenerationBudgetsOverride = new(111, 222, 333, 444);
        const string fragment = "{\"path\":\"new.txt\",\"content\":\"unfinished";
        var provider = new FakeChatProvider(Turn.Says(Plan),
            new Turn(Calls: [new("partial", "write_file", fragment)], FinishReason: finish),
            Turn.Calls1("write_file", """{"path":"new.txt","content":"COMPLETE"}""", "complete"),
            Turn.Says("done")) { Window = 65536 };
        var events = await fx.RunAsync(fx.Build(provider), "write a file");
        Assert.Equal("COMPLETE", fx.Read("new.txt"));
        Assert.Single(events, e => e.Kind == EventKind.ToolInvoked);
        var calls = provider.Requests.Where(r => r.Purpose is not null and not GenerationPurpose.Planning).ToArray();
        Assert.Equal(111, calls[0].OutputTokenLimit);
        Assert.Equal(222, calls[1].OutputTokenLimit);
        Assert.Equal(GenerationPurpose.FileWrite, calls[1].Purpose);
        var raw = calls[1].Messages.Single(m => m.Content?.StartsWith("[Incomplete model turn;") == true);
        Assert.Null(raw.ToolCalls);
        using var recorded = JsonDocument.Parse(raw.Content![(raw.Content!.IndexOf('\n') + 1)..]);
        Assert.Equal(fragment, recorded.RootElement.GetProperty("ToolCalls")[0].GetProperty("ArgumentsJson").GetString());
        Assert.DoesNotContain(calls[1].Messages, m => m.ToolCalls?.Any(c => c.Id == "partial") == true);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    [Fact]
    public void Generation_settings_round_trip_and_clone()
    {
        var settings = new Enactive.Settings.AppSettings { GenerationBudgets = new(101, 202, 303, 404) };
        var restored = JsonSerializer.Deserialize<Enactive.Settings.AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(settings.GenerationBudgets, restored.GenerationBudgets);
        Assert.Equal(settings.GenerationBudgets, settings.Clone().GenerationBudgets);
    }

    [Fact]
    public async Task Token_budget_finishes_the_batch_then_stops_before_the_next_request()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(Plan),
            new Turn(Calls: [new("a", "write_file", """{"path":"a.txt","content":"A"}"""),
                             new("b", "write_file", """{"path":"b.txt","content":"B"}""")],
                PromptTokens: 10, CompletionTokens: 10),
            Turn.Calls1("write_file", """{"path":"never.txt","content":"NO"}"""));
        var events = await fx.RunAsync(fx.Build(provider, limits: new ExecutionLimits(MaxTokens: 15)), "write");
        Assert.Equal("A", fx.Read("a.txt"));
        Assert.Equal("B", fx.Read("b.txt"));
        Assert.False(File.Exists(Path.Combine(fx.Root, "never.txt")));
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public async Task Text_continuation_uses_final_budget_and_repeated_reasoning_truncation_stops()
    {
        using var fx = new EngineFixture();
        fx.GenerationBudgetsOverride = new(111, 222, 333, 444);
        var provider = new FakeChatProvider(Turn.Says(Plan), new Turn("partial", FinishReason: "length"), Turn.Says("complete"));
        await fx.RunAsync(fx.Build(provider), "summarize");
        Assert.Equal(GenerationPurpose.FinalAnswer, provider.Requests[^1].Purpose);
        Assert.Equal(333, provider.Requests[^1].OutputTokenLimit);

        var repeated = new FakeChatProvider(Turn.Says(Plan))
        { WhenExhausted = new Turn(Thinking: "still reasoning", FinishReason: "length") };
        var events = await fx.RunAsync(fx.Build(repeated), "do it");
        Assert.Equal(4, repeated.Requests.Count); // planner plus original and two recovery turns
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public void Turn_budget_ignores_started_steps_but_checks_elapsed_time()
    {
        var now = DateTimeOffset.UtcNow;
        var budget = new RunBudget(new(MaxSteps: 1, MaxDurationSeconds: 5), now, () => now);
        budget.StepStarted();
        Assert.NotNull(budget.Exhausted);
        Assert.Null(budget.TurnExhausted);
        now = now.AddSeconds(6);
        Assert.Contains("second(s)", budget.TurnExhausted);
    }
}

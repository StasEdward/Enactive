namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Xunit;

public sealed class RepairConsultationTests
{
    private static readonly ToolCall Check = new("c", "run_command", """{"command":"build"}""");
    private static readonly ToolResult Failed = ToolResults.Fail("compiler error CS1002");
    private static void Edit(RepairAttempts tracker, string content) => tracker.Observe(
        new("e", "edit_file", System.Text.Json.JsonSerializer.Serialize(new { content })),
        ToolResults.Ok("changed") with { WorkspaceEffect = WorkspaceEffect.Changed }, false);

    [Fact]
    public void Duplicate_keys_use_the_last_value_like_tool_argument_lookup()
    {
        var tracker = new RepairAttempts();
        tracker.Observe(new("c", "run_command", """{"command":"ignored","command":"build"}"""), Failed, true);
        Edit(tracker, "A");
        tracker.Observe(Check, Failed, true);
        Assert.Equal(1, tracker.FailedRepairs);
        tracker.Observe(new("c", "run_command", """{"command":"ignored","command":"build","force":false,"force":true}"""),
            ToolResults.Ok("passed"), true);
        Assert.Equal(0, tracker.FailedRepairs);
    }

    [Fact]
    public void Repeats_unknown_shell_effects_and_expected_failures_do_not_count_as_failed_repairs()
    {
        var tracker = new RepairAttempts();
        tracker.Observe(Check, Failed, true);
        tracker.Observe(Check, Failed, true);
        Assert.Equal(0, tracker.FailedRepairs);
        Edit(tracker, "A");
        tracker.Observe(Check, Failed, true);
        Assert.Equal(1, tracker.FailedRepairs);
        Edit(tracker, "A");
        tracker.Observe(Check, Failed, true);
        Assert.Equal(1, tracker.FailedRepairs);
        tracker.Observe(new("s", "run_command", "{}"), ToolResults.Ok("shell") with { WorkspaceEffect = WorkspaceEffect.Unknown }, true);
        tracker.Observe(Check, Failed, true);
        Assert.Equal(1, tracker.FailedRepairs);
        Edit(tracker, "B");
        tracker.Observe(Check, Failed, true);
        Assert.Equal(2, tracker.FailedRepairs);
        tracker.Observe(Check with { ArgumentsJson = """{"command":"build","expectedExitCodes":[1]}""" }, ToolResults.Ok("expected mutation failure"), true);
        Assert.Equal(0, tracker.FailedRepairs);
        Edit(tracker, "C");
        tracker.Observe(Check, Failed, true);
        Assert.Equal(0, tracker.FailedRepairs); // accepted result cleared this failure chain
    }

    [Theory]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(true, "length")]
    [InlineData(true, "error")]
    [InlineData(true, "budget")]
    public async Task Advice_is_opt_in_bounded_and_returns_to_the_original_worker(bool enabled, string finish)
    {
        using var fx = new EngineFixture();
        fx.RepairConsultationOverride = enabled ? new(new("adviser", "strong"), 2, 512) : new();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command")
            .Append(new FailingCheck()).ToArray();
        var script = new List<Turn> { Turn.Says("""{"disposition":"quick_action","title":"repair"}"""),
            Turn.Says("TRANSCRIPT_ONLY_SECRET") with { Calls = [Check] } };
        for (var i = 0; i < 4; i++)
        {
            script.Add(Turn.Calls1("write_file", System.Text.Json.JsonSerializer.Serialize(new { path = "new.txt", content = "version " + i })));
            script.Add(new Turn(Calls: [Check]));
        }
        script.Add(Turn.Says("cannot repair"));
        var worker = new FakeChatProvider(script.ToArray());
        var adviser = new FakeChatProvider(new Turn("Fix the missing semicolon.", FinishReason: finish, PromptTokens: 30, CompletionTokens: 10));
        if (finish == "error") adviser.Answering = _ => throw new HttpRequestException("consultant unavailable");
        var events = await fx.RunAsync(fx.Build(new Factory(worker, adviser),
            limits: finish == "budget" ? new Enactive.Core.Templates.ExecutionLimits(MaxTokens: 35) : null), "Repair compilation");
        if (!enabled) { Assert.Empty(adviser.Requests); return; }
        var request = Assert.Single(adviser.Requests);
        Assert.Null(request.Tools);
        Assert.Equal(512, request.OutputTokenLimit);
        Assert.Equal(2, request.Messages.Count);
        var evidence = request.Messages[1].Content!;
        Assert.Contains("Repair compilation", evidence);
        Assert.Contains("CS1002", evidence);
        Assert.DoesNotContain("TRANSCRIPT_ONLY_SECRET", evidence);
        Assert.True(evidence.Length < 18000);
        if (finish != "error") Assert.Contains(events, e => e.Kind == EventKind.UsageReported && e.PayloadJson?.Contains("\"repair\"") == true);
        Assert.Equal(finish == "stop", worker.Requests.Any(r => r.Messages.Any(m => m.Content?.Contains("Fix the missing semicolon.") == true)));
        Assert.Equal(finish == "budget" ? "version 1" : "version 3", fx.Read("new.txt"));
        if (finish == "budget") Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public void Policy_requires_explicit_model_and_threshold_and_survives_settings_round_trip()
    {
        Assert.False(new RepairConsultation().Enabled);
        Assert.False(new RepairConsultation(new("cloud", "strong")).Enabled);
        var settings = new Enactive.Settings.AppSettings { RepairConsultation = new(new("cloud", "strong"), 3, 700) };
        var copy = System.Text.Json.JsonSerializer.Deserialize<Enactive.Settings.AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Assert.True(copy.RepairConsultation.Enabled);
        Assert.Equal(settings.RepairConsultation, copy.RepairConsultation);
        Assert.Equal(settings.RepairConsultation, settings.Clone().RepairConsultation);
    }

    private sealed class Factory(IChatProvider worker, IChatProvider adviser) : IChatProviderFactory
    {
        public IChatProvider Create(string providerId) => providerId == "adviser" ? adviser : worker;
    }
    private sealed class FailingCheck : ITool
    {
        public ToolDefinition Definition { get; } = new("run_command", "test", "{}", WorkspaceEffect.None, Kind: ToolKind.Command);
        public Enactive.Core.Permissions.PermissionLevel RequiredLevel => Enactive.Core.Permissions.PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct) => Task.FromResult(Failed);
    }
}

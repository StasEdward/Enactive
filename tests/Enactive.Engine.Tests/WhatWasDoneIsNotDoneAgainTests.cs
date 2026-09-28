namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A step carried on after a question does not do again what it had already done - above all an
/// action that cannot be taken back.
///
/// <para><b>The defect.</b> Carrying a run on after NeedsUser redid the stopped step from its
/// beginning (a quick action from scratch, a planned step from its boundary). A step that sent a
/// message, then stopped at a question about its next action, sent the message again once the
/// question was answered. For file edits that is often survivable; for an engine meant to send
/// email, post and deploy (amendment J), it is not.</para>
/// </summary>
public sealed class WhatWasDoneIsNotDoneAgainTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"notify and tidy"}""";
    private const string TwoSteps = """
        {"disposition":"task","title":"write, then notify and tidy",
         "steps":[{"title":"write","dependsOn":[]},{"title":"notify and tidy","dependsOn":[0]}]}
        """;
    private const string Notify = """{"to":"team","text":"the report is ready"}""";

    /// <summary>A message to the team: outside the workspace, and a second one is a second message.</summary>
    private sealed class NotifyTool(Action? after = null) : ITool
    {
        public int Sent;
        public ToolDefinition Definition { get; } = new("notify", "Send a message to the team.", """{"type":"object"}""",
            WorkspaceEffect: WorkspaceEffect.None, Kind: ToolKind.Unknown, OnceOnly: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Sent);
            after?.Invoke();
            return Task.FromResult(new ToolResult(true, $"sent #{Sent}", null, [], new Dictionary<string, object?>()));
        }
    }

    private static readonly Enactive.Core.Workers.Worker Worker =
        EngineFixture.WorkerWith("notify", "write_file", "read_file", "delete_file");

    private static EngineFixture Workspace(NotifyTool notify)
    {
        var fx = new EngineFixture();
        fx.ToolsOverride = EngineFixture.ShippedTools().Append(notify).ToArray();
        fx.Write("draft.md", "an old draft");
        return fx;
    }

    private static async Task<List<WorkEvent>> Submit(EngineFixture fx, Orchestrator engine, Guid taskId, CancellationToken ct = default)
    {
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var events = new List<WorkEvent>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await foreach (var ev in engine.SubmitIntentAsync(new Intent(taskId, "tell the team, then tidy up",
                               IntentSource.CommandBar, context, DateTimeOffset.UtcNow), timeout.Token))
                events.Add(ev);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return events;
    }

    private static string Conversation(FakeChatProvider provider, int request)
        => string.Join("\n", provider.Requests[request].Messages.Select(m => m.Content ?? ""));

    /// <summary>
    /// THE ONE THAT MATTERS: sent, then a question about the next action; answered, and the step is
    /// carried on from where it stopped - the message is not sent again, and the model is shown that
    /// it was sent.
    /// </summary>
    [Fact]
    public async Task A_message_sent_before_the_question_is_not_sent_again_after_it()
    {
        var notify = new NotifyTool();
        using var fx = Workspace(notify);
        var taskId = Guid.NewGuid();
        var ledger = new DecisionLedger(fx.Root);

        var first = await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("notify", Notify, "n1"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Says("done")), Worker, decisions: new ParkingDecisionHandler()), taskId);
        Assert.Equal(RunOutcomeKind.NeedsUser, first.Last().Outcome());
        Assert.Equal(1, notify.Sent);
        var question = Assert.Single(ledger.Pending());
        ledger.Answer(taskId, question.RequestId, "allow");

        // Carried on: the model's first turn is the call that was waiting, made again.
        var carriedOn = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d2"),
            Turn.Says("Told the team and removed the draft."));
        var second = await Submit(fx, fx.Build(carriedOn, Worker, decisions: new ParkingDecisionHandler()), taskId);

        Assert.Equal(RunOutcomeKind.Completed, second.Last().Outcome());
        Assert.Equal(1, notify.Sent);                                            // not sent twice
        Assert.False(File.Exists(Path.Combine(fx.Root, "draft.md")));             // the waiting action was done
        var shown = Conversation(carriedOn, 1);
        Assert.Contains("sent #1", shown, StringComparison.Ordinal);              // it sees what it did
        Assert.Contains("NOT CARRIED OUT", shown, StringComparison.Ordinal);      // and what it had not
        Assert.Contains(second, e => e.Summary.StartsWith("Carried on from where it stopped at a question", StringComparison.Ordinal));
    }

    /// <summary>The same in a plan: the step that asked is carried on from its position, not from its boundary.</summary>
    [Fact]
    public async Task A_planned_step_is_carried_on_from_its_position_not_its_boundary()
    {
        var notify = new NotifyTool();
        using var fx = Workspace(notify);
        var checkpoints = new JsonCheckpointStore(fx.Workspace);
        var ledger = new DecisionLedger(fx.Root);

        var first = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the report"}""", "w1"), Turn.Says("written"),
            Turn.Calls1("notify", Notify, "n1"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Worker, checkpoints: checkpoints, decisions: new ParkingDecisionHandler()), "write, notify, tidy");
        Assert.Equal(RunOutcomeKind.NeedsUser, first.Last().Outcome());
        var question = Assert.Single(ledger.Pending());
        ledger.Answer(question.TaskId, question.RequestId, "allow");

        var carriedOn = new FakeChatProvider(Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d2"), Turn.Says("tidied"));
        var resumed = await fx.ResumeAsync(fx.Build(carriedOn, Worker, checkpoints: checkpoints,
            decisions: new ParkingDecisionHandler()), (await ParkedRuns.CheckpointForAsync(checkpoints, question.TaskId, default))!);

        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
        Assert.Equal(1, notify.Sent);
        Assert.False(File.Exists(Path.Combine(fx.Root, "draft.md")));
        Assert.Contains("sent #1", Conversation(carriedOn, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// With no position to carry on from - the process died just after the message went - the task is
    /// done again from its start, and the message, asked for again with the same words, is not sent.
    /// </summary>
    [Fact]
    public async Task A_once_only_action_is_not_repeated_even_when_the_work_is_redone()
    {
        using var dies = new CancellationTokenSource();
        var notify = new NotifyTool(after: dies.Cancel);
        using var fx = Workspace(notify);
        var taskId = Guid.NewGuid();

        await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("notify", Notify, "n1"), Turn.Says("done")), Worker), taskId, dies.Token);
        Assert.Equal(1, notify.Sent);

        var redo = new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("notify", """{ "to": "team",  "text": "the report is ready" }""", "n2"), Turn.Says("done"));
        var again = await Submit(fx, fx.Build(redo, Worker), taskId);

        Assert.Equal(RunOutcomeKind.Completed, again.Last().Outcome());
        Assert.Equal(1, notify.Sent);                                            // same action, other whitespace: not sent
        Assert.Contains("ALREADY DONE", Conversation(redo, 2), StringComparison.Ordinal);
    }

    /// <summary>A different message is a different action, and is sent.</summary>
    [Fact]
    public async Task A_different_action_is_not_mistaken_for_one_already_taken()
    {
        using var dies = new CancellationTokenSource();
        var notify = new NotifyTool(after: dies.Cancel);
        using var fx = Workspace(notify);
        var taskId = Guid.NewGuid();

        await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("notify", Notify, "n1"), Turn.Says("done")), Worker), taskId, dies.Token);
        await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("notify", """{"to":"team","text":"one more thing"}""", "n2"), Turn.Says("done")), Worker), taskId);

        Assert.Equal(2, notify.Sent);
    }

    /// <summary>
    /// Planned again, a quick action can come back as steps, and its position would be found by
    /// nothing. It is carried on as the quick action it was - with the planning's restrictions kept.
    /// </summary>
    [Fact]
    public async Task A_quick_action_is_carried_on_as_one_even_if_the_planner_now_says_steps()
    {
        var notify = new NotifyTool();
        using var fx = Workspace(notify);
        var taskId = Guid.NewGuid();
        var ledger = new DecisionLedger(fx.Root);

        await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("notify", Notify, "n1"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("done")),
            Worker, decisions: new ParkingDecisionHandler()), taskId);
        var question = Assert.Single(ledger.Pending());
        ledger.Answer(taskId, question.RequestId, "allow");

        var second = await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d2"), Turn.Says("done")),
            Worker, decisions: new ParkingDecisionHandler()), taskId);

        Assert.Contains(second, e => e.Summary.StartsWith("Carried on as the quick action it was", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Completed, second.Last().Outcome());
        Assert.Equal(1, notify.Sent);
    }

    [Fact]
    public void Send_email_declares_that_it_cannot_be_taken_back()
        => Assert.True(EngineFixture.ToolNamed("send_email").Definition.OnceOnly);
}

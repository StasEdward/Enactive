namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.App.Ui;
using Enactive.Core.Events;
using Enactive.Core.Inbox;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A background run that stops at a question is filed in the inbox as that question, is answered
/// there, and carries on - the host half of NeedsUser, everything but the pixels.
///
/// <para>Before: a background run's question was answered "no" on the spot, the inbox said a
/// decision "needed your approval and was declined automatically", and the only way to get "yes" in
/// was to run the whole task again, in the foreground.</para>
/// </summary>
public sealed class AnsweredFromTheInboxTests
{
    private const string TwoSteps = """
        {"disposition":"task","title":"write then tidy",
         "steps":[{"title":"write","dependsOn":[]},{"title":"tidy","dependsOn":[0]}]}
        """;

    private static readonly Enactive.Core.Workers.Worker Tidier = EngineFixture.WorkerWith("write_file", "read_file", "delete_file");

    [Fact]
    public async Task A_question_in_the_inbox_is_answered_there_and_the_run_carries_on()
    {
        using var fx = new EngineFixture();
        fx.Write("draft.md", "an old draft");
        var runs = new JsonRunStore(fx.Workspace);
        var inbox = new JsonInboxStore(fx.Workspace);
        var checkpoints = new JsonCheckpointStore(fx.Workspace);
        var recorder = new RunRecorder(runs, workspaceId: fx.Workspace.Id);
        const string request = "write the final, then remove the draft";

        // The background run, as the window starts one: parked, not refused.
        var engine = fx.Build(new FakeChatProvider(
                Turn.Says(TwoSteps),
                Turn.Calls1("write_file", """{"path":"final.md","content":"the final text"}""", "w1"), Turn.Says("written"),
                Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Tidier, checkpoints: checkpoints, decisions: new ParkingDecisionHandler());
        var context = new Enactive.Core.Context.WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var intent = new Enactive.Core.Intents.Intent(Guid.NewGuid(), request, Enactive.Core.Intents.IntentSource.Inbox,
            context, DateTimeOffset.UtcNow);
        await BackgroundRunner.RunAsync(recorder.RecordAsync(engine.SubmitIntentAsync(intent, default), default),
            inbox, fx.Workspace, request, default);

        // Filed as a question, not an error.
        var filed = Assert.Single(await inbox.LoadAllAsync(default));
        Assert.Equal("decision", filed.Kind);
        Assert.StartsWith("Waiting for your decision", filed.Summary, StringComparison.Ordinal);

        // Opened in the inbox, the way the window finds it: the item's run, that run's task, the
        // question waiting for it - in full, with its answers.
        (ParkedDecision Parked, InboxItem Item)? carriedOn = null;
        var decisions = new InboxDecisions(fx.Root, (parked, item) =>
        {
            carriedOn = (parked, item);
            return Task.CompletedTask;
        });
        var run = (await runs.LoadSummariesAsync(default)).Single(r => r.RunId == filed.RunId);
        var waiting = decisions.WaitingFor(run.TaskId);
        Assert.NotNull(waiting);
        Assert.Contains("draft.md", waiting.FullText, StringComparison.Ordinal);

        var said = await decisions.AnswerAsync(waiting, filed, "allow");
        Assert.Contains("carries on in the background", said, StringComparison.Ordinal);
        var (parked, item) = carriedOn!.Value;
        Assert.Equal(filed.Id, item.Id);
        Assert.Null(decisions.WaitingFor(run.TaskId));

        // A second answer - a double click, another window - authorises nothing and carries nothing on.
        carriedOn = null;
        Assert.Contains("no longer waiting", await decisions.AnswerAsync(waiting, filed, "deny"), StringComparison.Ordinal);
        Assert.Null(carriedOn);

        // Carried on the way the window does it: from the step boundary, answered.
        var checkpoint = await ParkedRuns.CheckpointForAsync(checkpoints, parked.TaskId, default);
        Assert.NotNull(checkpoint);
        var resumed = fx.Build(new FakeChatProvider(
                Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Tidier, checkpoints: checkpoints, decisions: new ParkingDecisionHandler());
        await BackgroundRunner.RunAsync(recorder.RecordAsync(resumed.ResumeRunAsync(checkpoint!, context, default), default),
            inbox, fx.Workspace, request, default);

        Assert.False(File.Exists(Path.Combine(fx.Root, "draft.md")));
        var outcome = (await inbox.LoadAllAsync(default)).OrderBy(i => i.At).Last();
        Assert.Equal("result", outcome.Kind);
        Assert.StartsWith("Completed", outcome.Summary, StringComparison.Ordinal);
    }
}

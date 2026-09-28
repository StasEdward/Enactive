namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// A run with nobody there to answer stops at the question, keeps it, and goes on once it is
/// answered - Phase 1.8, NeedsUser: "can stop and correctly resume a run".
///
/// <para>Before: the question was answered "no" on the spot (unattended) and the run ended
/// Incomplete; the only way to get "yes" in was to start the whole task again, attended.</para>
/// </summary>
public sealed class AQuestionWaitsForAnAnswerTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"tidy up"}""";
    private const string TwoSteps = """
        {"disposition":"task","title":"write then tidy",
         "steps":[{"title":"write","dependsOn":[]},{"title":"tidy","dependsOn":[0]}]}
        """;

    private static readonly Enactive.Core.Workers.Worker Tidier = EngineFixture.WorkerWith("write_file", "read_file", "delete_file");

    private static async Task<List<WorkEvent>> Submit(EngineFixture fx, Orchestrator engine, Guid taskId, string request)
    {
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var events = new List<WorkEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var ev in engine.SubmitIntentAsync(
                           new Intent(taskId, request, IntentSource.CommandBar, context, DateTimeOffset.UtcNow), cts.Token))
            events.Add(ev);
        return events;
    }

    private sealed class Checkpoints : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) { lock (Deleted) Deleted.Add(runId); return Task.CompletedTask; }
    }

    /// <summary>THE ONE THAT MATTERS: stopped at the question, answered, and done - without being started again by hand.</summary>
    [Fact]
    public async Task A_run_stops_at_a_question_and_goes_on_once_it_is_answered()
    {
        using var fx = new EngineFixture();
        fx.Write("draft.md", "an old draft");
        var taskId = Guid.NewGuid();
        var ledger = new DecisionLedger(fx.Root);

        var first = await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Says("Removed the draft.")), Tidier, decisions: new ParkingDecisionHandler()), taskId, "remove the draft");

        Assert.Equal(RunOutcomeKind.NeedsUser, first.Last().Outcome());
        Assert.Contains("Run tool 'delete_file'?", first.Last().Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fx.Root, "draft.md")));          // asked, not done
        var question = Assert.Single(ledger.Pending());
        Assert.Equal(taskId, question.TaskId);

        Assert.True(ledger.Answer(taskId, question.RequestId, "allow"));

        var second = await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Says("Removed the draft.")), Tidier, decisions: new ParkingDecisionHandler()), taskId, "remove the draft");

        Assert.Equal(RunOutcomeKind.Completed, second.Last().Outcome());
        Assert.False(File.Exists(Path.Combine(fx.Root, "draft.md")));
        Assert.Empty(ledger.For(taskId));                                     // an ended run keeps no answers
    }

    /// <summary>
    /// In a plan, the run keeps its last step boundary: the step that asked is redone with the
    /// answer, and the step before it is not run again.
    /// </summary>
    [Fact]
    public async Task A_planned_run_keeps_its_checkpoint_and_resumes_at_the_step_that_asked()
    {
        using var fx = new EngineFixture();
        fx.Write("draft.md", "an old draft");
        var store = new Checkpoints();
        var ledger = new DecisionLedger(fx.Root);

        var first = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1("write_file", """{"path":"final.md","content":"the final text"}""", "w1"), Turn.Says("written"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Tidier, checkpoints: store, decisions: new ParkingDecisionHandler()), "write the final, then remove the draft");

        Assert.Equal(RunOutcomeKind.NeedsUser, first.Last().Outcome());
        Assert.Empty(store.Deleted);                                          // not an ending: nothing forgot it
        var checkpoint = store.Saved.Last();
        Assert.Equal(1, checkpoint.Finished);

        var question = Assert.Single(ledger.Pending());
        Assert.True(ledger.Answer(question.TaskId, question.RequestId, "allow"));

        var resumed = await fx.ResumeAsync(fx.Build(new FakeChatProvider(
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Tidier, checkpoints: store, decisions: new ParkingDecisionHandler()), checkpoint);

        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
        Assert.False(File.Exists(Path.Combine(fx.Root, "draft.md")));
        Assert.Equal("the final text", fx.Read("final.md"));
        Assert.DoesNotContain(resumed, e => e.Kind == EventKind.StepStarted && e.Summary.Contains("write", StringComparison.Ordinal));
    }

    /// <summary>
    /// An answer is to exactly the question asked. The step, redone, deciding to do something else
    /// asks about THAT - an approval of one action is not an approval of the next.
    /// </summary>
    [Fact]
    public async Task An_answer_does_not_carry_over_to_a_different_question()
    {
        using var fx = new EngineFixture();
        fx.Write("draft.md", "an old draft");
        fx.Write("notes.md", "notes");
        var taskId = Guid.NewGuid();
        var ledger = new DecisionLedger(fx.Root);

        await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("done")),
            Tidier, decisions: new ParkingDecisionHandler()), taskId, "tidy up");
        var asked = Assert.Single(ledger.Pending());
        ledger.Answer(taskId, asked.RequestId, "allow");

        var second = await Submit(fx, fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("delete_file", """{"path":"notes.md"}""", "d1"), Turn.Says("done")),
            Tidier, decisions: new ParkingDecisionHandler()), taskId, "tidy up");

        Assert.Equal(RunOutcomeKind.NeedsUser, second.Last().Outcome());
        Assert.True(File.Exists(Path.Combine(fx.Root, "notes.md")));
        Assert.Contains("notes.md", Assert.Single(ledger.Pending()).FullText, StringComparison.Ordinal);
    }

    /// <summary>With somebody there, nothing changes: the question is answered where it is asked, and nothing is written down.</summary>
    [Fact]
    public async Task An_attended_run_is_asked_as_before_and_nothing_is_kept()
    {
        using var fx = new EngineFixture();
        fx.Write("draft.md", "an old draft");
        fx.Decisions.Answer = "allow";

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("done")), Tidier), "tidy up");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(fx.Decisions.Requests);
        Assert.False(Directory.Exists(Path.Combine(fx.Root, DecisionLedger.Folder)));
    }

    // ── the ledger itself ────────────────────────────────────────────────────────────────

    private static Enactive.Core.Permissions.DecisionRequest Question(Guid task, string detail = "Arguments: x")
        => new(task, "Run tool 'delete_file'?", detail,
            [new("allow", "Allow"), new("deny", "Deny")], "allow", "delete_file");

    [Fact]
    public void An_answer_that_was_not_one_of_the_options_authorises_nothing()
    {
        var root = Directory.CreateTempSubdirectory("ledger").FullName;
        try
        {
            var ledger = new DecisionLedger(root);
            var task = Guid.NewGuid();
            var parked = ledger.Park(Question(task));

            Assert.False(ledger.Answer(task, parked.RequestId, "yes please"));
            Assert.False(ledger.Answer(task, Guid.NewGuid(), "allow"));
            Assert.Null(ledger.Answered(Question(task)));
            Assert.True(ledger.Answer(task, parked.RequestId, "allow"));
            Assert.False(ledger.Answer(task, parked.RequestId, "deny"));        // answered once
            Assert.Equal("allow", ledger.Answered(Question(task))!.OptionId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_same_question_waiting_is_kept_once()
    {
        var root = Directory.CreateTempSubdirectory("ledger").FullName;
        try
        {
            var ledger = new DecisionLedger(root);
            var task = Guid.NewGuid();
            ledger.Park(Question(task));
            ledger.Park(Question(task));
            Assert.Single(ledger.For(task));
        }
        finally { Directory.Delete(root, true); }
    }
}

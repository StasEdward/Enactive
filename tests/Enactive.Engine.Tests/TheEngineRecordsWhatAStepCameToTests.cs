namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// The engine, not the step, records what a step came to: its outcome, why (as a stable code), and the
/// last result it accepted from it with what that result is worth - kept whatever came after.
/// </summary>
public sealed class TheEngineRecordsWhatAStepCameToTests
{
    [Theory]
    [InlineData(StepOutcomeKind.Succeeded, OutcomeCause.None, true, true, ResultStanding.Confirmed)]
    [InlineData(StepOutcomeKind.Succeeded, OutcomeCause.None, true, false, ResultStanding.Unreviewed)]
    [InlineData(StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUnprocessable, true, true, ResultStanding.Unconfirmed)]
    [InlineData(StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUndecided, true, true, ResultStanding.Unconfirmed)]
    [InlineData(StepOutcomeKind.ReviewRejected, OutcomeCause.ReviewRejected, true, true, ResultStanding.Rejected)]
    [InlineData(StepOutcomeKind.Incomplete, OutcomeCause.ReviewRejected, true, true, ResultStanding.Rejected)]
    [InlineData(StepOutcomeKind.Incomplete, OutcomeCause.StepIncomplete, true, true, ResultStanding.Provisional)]
    [InlineData(StepOutcomeKind.Incomplete, OutcomeCause.StepIncomplete, false, true, ResultStanding.NotProvided)]
    public void What_a_result_is_worth_follows_how_the_step_ended_and_never_more(
        StepOutcomeKind outcome, OutcomeCause cause, bool hasResult, bool reviewed, ResultStanding expected)
        => Assert.Equal(expected, StepRecord.StandingOf(outcome, cause, hasResult, reviewed));

    [Fact]
    public void The_words_are_the_engines_and_separate_from_the_codes()
    {
        StepRecord Record(StepOutcomeKind o, OutcomeCause c, ResultStanding s, string? why = null) => new(o, c, why, s, null);
        Assert.Equal("Unconfirmed: the review could not be processed",
            ItemReport.Status(Record(StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUnprocessable, ResultStanding.Unconfirmed)));
        Assert.Equal("Unconfirmed: the reviewer could not decide",
            ItemReport.Status(Record(StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUndecided, ResultStanding.Unconfirmed)));
        Assert.Equal("Rejected by review", ItemReport.Status(Record(StepOutcomeKind.ReviewRejected, OutcomeCause.ReviewRejected, ResultStanding.Rejected)));
        Assert.Equal("Result not provided; unresolved tool call",
            ItemReport.Remains(Record(StepOutcomeKind.Incomplete, OutcomeCause.StepIncomplete, ResultStanding.NotProvided, "unresolved tool call")));
    }

    private const string TwoSteps = """
        {"disposition":"task","title":"find then list",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"list them","dependsOn":[0]}]}
        """;

    private sealed class Recording : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// THE ONE THAT MATTERS: a step hands its result on and then does not finish. The result is kept -
    /// in the checkpoint, by name - as provisional, and the step is not made successful by having it.
    /// </summary>
    [Fact]
    public async Task A_result_handed_on_before_a_step_broke_off_is_kept_as_provisional()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        fx.Write("pages/a.md", "a");
        var store = new Recording();
        var worker = new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["pages/a.md"]}""", "s1"),
            Turn.Calls1("run_command", """{"command":"exit 3"}""", "c1"),                 // fails, and is never made good
            Turn.Says("Found one."));

        var events = await fx.RunAsync(fx.Build(worker, checkpoints: store), "list the pages");

        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("find pages — INCOMPLETE", StringComparison.Ordinal));
        var saved = JsonSerializer.Serialize(store.Saved.Last(c => c.Steps[0].Record is not null));
        Assert.Contains("\"Standing\":\"Provisional\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"Cause\":\"StepIncomplete\"", saved, StringComparison.Ordinal);
        var record = JsonSerializer.Deserialize<RunCheckpoint>(saved)!.Steps[0].Record!;
        Assert.Equal(ResultStanding.Provisional, record.Standing);
        Assert.Contains("pages/a.md", record.Result!.ValuesJson, StringComparison.Ordinal);
    }

    /// <summary>The reminder to hand the result on comes before the verdict on calls still open - and changes nothing about that verdict.</summary>
    [Fact]
    public async Task The_reminder_to_hand_on_comes_before_the_verdict_on_open_calls()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        fx.Write("pages/a.md", "a");
        var worker = new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1("run_command", """{"command":"exit 3"}""", "c1"),
            Turn.Says("Found one."),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["pages/a.md"]}""", "s1"),
            Turn.Says("Handed on."));

        var events = await fx.RunAsync(fx.Build(worker), "list the pages");

        var reminder = string.Join("\n", worker.Requests[3].Messages.Select(m => m.Content ?? ""));
        Assert.Contains("is not finished until it hands its result on", reminder, StringComparison.Ordinal);
        Assert.Contains("These calls are still open", reminder, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("unresolved tool call", StringComparison.Ordinal));
    }

    /// <summary>A reviewer's answer that cannot be used is its own cause, apart from a verdict against the work.</summary>
    [Fact]
    public async Task A_review_that_could_not_be_processed_is_recorded_as_that()
    {
        using var fx = new EngineFixture();
        var store = new Recording();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"t","steps":[{"title":"write it","dependsOn":[]}]}"""),
            Turn.Calls1("write_file", """{"path":"r.md","content":"x"}""", "w1"),
            Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider(Turn.Says("not json at all"), Turn.Says("still not json"));

        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer, reviewRetries: 0,
            checkpoints: store), "write it");

        var record = store.Saved.Last(c => c.Steps[0].Record is not null).Steps[0].Record!;
        Assert.Equal(StepOutcomeKind.DoneUnverified, record.Outcome);
        Assert.Equal(OutcomeCause.ReviewUnprocessable, record.Cause);
        Assert.Equal("Unconfirmed: the review could not be processed", ItemReport.Status(record));
    }
}

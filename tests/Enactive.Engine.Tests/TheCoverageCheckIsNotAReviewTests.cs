namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application.Projections;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// A step sent back or rejected by the engine's coverage check (StepCoverageGate) was seen by no reviewer, and nothing a
/// person reads says one did: not the step's status, not its card, not the log, not who is at work.
/// </summary>
public sealed class TheCoverageCheckIsNotAReviewTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent GateFailed(int step = 2)
        => Ev(EventKind.ReviewFailed, $"[{step}] FAIL (the engine's coverage check - no reviewer was asked): covered 2 of 12",
            WorkEventPayload.EngineCheckPayload(step));

    [Fact]
    public void The_status_says_the_engines_coverage_check_rejected_it()
    {
        var record = new StepRecord(StepOutcomeKind.ReviewRejected, OutcomeCause.CoverageGateRejected, "covered 2 of 12", ResultStanding.Rejected, null);

        Assert.Equal("Rejected by the engine's coverage check", ItemReport.Status(record));
        Assert.Equal(ResultStanding.Rejected, StepRecord.StandingOf(StepOutcomeKind.ReviewRejected, OutcomeCause.CoverageGateRejected, true, false));
    }

    [Fact]
    public void Its_payload_says_it_is_the_engines()
    {
        Assert.True(GateFailed().ByEngineCheck());
        Assert.Equal(2, GateFailed().StepNo());
        Assert.False(Ev(EventKind.ReviewFailed, "[2] FAIL (Step review): wrong", "{\"step\":2}").ByEngineCheck());
    }

    [Fact]
    public void The_card_says_the_engine_sent_it_back_not_a_review()
    {
        var feed = new RunFeed(null);
        feed.Apply(Ev(EventKind.StepStarted, "[2/2] review pages", "{\"step\":2}"));

        feed.Apply(GateFailed());

        var card = feed.Cards.Last();
        Assert.Equal("The engine's coverage check sent it back…", card.Activity);
    }

    [Fact]
    public void Nobody_is_shown_reviewing_it()
    {
        var activity = new RunActivity();
        activity.Apply(Ev(EventKind.StepStarted, "step 2", "{\"step\":2}"));

        activity.Apply(GateFailed());

        Assert.Equal(AgentKind.Coder, activity.Agent);
        Assert.StartsWith("[2] FAIL (the engine's coverage check", activity.CurrentAction, StringComparison.Ordinal);
    }

    private sealed class Sink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];
        public void Log(LogEntry entry) => Entries.Add(entry);
    }

    private static async IAsyncEnumerable<WorkEvent> Of(params WorkEvent[] events)
    {
        foreach (var ev in events) yield return ev;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_log_has_it_from_the_orchestrator_not_the_reviewer()
    {
        var sink = new Sink();
        await foreach (var _ in Of(GateFailed(), Ev(EventKind.ReviewFailed, "[2] FAIL (Step review): wrong", "{\"step\":2}")).TeeToLog(sink)) { }

        Assert.Equal(LogSource.Orchestrator, sink.Entries[0].Source);
        Assert.Equal(LogLevel.Warn, sink.Entries[0].Level);
        Assert.Equal(LogSource.Reviewer, sink.Entries[1].Source);
    }
}

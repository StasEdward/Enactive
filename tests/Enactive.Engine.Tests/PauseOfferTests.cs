namespace Enactive.Engine.Tests;

using Enactive.Application.Projections;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// "Pause after this step" as every host offers it (PauseOffer): the window's run header, the service's run page and the
/// console's P key. One rule for when it is offered, so a person is never offered a pause that will not happen - a quick
/// action has no step boundary, and a run that stages its changes ignores a pause (the engine says so in one line).
/// </summary>
public sealed class PauseOfferTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary = "", string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), RunId, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent Planned()
        => Ev(EventKind.PlanCreated, "Report — 2 steps", WorkEventPayload.PlanPayload("Report", ["gather", "write"]));

    private static WorkEvent Quick() => Ev(EventKind.Routed, "Quick action: list files", WorkEventPayload.QuickActionPayload("list files"));

    private static WorkEvent Ended(RunOutcomeKind outcome)
        => Ev(EventKind.TaskFailed, outcome.ToString(), WorkEventPayload.OutcomePayload(outcome, "why"));

    [Fact]
    public void It_is_offered_only_once_the_run_has_a_plan()
    {
        var offer = new PauseOffer(new RunPause());
        Assert.Equal(PauseStanding.NotYet, offer.Standing);
        Assert.Contains("once the run has a plan", offer.Why);

        Assert.True(offer.Apply(Planned()));

        Assert.Equal(PauseStanding.Offered, offer.Standing);
        Assert.Equal(PauseOffer.Label, offer.Text);
    }

    [Fact]
    public void Asking_requests_the_run_s_pause_and_it_is_then_pending()
    {
        var pause = new RunPause();
        var offer = new PauseOffer(pause);
        offer.Apply(Planned());

        Assert.Null(offer.Request());

        Assert.True(pause.IsRequested);
        Assert.Equal(PauseStanding.Pending, offer.Standing);
        Assert.Equal("Pausing after the steps running now…", offer.Text);
        // Asked twice - two people, or a key pressed again: still pending, nothing refused.
        Assert.Null(offer.Request());
    }

    [Fact]
    public void Before_the_plan_it_is_refused_and_nothing_is_requested()
    {
        var pause = new RunPause();
        var offer = new PauseOffer(pause);

        Assert.NotNull(offer.Request());
        Assert.False(pause.IsRequested);
    }

    /// <summary>A quick action has no step to pause after; a staged run ignores a pause. Neither is offered one.</summary>
    [Fact]
    public void A_quick_action_and_a_staged_run_are_not_offered_a_pause()
    {
        var quickPause = new RunPause();
        var quick = new PauseOffer(quickPause);
        quick.Apply(Quick());
        Assert.Equal(PauseStanding.Hidden, quick.Standing);
        Assert.Contains("quick action", quick.Request());
        Assert.False(quickPause.IsRequested);

        var stagedPause = new RunPause();
        var staged = new PauseOffer(stagedPause);
        staged.Staging();
        staged.Apply(Planned());
        Assert.Equal(PauseStanding.Hidden, staged.Standing);
        Assert.Contains("stages its changes", staged.Request());
        Assert.False(stagedPause.IsRequested);
    }

    [Theory]
    [InlineData(RunOutcomeKind.Paused)]
    [InlineData(RunOutcomeKind.Completed)]
    [InlineData(RunOutcomeKind.Failed)]
    public void Once_the_run_has_ended_it_is_gone(RunOutcomeKind outcome)
    {
        var pause = new RunPause();
        var offer = new PauseOffer(pause);
        offer.Apply(Planned());
        offer.Request();

        Assert.True(offer.Apply(Ended(outcome)));

        Assert.Equal(PauseStanding.Hidden, offer.Standing);
        Assert.Equal("The run has already ended.", offer.Request());
    }

    /// <summary>Cancelled, or broken: no terminal event comes, and the host says the run is over.</summary>
    [Fact]
    public void A_run_that_stopped_without_an_end_is_not_offered_a_pause()
    {
        var offer = new PauseOffer(new RunPause());
        offer.Apply(Planned());

        offer.Ended();

        Assert.Equal(PauseStanding.Hidden, offer.Standing);
    }

    [Fact]
    public void A_run_given_no_pause_cannot_be_paused()
    {
        var offer = new PauseOffer(null);
        offer.Apply(Planned());

        Assert.Equal(PauseStanding.Hidden, offer.Standing);
        Assert.Equal("This run cannot be paused.", offer.Request());
    }
}

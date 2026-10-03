namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// The pill and the caption of a run in the history were written before the engine recorded NeedsUser and
/// Blocked: a run waiting for an answer was drawn as one in progress, and a blocked, waiting or unfinished run
/// was captioned like a completed one - "2 artifacts", "41s".
/// </summary>
public sealed class RunStandingTests
{
    /// <summary>Every outcome the engine records, placed on purpose. A new one fails here until it is.</summary>
    private static readonly Dictionary<RunOutcomeKind, (RunStandingKind Standing, string? Word)> Placed = new()
    {
        [RunOutcomeKind.Completed] = (RunStandingKind.Done, null),
        [RunOutcomeKind.Failed] = (RunStandingKind.Failed, "failed"),
        [RunOutcomeKind.Incomplete] = (RunStandingKind.Open, "not finished"),
        [RunOutcomeKind.Cancelled] = (RunStandingKind.Idle, "cancelled"),
        [RunOutcomeKind.NeedsUser] = (RunStandingKind.Open, "waiting for you"),
        [RunOutcomeKind.Blocked] = (RunStandingKind.Open, "blocked")
    };

    [Fact]
    public void Every_outcome_the_engine_records_has_its_pill_and_its_word()
    {
        foreach (var outcome in Enum.GetValues<RunOutcomeKind>())
        {
            Assert.True(Placed.TryGetValue(outcome, out var expected),
                $"{outcome} is not placed: decide which pill it gets and what its row says, here and in RunStanding.");
            Assert.Equal(expected.Standing, RunStanding.Of(outcome.ToString()));
            Assert.Equal(expected.Word, RunStanding.Word(outcome.ToString()));
        }
    }

    [Fact]
    public void No_recorded_outcome_reads_as_a_run_still_going()
    {
        foreach (var outcome in Enum.GetValues<RunOutcomeKind>())
            Assert.NotEqual(RunStandingKind.Running, RunStanding.Of(outcome.ToString()));
    }

    [Fact]
    public void Only_a_completed_run_is_left_to_say_what_it_produced()
    {
        foreach (var outcome in Enum.GetValues<RunOutcomeKind>().Where(o => o != RunOutcomeKind.Completed))
            Assert.NotNull(RunStanding.Word(outcome.ToString()));
    }

    [Theory]
    [InlineData("Interrupted")]
    [InlineData("interrupted")]
    public void A_run_older_history_calls_interrupted_is_open_and_not_finished(string status)
    {
        Assert.Equal(RunStandingKind.Open, RunStanding.Of(status));
        Assert.Equal("not finished", RunStanding.Word(status));
    }

    [Theory]
    [InlineData("Planning")]
    [InlineData("Reviewing")]
    [InlineData("Quarantined")]
    public void A_word_this_build_does_not_know_is_a_phase_of_a_run_in_progress(string phase)
    {
        Assert.Equal(RunStandingKind.Running, RunStanding.Of(phase));
        Assert.Null(RunStanding.Word(phase));
    }

    [Theory]
    [InlineData("NEEDSUSER")]
    [InlineData("needsuser")]
    public void Case_does_not_matter(string status)
        => Assert.Equal(RunStandingKind.Open, RunStanding.Of(status));
}

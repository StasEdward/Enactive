namespace Enactive.Engine.Tests;

using Enactive.Core.History;
using Xunit;

/// <summary>
/// The run column while something is still running.
///
/// <para>It used to say nothing at all about a run in flight: the store has one call and a record
/// is a finished thing, so a run appeared only when it ended. Somebody started a run, opened an
/// older one to compare, and then could not find the run they had just started - it was in no list
/// anywhere.</para>
/// </summary>
public sealed class RunColumnTests
{
    private static LiveRun Running(
        Guid? runId = null, string title = "do the thing", bool headless = false,
        int done = 0, int total = 0, int minutesAgo = 0)
        => new(Guid.NewGuid(), runId ?? Guid.Empty, title, headless, done, total,
               DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    private static RunSummary Finished(Guid runId)
        => new(runId, Guid.NewGuid(), "done thing", null,
               DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
               "Completed", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The decisive one. A run ends and the window learns it twice - the call it was awaiting
    /// returns, and the list is re-read from the store - in whatever order those land. In the
    /// overlap the same run would be "running" at the top and a finished card below, which is one
    /// column saying two things about one run.
    /// </summary>
    [Fact]
    public void A_run_the_history_already_has_is_not_also_shown_as_running()
    {
        var id = Guid.NewGuid();

        var live = RunColumn.Live(new[] { Running(id) }, new[] { Finished(id) });

        Assert.Empty(live);
    }

    /// <summary>
    /// And a run the history does NOT have stays. This is the other half: a rule that dropped
    /// everything would pass the test above and show nothing ever.
    /// </summary>
    [Fact]
    public void A_run_that_has_not_finished_is_shown()
    {
        var live = RunColumn.Live(new[] { Running(Guid.NewGuid()) }, new[] { Finished(Guid.NewGuid()) });

        Assert.Single(live);
    }

    /// <summary>
    /// A row whose run id is not known yet is never dropped. The id is the orchestrator's to mint
    /// and arrives with the first event, so every run is unidentified for its first moments -
    /// exactly the moments when seeing that something started matters most. Nothing can be matched
    /// against the history, so nothing may be concluded from the failure to match.
    /// </summary>
    [Fact]
    public void A_run_whose_id_is_not_known_yet_is_kept()
    {
        var live = RunColumn.Live(new[] { Running() }, new[] { Finished(Guid.NewGuid()) });

        Assert.Single(live);
    }

    /// <summary>Newest first, like the history under it.</summary>
    [Fact]
    public void The_newest_run_is_first()
    {
        var live = RunColumn.Live(
            new[] { Running(title: "older", minutesAgo: 5), Running(title: "newer", minutesAgo: 1) },
            Array.Empty<RunSummary>());

        Assert.Equal(new[] { "newer", "older" }, live.Select(r => r.Title).ToArray());
    }

    /// <summary>
    /// Before there is a plan there are no steps, and "step 0 of 0" would be a progress report
    /// about work nobody has decided on yet. Planning is what is actually happening.
    /// </summary>
    [Fact]
    public void A_run_without_a_plan_yet_says_it_is_planning()
        => Assert.Equal("running · planning", RunColumn.Meta(Running()));

    [Fact]
    public void A_running_step_is_counted_from_one()
        => Assert.Equal("running · step 3 of 5", RunColumn.Meta(Running(done: 2, total: 5)));

    /// <summary>
    /// Every step done and the run still going: the closing work - the review, the summary - is
    /// not a step and has no number. "step 6 of 5" is the alternative.
    /// </summary>
    [Fact]
    public void A_run_past_its_last_step_says_it_is_finishing()
        => Assert.Equal("running · finishing", RunColumn.Meta(Running(done: 5, total: 5)));

    /// <summary>
    /// A headless run says where its answer will be. There is no live feed to open, so the row
    /// must not read like an invitation to open one.
    /// </summary>
    [Fact]
    public void A_background_run_says_its_result_goes_to_the_inbox()
        => Assert.Contains("Inbox", RunColumn.Meta(Running(headless: true, done: 1, total: 3)));
}

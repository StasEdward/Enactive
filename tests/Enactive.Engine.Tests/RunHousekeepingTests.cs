namespace Enactive.Engine.Tests;

using Enactive.Core.History;
using Xunit;

/// <summary>
/// Which runs a filter shows, and which a retention setting forgets.
///
/// <para>These decide what a button called "Delete the 23 shown" destroys, so they are not a
/// display detail and do not live in the window. Nothing ever removed a run before this: 37 of them
/// accumulated in a workspace used for a few days, most of them repeat attempts at one task.</para>
/// </summary>
public sealed class RunHousekeepingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);

    private static RunSummary Run(string status, int daysAgo = 0) => new(
        Guid.NewGuid(), Guid.NewGuid(), "a task", "ollama/x",
        Now.AddDays(-daysAgo), Now.AddDays(-daysAgo), status, [], []);

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Incomplete", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Interrupted", true)]
    [InlineData("Completed", false)]
    public void Unfinished_is_the_ones_that_did_not_do_the_work(string status, bool unfinished)
    {
        Assert.Equal(unfinished, RunHousekeeping.Matches(Run(status), RunFilter.Unfinished, Now));
        Assert.Equal(!unfinished, RunHousekeeping.Matches(Run(status), RunFilter.Completed, Now));
    }

    /// <summary>
    /// A status this build does not know is treated as finished, so a bulk delete under
    /// "unfinished" cannot sweep up a run whose outcome it could not read. The safe direction is the
    /// one that keeps a record somebody may want.
    /// </summary>
    [Fact]
    public void A_status_from_a_future_build_is_not_swept_up()
    {
        Assert.False(RunHousekeeping.Matches(Run("Quarantined"), RunFilter.Unfinished, Now));
        Assert.True(RunHousekeeping.Matches(Run("Quarantined"), RunFilter.All, Now));
    }

    [Fact]
    public void Older_than_a_week_counts_from_when_it_started()
    {
        Assert.False(RunHousekeeping.Matches(Run("Completed", daysAgo: 6), RunFilter.OlderThanAWeek, Now));
        Assert.True(RunHousekeeping.Matches(Run("Completed", daysAgo: 8), RunFilter.OlderThanAWeek, Now));
    }

    [Fact]
    public void Where_returns_exactly_what_the_button_would_delete()
    {
        RunSummary[] runs = [Run("Completed"), Run("Failed"), Run("Incomplete"), Run("Completed")];

        Assert.Equal(2, RunHousekeeping.Where(runs, RunFilter.Unfinished, Now).Count);
        Assert.Equal(4, RunHousekeeping.Where(runs, RunFilter.All, Now).Count);
    }

    // ── retention ────────────────────────────────────────────────────────────────

    [Fact]
    public void Retention_forgets_everything_past_the_newest_kept()
    {
        RunSummary[] runs = [
            Run("Completed", daysAgo: 0), Run("Completed", daysAgo: 1),
            Run("Completed", daysAgo: 2), Run("Completed", daysAgo: 3)];

        var trimmed = RunHousekeeping.BeyondTheNewest(runs, keep: 2);

        Assert.Equal(2, trimmed.Count);
        Assert.All(trimmed, r => Assert.True(r.StartedAt <= Now.AddDays(-2)));
    }

    /// <summary>
    /// Ordered by when a run STARTED. An interrupted run has no meaningful finish, so ordering by
    /// one would put those at the far end and delete them first - exactly backwards, because those
    /// are the runs somebody may still resume.
    /// </summary>
    [Fact]
    public void An_interrupted_run_is_not_deleted_first_for_having_no_end()
    {
        var interrupted = new RunSummary(
            Guid.NewGuid(), Guid.NewGuid(), "interrupted", null,
            Now, default, "Interrupted", [], []);

        RunSummary[] runs = [Run("Completed", daysAgo: 5), interrupted, Run("Completed", daysAgo: 6)];

        var trimmed = RunHousekeeping.BeyondTheNewest(runs, keep: 1);

        Assert.DoesNotContain(interrupted, trimmed);
    }

    /// <summary>
    /// Keeping nothing is not a setting anybody means. Reading an empty or mistyped value as
    /// "delete the lot" is the kind of default that ends a session badly.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Keeping_nothing_means_keeping_everything(int keep)
        => Assert.Empty(RunHousekeeping.BeyondTheNewest([Run("Completed"), Run("Failed")], keep));

    [Fact]
    public void Fewer_runs_than_the_limit_removes_none()
        => Assert.Empty(RunHousekeeping.BeyondTheNewest([Run("Completed"), Run("Failed")], keep: 50));
}

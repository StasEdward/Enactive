namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
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
    /// A run that stopped at a question, or at a cause it cannot remove, has not done its work: it
    /// is kept to be carried on. Both outcomes were added after the filter was written, were in no
    /// list, and so fell through to "finished" - shown under Completed, where "Delete these N"
    /// removes them along with the runs that are really over, and absent from Unfinished, where
    /// somebody looks for what still needs them.
    /// </summary>
    [Theory]
    [InlineData("Blocked")]
    [InlineData("NeedsUser")]
    public void A_run_waiting_to_be_carried_on_is_unfinished_and_not_completed(string status)
    {
        Assert.True(RunHousekeeping.Matches(Run(status), RunFilter.Unfinished, Now));
        Assert.False(RunHousekeeping.Matches(Run(status), RunFilter.Completed, Now));
    }

    /// <summary>
    /// Every outcome the engine can record, and which side of the filter it is on - written out
    /// here, one by one. An outcome missing from this table fails the test: that is how Blocked
    /// and NeedsUser went wrong, by being added to the enum and inheriting "finished" from a list
    /// nobody was made to revisit.
    /// </summary>
    private static readonly Dictionary<RunOutcomeKind, bool> IsUnfinished = new()
    {
        [RunOutcomeKind.Completed] = false,
        [RunOutcomeKind.Failed] = true,
        [RunOutcomeKind.Incomplete] = true,
        [RunOutcomeKind.Cancelled] = true,
        [RunOutcomeKind.NeedsUser] = true,
        [RunOutcomeKind.Blocked] = true
    };

    [Fact]
    public void Every_outcome_the_engine_records_is_classified_on_purpose()
    {
        foreach (var kind in Enum.GetValues<RunOutcomeKind>())
        {
            Assert.True(IsUnfinished.TryGetValue(kind, out var unfinished),
                $"{kind} is a new outcome: decide whether it is unfinished, here and in RunHousekeeping.");

            // The name, because that is what the recorder stores as the run's status.
            var run = Run(kind.ToString());
            Assert.True(unfinished == RunHousekeeping.Matches(run, RunFilter.Unfinished, Now), $"{kind} under Unfinished");
            Assert.True(!unfinished == RunHousekeeping.Matches(run, RunFilter.Completed, Now), $"{kind} under Completed");
        }
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
        Assert.True(RunHousekeeping.Matches(Run("Quarantined"), RunFilter.Completed, Now));
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

    // ── attempts at one task are one row ─────────────────────────────────────────

    private static RunSummary Attempt(Guid task, string status, int daysAgo) => new(
        Guid.NewGuid(), task, "WSFC Workgroup Cluster Setup", "ollama/x",
        Now.AddDays(-daysAgo), Now.AddDays(-daysAgo), status, [], []);

    /// <summary>
    /// The observation this exists for: twelve consecutive rows of one task tried twelve times,
    /// reading as twelve pieces of work. No filter fixes it - every one of those rows is a real run
    /// and belongs in every filter.
    /// </summary>
    [Fact]
    public void Twelve_attempts_at_one_task_are_one_row()
    {
        var task = Guid.NewGuid();
        var runs = Enumerable.Range(0, 12).Select(i => Attempt(task, "Failed", i)).ToArray();

        var rows = RunHousekeeping.Rows(runs);

        var only = Assert.Single(rows);
        Assert.Equal(12, only.Attempts);
        Assert.True(only.IsLead);
    }

    /// <summary>The lead is the NEWEST attempt: what somebody looks for is how it ended up.</summary>
    [Fact]
    public void The_row_shows_the_newest_attempt()
    {
        var task = Guid.NewGuid();
        var newest = Attempt(task, "Completed", daysAgo: 0);

        var rows = RunHousekeeping.Rows([Attempt(task, "Failed", 3), newest, Attempt(task, "Failed", 1)]);

        Assert.Equal(newest.RunId, Assert.Single(rows).Run.RunId);
    }

    /// <summary>
    /// And groups are ordered by their newest attempt, so a task worked on today does not sit below
    /// one abandoned last week for having started earlier.
    /// </summary>
    [Fact]
    public void Groups_are_ordered_by_their_newest_attempt()
    {
        var old = Guid.NewGuid();
        var fresh = Guid.NewGuid();

        var rows = RunHousekeeping.Rows([
            Attempt(old, "Failed", 9), Attempt(old, "Failed", 8),
            Attempt(fresh, "Completed", 1)]);

        Assert.Equal(fresh, rows[0].Run.TaskId);
    }

    [Fact]
    public void Opening_a_group_lists_its_older_attempts_underneath()
    {
        var task = Guid.NewGuid();
        var runs = Enumerable.Range(0, 4).Select(i => Attempt(task, "Failed", i)).ToArray();

        var rows = RunHousekeeping.Rows(runs, new HashSet<string> { RunHousekeeping.GroupKey(runs[0]) });

        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].IsLead);
        Assert.All(rows.Skip(1), r => Assert.False(r.IsLead));

        // The count is on the lead alone: the rows underneath are attempts already counted there.
        Assert.All(rows.Skip(1), r => Assert.Equal(0, r.Attempts));
    }

    /// <summary>
    /// A single attempt is not a group. "1 attempt" on every ordinary run is noise on the common
    /// case to serve the rare one, so the count that would draw an expander is exactly 1 and the
    /// list must not treat it as one.
    /// </summary>
    [Fact]
    public void A_task_tried_once_is_just_a_run()
    {
        var rows = RunHousekeeping.Rows([Attempt(Guid.NewGuid(), "Completed", 0)]);

        Assert.Equal(1, Assert.Single(rows).Attempts);
    }

    /// <summary>
    /// Grouping comes AFTER filtering, and that is the case the filter exists for: a task that
    /// failed twelve times and succeeded on the thirteenth, filtered to Unfinished, shows the
    /// twelve failures and not the success. Grouping first would have to decide what a
    /// half-matching group means, and there is no honest answer to that.
    /// </summary>
    [Fact]
    public void The_filter_chooses_the_attempts_and_grouping_follows()
    {
        var task = Guid.NewGuid();
        var runs = Enumerable.Range(1, 12).Select(i => Attempt(task, "Failed", i))
            .Append(Attempt(task, "Completed", 0)).ToArray();

        var rows = RunHousekeeping.Rows(RunHousekeeping.Where(runs, RunFilter.Unfinished, Now));

        var only = Assert.Single(rows);
        Assert.Equal(12, only.Attempts);
        Assert.Equal("Failed", only.Run.Status);
    }

    private static RunSummary Launch(string templateId, int daysAgo, string status = "Completed")
    {
        var spec = new ResolvedTaskSpec(
            templateId, 1, "WSFC Workgroup Cluster Setup", Guid.NewGuid(), "ws", "C:/ws",
            "set up the cluster", new Dictionary<string, string>(),
            new PermissionPolicy(PermissionLevel.Execute, ["*"], []),
            [], new ExecutionLimits(), null, false);

        return new RunSummary(
            Guid.NewGuid(), Guid.NewGuid(), "WSFC Workgroup Cluster Setup", "ollama/x",
            Now.AddDays(-daysAgo), Now.AddDays(-daysAgo), status, [], [], Spec: spec.Snapshot());
    }

    /// <summary>
    /// The case the first version of this missed entirely.
    ///
    /// <para>Grouping by task id alone collapsed only Retry and Run again, because launching a
    /// template mints a fresh task id every time - so twelve launches of one template stayed twelve
    /// rows, which is exactly the list this was built for. Launches of one template are the same
    /// defined piece of work, repeated.</para>
    /// </summary>
    [Fact]
    public void Twelve_launches_of_one_template_are_one_row()
    {
        var runs = Enumerable.Range(0, 12).Select(i => Launch("wsfc-setup", i)).ToArray();

        var only = Assert.Single(RunHousekeeping.Rows(runs));

        Assert.Equal(12, only.Attempts);
    }

    [Fact]
    public void Different_templates_are_different_rows()
    {
        var rows = RunHousekeeping.Rows([Launch("wsfc-setup", 1), Launch("code-review", 0)]);

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// A retry of a template launch shares the task id AND the template, and belongs with the other
    /// launches of that template rather than in a group of its own. They are all "that template,
    /// run again".
    /// </summary>
    [Fact]
    public void A_retry_of_a_template_launch_joins_its_template()
    {
        var launch = Launch("wsfc-setup", 2);
        var retry = launch with { RunId = Guid.NewGuid(), StartedAt = Now.AddDays(-1) };

        Assert.Equal(2, Assert.Single(RunHousekeeping.Rows([launch, retry])).Attempts);
    }

    /// <summary>
    /// Typed runs are NOT grouped by their text. Two tasks reading "clean the directory", started
    /// on different days against a workspace in different states, are two different pieces of work;
    /// folding them into one row would be a claim about them that is not true. Identical text means
    /// the phrase matched, not that the work was the same.
    /// </summary>
    [Fact]
    public void Two_typed_runs_with_the_same_words_stay_apart()
    {
        var first = Run("Completed", daysAgo: 1);
        var second = Run("Completed", daysAgo: 0);

        Assert.Equal(2, RunHousekeeping.Rows([first, second]).Count);
    }

    /// <summary>A spec that cannot be read falls back to the task id rather than to one big group.</summary>
    [Fact]
    public void An_unreadable_spec_does_not_merge_unrelated_runs()
    {
        var a = Run("Completed", 1) with { Spec = "{not json" };
        var b = Run("Completed", 0) with { Spec = "{not json" };

        Assert.Equal(2, RunHousekeeping.Rows([a, b]).Count);
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

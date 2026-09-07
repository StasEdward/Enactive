namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// M4 of Docs/TASK_TEMPLATES_PLAN.md: a TASK is what was asked for, a RUN is one attempt at it.
///
/// <para><c>RunRecord.TaskId</c> existed from the start and was a fresh Guid on every run, so it
/// grouped nothing — a column that looked like a key and behaved as a serial number. Until an
/// attempt could be told from a task there was nothing for "retry" to mean, which is why it did not
/// exist.</para>
/// </summary>
public sealed class RunHistoryTests
{
    private static RunRecord Run(
        Guid taskId, int minutesAgo, string status = "Completed",
        string? request = null, Guid? runId = null, string? summary = null)
    {
        var at = DateTimeOffset.Now.AddMinutes(-minutesAgo);
        var events = request is null
            ? Array.Empty<RunEventRecord>()
            : new[]
            {
                new RunEventRecord(at, nameof(EventKind.IntentReceived),
                                   summary ?? "Intent: " + request,
                                   null, WorkEventPayload.RequestPayload(request))
            };

        return new RunRecord(
            runId ?? Guid.NewGuid(), taskId, "a task", "fake-model", at, at.AddSeconds(30), status,
            events, Array.Empty<string>(), Array.Empty<string>());
    }

    // ── grouping ────────────────────────────────────────────────────────────

    [Fact]
    public void Attempts_at_one_task_are_one_group_newest_first()
    {
        var task = Guid.NewGuid();
        var oldest = Run(task, 30);
        var middle = Run(task, 20);
        var newest = Run(task, 10);

        var groups = RunHistory.ByTask(new[] { middle, oldest, newest });

        var only = Assert.Single(groups);
        Assert.Equal(task, only.TaskId);
        Assert.Equal(3, only.Count);
        Assert.Equal(newest.RunId, only.Latest.RunId);
        Assert.Equal(new[] { newest.RunId, middle.RunId, oldest.RunId }, only.Runs.Select(r => r.RunId));
    }

    [Fact]
    public void Tasks_are_ordered_by_their_most_recent_attempt()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        var groups = RunHistory.ByTask(new[]
        {
            Run(older, 60), Run(older, 5),    // retried recently
            Run(newer, 30)
        });

        Assert.Equal(2, groups.Count);
        Assert.Equal(older, groups[0].TaskId);
    }

    /// <summary>
    /// Every run recorded before this milestone has a task id of its own, so history stays exactly
    /// as it reads today - one row per run. Nothing is retroactively grouped, and nothing should be.
    /// </summary>
    [Fact]
    public void Runs_from_before_tasks_meant_anything_stay_apart()
    {
        var groups = RunHistory.ByTask(new[] { Run(Guid.NewGuid(), 3), Run(Guid.NewGuid(), 2) });

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(1, g.Count));
    }

    /// <summary>
    /// An empty id is the ABSENCE of an answer, not an answer they share. Grouping them together
    /// would file unrelated work under one heading, which is a worse lie than showing them apart.
    /// </summary>
    [Fact]
    public void Runs_with_no_task_id_are_not_all_the_same_task()
    {
        var groups = RunHistory.ByTask(new[] { Run(Guid.Empty, 3), Run(Guid.Empty, 2) });

        Assert.Equal(2, groups.Count);
    }

    // ── an attempt's number ─────────────────────────────────────────────────

    [Fact]
    public void The_first_attempt_is_number_one_however_the_list_is_ordered()
    {
        var task = Guid.NewGuid();
        var first = Run(task, 30);
        var second = Run(task, 20);
        var third = Run(task, 10);

        var attempts = RunHistory.AttemptsOf(new[] { second, third, first }, second);

        Assert.Equal(3, attempts.Count);
        Assert.Equal(1, RunHistory.AttemptNumber(attempts, first.RunId));
        Assert.Equal(2, RunHistory.AttemptNumber(attempts, second.RunId));
        Assert.Equal(3, RunHistory.AttemptNumber(attempts, third.RunId));
        Assert.Equal(0, RunHistory.AttemptNumber(attempts, Guid.NewGuid()));
    }

    [Fact]
    public void A_run_with_no_task_id_is_alone_even_among_others_like_it()
    {
        var lonely = Run(Guid.Empty, 5);
        var attempts = RunHistory.AttemptsOf(new[] { lonely, Run(Guid.Empty, 4) }, lonely);

        Assert.Equal(lonely.RunId, Assert.Single(attempts).RunId);
    }

    [Fact]
    public void A_task_knows_whether_any_attempt_ever_worked()
    {
        var task = Guid.NewGuid();

        Assert.False(RunHistory.ByTask(new[] { Run(task, 9, "Failed"), Run(task, 8, "Incomplete") })[0].EverSucceeded);
        Assert.True(RunHistory.ByTask(new[] { Run(task, 9, "Failed"), Run(task, 8, "Completed") })[0].EverSucceeded);
    }

    // ── what was asked for ──────────────────────────────────────────────────

    /// <summary>
    /// Read from the event's PAYLOAD, never from its summary.
    ///
    /// <para>The summary is DISPLAY TEXT — "Intent: &lt;text&gt;" today, and whatever anyone rewords
    /// it to tomorrow. Recovering the request by stripping a prefix works right up until that line
    /// changes, and then it silently returns something else and a retry asks for the wrong thing.
    /// So the test rewords it: the payload still gives the request back, and a prefix parser gives
    /// the whole sentence.</para>
    /// </summary>
    [Fact]
    public void The_request_survives_its_summary_being_reworded()
    {
        var record = Run(
            Guid.NewGuid(), 1,
            request: "add a Remote Access button",
            summary: "Received a request: add a Remote Access button");

        Assert.Equal("add a Remote Access button", RunHistory.RequestOf(record));
    }

    [Fact]
    public void A_run_recorded_before_the_request_was_kept_simply_has_none()
        => Assert.Null(RunHistory.RequestOf(Run(Guid.NewGuid(), 1)));

    [Fact]
    public void A_payload_that_carries_no_request_is_not_mistaken_for_one()
    {
        Assert.Null(WorkEventPayload.RequestTextIn(null));
        Assert.Null(WorkEventPayload.RequestTextIn(""));
        Assert.Null(WorkEventPayload.RequestTextIn("""{"step":2}"""));
        Assert.Null(WorkEventPayload.RequestTextIn("not json"));
        Assert.Equal("do the thing", WorkEventPayload.RequestTextIn(
            WorkEventPayload.RequestPayload("do the thing")));
    }
}

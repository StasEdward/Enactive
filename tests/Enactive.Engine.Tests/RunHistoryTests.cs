namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// M4 of the task-templates plan: a TASK is what was asked for, a RUN is one attempt at it.
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

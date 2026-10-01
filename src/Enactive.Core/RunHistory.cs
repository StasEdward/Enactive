namespace Enactive.Core.History;

using Enactive.Core.Events;

/// <summary>
/// Reads a workspace's runs as tasks and attempts.
///
/// <para>A TASK is what was asked for, a RUN is one attempt at it. <c>RunRecord.TaskId</c> existed from
/// the start and was a fresh Guid on every run, so it grouped nothing - a column that looked like a key
/// and was a serial number. Until an attempt could be told from a task there was nothing for "retry" to
/// mean, which is why it did not exist.</para>
///
/// <para>Everything here takes an <see cref="IRunHeader"/>, because grouping and counting attempts
/// needs the task id and the start time and nothing else. That is what lets the history list do its
/// grouping off summaries and never load a transcript. <see cref="RequestOf"/> is the exception and
/// takes a whole record, because what was asked for is recorded in an EVENT.</para>
/// </summary>
public static class RunHistory
{
    /// <summary>
    /// Every attempt at the same task as <paramref name="run"/>, newest first — including it.
    /// A run whose task id is empty is alone, which is the honest answer for a record written before
    /// tasks meant anything.
    /// </summary>
    public static IReadOnlyList<IRunHeader> AttemptsOf(IEnumerable<IRunHeader> all, IRunHeader run)
    {
        if (run.TaskId == Guid.Empty)
            return new[] { run };

        return all.Where(r => r.TaskId == run.TaskId)
                  .OrderByDescending(r => r.StartedAt)
                  .ToArray();
    }

    /// <summary>
    /// Which attempt this run is, counting from the first: 1-based, and 0 when the run is not in the
    /// list at all.
    /// </summary>
    public static int AttemptNumber(IReadOnlyList<IRunHeader> attempts, Guid runId)
    {
        // Attempts arrive newest first, so the oldest is number one.
        for (var i = 0; i < attempts.Count; i++)
            if (attempts[i].RunId == runId)
                return attempts.Count - i;

        return 0;
    }

    /// <summary>
    /// What was originally asked for, from the run's own IntentReceived event.
    ///
    /// <para>Read from the event's PAYLOAD, never from its summary. The summary is
    /// "Intent: &lt;text&gt;" and taking the text apart from it would be the exact habit the typed
    /// payloads were introduced to end - and it would break the first time a request began with
    /// something that looked like a prefix. Null for a run recorded before the payload existed,
    /// which is a run that simply cannot be repeated by its text.</para>
    /// </summary>
    public static string? RequestOf(RunRecord record)
    {
        foreach (var ev in record.Events)
        {
            if (!string.Equals(ev.Kind, nameof(EventKind.IntentReceived), StringComparison.Ordinal))
                continue;

            var text = WorkEventPayload.RequestTextIn(ev.Payload);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }
}

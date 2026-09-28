namespace Enactive.Agents;

using Enactive.Core.History;

/// <summary>How a host carries on a run that stopped at a question, once the question is answered.</summary>
public static class ParkedRuns
{
    /// <summary>
    /// The step boundary to carry the task on from: its latest resumable checkpoint. Null for a run
    /// that never had one - a quick action has no steps to stop between - and such a task is started
    /// again under the SAME task id, which is what lets the recorded answer find its question.
    /// </summary>
    public static async Task<RunCheckpoint?> CheckpointForAsync(IRunCheckpointStore store, Guid taskId, CancellationToken ct)
        => (await store.LoadAllAsync(ct))
            .Where(c => c.TaskId == taskId && c.IsResumable)
            .OrderByDescending(c => c.At)
            .FirstOrDefault();
}

namespace AIClient.Agents;

using System.Runtime.CompilerServices;
using AIClient.Core.Events;
using AIClient.Core.History;

/// <summary>
/// Wraps an orchestrator event stream, re-yielding every event unchanged while accumulating a
/// <see cref="RunRecord"/> that is saved when the run ends (even on failure or cancellation).
/// Keeps the orchestrator pure — persistence is a consumer concern.
/// </summary>
public sealed class RunRecorder
{
    private readonly IRunStore _store;

    public RunRecorder(IRunStore store) => _store = store;

    public async IAsyncEnumerable<WorkEvent> RecordAsync(
        IAsyncEnumerable<WorkEvent> stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var events = new List<WorkEvent>();
        try
        {
            await foreach (var ev in stream.WithCancellation(ct))
            {
                events.Add(ev);
                yield return ev;
            }
        }
        finally
        {
            if (events.Count > 0)
                await _store.SaveAsync(Build(events), CancellationToken.None);
        }
    }

    private static RunRecord Build(List<WorkEvent> events)
    {
        var first = events[0];
        var last = events[^1];

        var title = "(intent)";
        string? model = null;
        var artifacts = new List<string>();
        var decisions = new List<string>();
        var eventRecords = new List<RunEventRecord>();
        var failed = false;
        var completed = false;

        foreach (var ev in events)
        {
            eventRecords.Add(new RunEventRecord(ev.At, ev.Kind.ToString(), ev.Summary));
            switch (ev.Kind)
            {
                case EventKind.IntentReceived:
                    title = StripPrefix(ev.Summary, "Intent: ");
                    break;
                case EventKind.Routed when model is null && ev.Summary.Contains("-> model "):
                    model = ExtractAfter(ev.Summary, "-> model ");
                    break;
                case EventKind.ArtifactProduced:
                    artifacts.Add(ev.Summary);
                    break;
                case EventKind.DecisionResolved:
                    decisions.Add(ev.Summary);
                    break;
                case EventKind.TaskFailed:
                    failed = true;
                    break;
                case EventKind.TaskCompleted:
                    completed = true;
                    break;
            }
        }

        var status = failed ? "Failed" : completed ? "Completed" : "Incomplete";
        return new RunRecord(
            first.RunId, first.TaskId, title, model,
            first.At, last.At, status, eventRecords, artifacts, decisions);
    }

    private static string StripPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string? ExtractAfter(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? value[(index + marker.Length)..].Trim() : null;
    }
}

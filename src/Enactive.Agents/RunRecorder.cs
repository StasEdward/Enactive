namespace Enactive.Agents;

using System.Runtime.CompilerServices;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Memory;

/// <summary>
/// Wraps an orchestrator event stream, re-yielding every event unchanged while accumulating a
/// <see cref="RunRecord"/> that is saved when the run ends (even on failure or cancellation).
/// Keeps the orchestrator pure — persistence is a consumer concern.
/// </summary>
public sealed class RunRecorder
{
    private readonly IRunStore _store;
    private readonly IMemoryStore? _memory;
    private readonly Guid _workspaceId;
    private readonly RunSettings? _settings;

    public RunRecorder(
        IRunStore store, IMemoryStore? memory = null, Guid workspaceId = default,
        RunSettings? settings = null)
    {
        _store = store;
        _memory = memory;
        _workspaceId = workspaceId;
        // Handed in rather than read back later: what the run was allowed to do is a fact about the
        // moment it started, and the slider will have moved by the time anyone asks.
        _settings = settings;
    }

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
            {
                var record = Build(events, _settings);
                await _store.SaveAsync(record, CancellationToken.None);

                // Fold each resolved decision into the project's durable memory (PLAN_v2 §2.6).
                if (_memory is not null)
                    foreach (var decision in record.Decisions)
                        await _memory.AppendAsync(
                            new MemoryEntry(Guid.NewGuid(), _workspaceId, "decision", decision, null, record.FinishedAt),
                            CancellationToken.None);
            }
        }
    }

    private static RunRecord Build(List<WorkEvent> events, RunSettings? settings)
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
        var promptTokens = 0;
        var completionTokens = 0;
        var sawUsage = false;

        foreach (var ev in events)
        {
            // The step number is stamped HERE, while the event still carries it. Nothing downstream
            // can work it out again: replaying by order attributes a tool call to whichever step
            // started last, which is the wrong one as soon as two run at once.
            eventRecords.Add(new RunEventRecord(ev.At, ev.Kind.ToString(), ev.Summary, ev.StepNo()));
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
                case EventKind.UsageReported when ev.Usage() is { } used:
                    promptTokens += used.In;
                    completionTokens += used.Out;
                    sawUsage = true;
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
            first.At, last.At, status, eventRecords, artifacts, decisions, settings,
            // Null rather than zero when nothing reported: "this provider does not tell us" and
            // "this run used no tokens" are different facts and are shown differently.
            sawUsage ? new RunUsage(promptTokens, completionTokens) : null);
    }

    private static string StripPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string? ExtractAfter(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? value[(index + marker.Length)..].Trim() : null;
    }
}

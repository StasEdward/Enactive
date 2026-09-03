namespace AIClient.Agents;

using System.Runtime.CompilerServices;
using AIClient.Core.Diagnostics;
using AIClient.Core.Events;

/// <summary>
/// Bridges the orchestrator's domain <see cref="WorkEvent"/> stream into the global log without the
/// orchestrator knowing anything about logging. A host pipes the run through <see cref="TeeToLog"/>:
/// every event is mirrored to the sink (tagged with the event's own RunId/TaskId) and then passed
/// through unchanged, so existing consumers are unaffected. This is the Orchestrator/Task/Tool plane
/// at the domain level; raw prompts/responses come from the provider layer.
/// </summary>
public static class EventLogTap
{
    public static async IAsyncEnumerable<WorkEvent> TeeToLog(
        this IAsyncEnumerable<WorkEvent> source,
        ILogSink? log,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in source.WithCancellation(ct))
        {
            if (log is not null)
            {
                try
                {
                    var (src, level) = Map(ev.Kind);
                    log.Log(new LogEntry(
                        Seq: 0,
                        At: ev.At,
                        Level: level,
                        Source: src,
                        RunId: ev.RunId,
                        TaskId: ev.TaskId,
                        Message: $"{ev.Kind}: {ev.Summary}",
                        Detail: ev.PayloadJson,
                        Category: ev.Kind.ToString()));
                }
                catch { /* logging must never break the run */ }
            }
            yield return ev;
        }
    }

    private static (LogSource Source, LogLevel Level) Map(EventKind kind) => kind switch
    {
        EventKind.IntentReceived => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.ContextAssembled => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.Routed => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.PlanCreated => (LogSource.Planner, LogLevel.Info),
        EventKind.StepStarted => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.StepCompleted => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.AssistantDelta => (LogSource.Llm, LogLevel.Trace),   // per-token — Trace keeps it out of the default view
        EventKind.ToolInvoked => (LogSource.Tool, LogLevel.Debug),
        EventKind.ToolResult => (LogSource.Tool, LogLevel.Debug),
        EventKind.DecisionRequested => (LogSource.Permission, LogLevel.Info),
        EventKind.DecisionResolved => (LogSource.Permission, LogLevel.Info),
        EventKind.ReviewRequested => (LogSource.Reviewer, LogLevel.Info),
        EventKind.ReviewPassed => (LogSource.Reviewer, LogLevel.Info),
        EventKind.ReviewFailed => (LogSource.Reviewer, LogLevel.Warn),
        EventKind.ArtifactProduced => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.ErrorObserved => (LogSource.Orchestrator, LogLevel.Warn),
        EventKind.TaskCompleted => (LogSource.Orchestrator, LogLevel.Info),
        EventKind.TaskFailed => (LogSource.Orchestrator, LogLevel.Error),
        _ => (LogSource.Orchestrator, LogLevel.Info)
    };
}

namespace Enactive.Agents;

using Enactive.Core.Tools;
using Enactive.Core.Execution;
using static Enactive.Agents.ToolCallParsing;

/// <summary>One result enters progress, unresolved failures, read coverage, evidence and the step's boundary, in this
/// order. Event emission, artifact publication and transcript lifetime remain with the tool loop.</summary>
/// <param name="recordNamed">Whether to keep what each call named (ExecutedAction.Named): only the coverage of step
/// outputs reads it, and with them off it is every call's arguments parsed again for nothing. Left out, kept - the safe
/// way round: a missing record makes coverage fall back to the compacted arguments, which name less.</param>
internal sealed class ToolResultAccounting(StepFrame frame, IToolRegistry tools, RepairAttempts repairAttempts,
    bool repairEnabled, ToolCallOrigin origin, bool recordNamed = true)
{
    internal string? Record(ToolCall call, ToolInvocation.Result invocation)
    {
        var result = invocation.Value;
        // The same revision accounting applies to serial calls and completed parallel reads.
        frame.Progress.Resulted(call, result, invocation.Before, invocation.After);
        if (repairEnabled) repairAttempts.Observe(call, result, tools.DefinitionOf(call.Name)?.Kind == ToolKind.Command);

        if (result.Success)
            frame.Open.Succeeded(call, result.Artifacts);
        else if (result.IsAnswer)
            frame.Open.FoundNothing(call, result.Error);
        else
            frame.Open.Failed(call, result.Error, result.DidNotRun);

        // What a failure SAYS: the error, and the output under it when there is one. Built
        // once, here, because the model and the reviewer each get a copy and on 2026-09-07
        // 23:42 they got different ones. The model was told "Command exited with code
        // -532462766" and, beneath it, the stderr: "Unhandled exception.
        // System.ArgumentException: HTML cannot be null or empty" - the very crash the step
        // was there to cause. The journal recorded the first line only. So the reviewer,
        // handed evidence with no exception in it, read the agent's true account of the
        // crash and called it fabricated. The failure-carries-its-output fix below had
        // been made for the transcript alone; the evidence is the record that gets judged.
        var failure = string.IsNullOrWhiteSpace(result.Output)
            ? result.Error
            : $"{result.Error}\n{result.Output}";

        // The evidence, written down at the moment it exists. Nothing that shortens the
        // prompt afterwards can take it away.
        frame.Reads.Saw(call, result, tools.DefinitionOf(call.Name));

        frame.Journal.Record(
            frame.StepNo, call.Name, Compact(call.ArgumentsJson),
            result.Metadata.ContainsKey("taskConstraintRefusal") ? ActionOutcome.Refused
                : result.Success ? ActionOutcome.Succeeded
                : result.IsAnswer ? ActionOutcome.Answered
                : ActionOutcome.Failed,
            result.Success ? result.Output : failure,
            result.WorkspaceEffect ?? WorkspaceEffect.Unknown, result.ChangedPaths,
            exitCode: !result.DidNotRun && tools.DefinitionOf(call.Name)?.Kind == ToolKind.Command
                && result.Metadata.TryGetValue("exitCode", out var code) && code is int exit ? exit : null,
            fileDeletion: !result.DidNotRun && result.Success && RecordedOperations.DeletesFiles(call, tools.DefinitionOf(call.Name)),
            origin: origin,
            // What the call named, from its FULL arguments: the recorded ones are compacted for reading and cut at 120
            // characters, and coverage read its mentions from those - a write_file call named nothing (EvidenceCoverage.NamesIn).
            // Only when coverage will read it (recordNamed): nothing else does.
            named: recordNamed ? EvidenceCoverage.NamesIn(call.ArgumentsJson) : null);

        // What the step may change, told of a change made within it - a result, so here and not in the loop.
        if (result.Success) frame.Boundary?.Succeeded(call);

        return failure;
    }
}

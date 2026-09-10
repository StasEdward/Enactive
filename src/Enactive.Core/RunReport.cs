namespace Enactive.Core.History;

using System.Text;
using Enactive.Core.Events;

/// <summary>
/// What a run did, for somebody who was not watching it.
///
/// <para>A scheduled run's whole audience is a person reading it afterwards, and a scheduler's own
/// log is usually gone by then. This is the artifact that outlives both.</para>
///
/// <para>Everything here is read from the record's typed payloads, never from the wording of its
/// summaries - the same rule the rest of the engine follows, and the reason a reworded log line
/// cannot quietly change what a report says happened.</para>
/// </summary>
public static class RunReport
{
    /// <summary>
    /// The exit code a scheduler reads.
    ///
    /// <para>Distinct codes for Failed and Incomplete because the distinction is real and useful at
    /// three in the morning: FAILED means the work is wrong, INCOMPLETE means we could not finish or
    /// could not check. A pipeline may well want to stop for the first and only warn on the second,
    /// and collapsing both to 1 takes that choice away.</para>
    /// </summary>
    public static int ExitCodeFor(RunOutcomeKind outcome) => outcome switch
    {
        RunOutcomeKind.Completed => 0,
        RunOutcomeKind.Failed => 1,
        RunOutcomeKind.Incomplete => 2,
        RunOutcomeKind.Cancelled => 130,   // the shell convention for SIGINT
        _ => 1
    };

    /// <summary>The run's outcome, or null when it never wrote a terminal event.</summary>
    public static RunOutcomeKind? OutcomeOf(RunRecord record)
    {
        foreach (var ev in record.Events)
        {
            if (!string.Equals(ev.Kind, nameof(EventKind.TaskCompleted), StringComparison.Ordinal)
                && !string.Equals(ev.Kind, nameof(EventKind.TaskFailed), StringComparison.Ordinal))
                continue;

            if (WorkEventPayload.OutcomeIn(ev.Payload) is { } kind)
                return kind;
        }

        // Older records, and the status column as a fallback: it is written from the same outcome.
        return Enum.TryParse<RunOutcomeKind>(record.Status, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Why a record rebuilds no step cards. Empty when it does have them.
    ///
    /// <para>There are two reasons and they are nothing alike, which is the point. A record written
    /// before events carried step numbers cannot be rebuilt without guessing. A run that STOPPED
    /// BEFORE PLANNING has no steps because it never had a plan - and telling that person their
    /// record is too old sends them looking for a history problem instead of at the failure that is
    /// sitting in the same record.</para>
    ///
    /// <para>Found on 2026-09-10: two scheduled runs died before planning, and the window explained
    /// both with the sentence about old records. It was the only reason that had ever existed when
    /// the sentence was written, and it was wrong the first time a run failed early.</para>
    /// </summary>
    public static string WhyNoSteps(RunRecord record)
    {
        if (record.Events.Any(e => e.Step is not null))
            return "";

        var planned = record.Events.Any(
            e => string.Equals(e.Kind, nameof(EventKind.PlanCreated), StringComparison.Ordinal));

        if (planned)
            return "This run was recorded before step numbers were, so its steps cannot be rebuilt "
                 + "without guessing which one each tool call came from. The timeline has everything "
                 + "it does hold.";

        var reason = ReasonOf(record);
        return "This run stopped before it had a plan, so there are no steps to show"
             + (string.IsNullOrWhiteSpace(reason) ? "" : " — " + reason)
             + ". The timeline has what it got through.";
    }

    public static string Render(RunRecord record, string workspaceRoot)
    {
        var sb = new StringBuilder();
        var outcome = OutcomeOf(record);

        sb.AppendLine($"ENACTIVE RUN REPORT — {record.Title}");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine($"  workspace : {workspaceRoot}");
        sb.AppendLine($"  run       : {record.RunId:N}");
        sb.AppendLine($"  task      : {record.TaskId:N}");
        sb.AppendLine($"  started   : {record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  took      : {Duration(record.FinishedAt - record.StartedAt)}");
        sb.AppendLine($"  model     : {record.Model ?? "(not recorded)"}");

        var reason = ReasonOf(record);
        sb.AppendLine($"  OUTCOME   : {outcome?.ToString() ?? record.Status}"
                    + (string.IsNullOrWhiteSpace(reason) ? "" : " — " + reason));

        // ── the checks, which are the only part of a run that is not somebody's opinion ──
        var checks = record.Events
            .Where(e => string.Equals(e.Kind, nameof(EventKind.CriterionEvaluated), StringComparison.Ordinal))
            .ToArray();

        sb.AppendLine();
        if (checks.Length == 0)
        {
            sb.AppendLine("  CHECKS    : none — nothing verified this run beyond the model's own report.");
        }
        else
        {
            sb.AppendLine("  CHECKS");
            foreach (var check in checks)
                sb.AppendLine("    " + check.Summary.Replace("\n", "\n      "));
        }

        // ── what it did ──
        var steps = record.Events
            .Where(e => string.Equals(e.Kind, nameof(EventKind.StepCompleted), StringComparison.Ordinal))
            .ToArray();

        if (steps.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  STEPS");
            foreach (var step in steps)
                sb.AppendLine("    " + step.Summary);
        }

        sb.AppendLine();
        sb.AppendLine($"  FILES     : {(record.Artifacts.Count == 0 ? "(none changed)" : string.Join(", ", record.Artifacts))}");
        sb.AppendLine($"  TOKENS    : {(record.Usage is { } u
            ? $"{u.Total} ({u.PromptTokens} in, {u.CompletionTokens} out)"
            : "not reported")}");

        if (record.Decisions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  DECISIONS");
            foreach (var decision in record.Decisions)
                sb.AppendLine("    " + decision);
        }

        // The errors last, because that is what a person scrolls to the bottom for.
        var errors = record.Events
            .Where(e => string.Equals(e.Kind, nameof(EventKind.ErrorObserved), StringComparison.Ordinal))
            .ToArray();

        if (errors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  PROBLEMS");
            foreach (var error in errors)
                sb.AppendLine("    " + error.Summary);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// What the engine recorded about how the run ENDED, or null when it recorded nothing.
    ///
    /// <para>Public because the Inbox needs the same sentence the report prints. Two readings of
    /// "why did it stop" drift, and the one that drifts is the one with no test.</para>
    /// </summary>
    public static string? ReasonOf(RunRecord record)
    {
        foreach (var ev in record.Events)
            if (string.Equals(ev.Kind, nameof(EventKind.TaskFailed), StringComparison.Ordinal)
                || string.Equals(ev.Kind, nameof(EventKind.TaskCompleted), StringComparison.Ordinal))
                if (WorkEventPayload.OutcomeReasonIn(ev.Payload) is { Length: > 0 } reason)
                    return reason;

        return null;
    }

    private static string Duration(TimeSpan span)
    {
        var seconds = Math.Max(0, span.TotalSeconds);
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }
}

namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// The declared workspace checks (Plan Phase 3.2), run where they are owed: at a step's gate, at the end of a wave, at the
/// run's end - each through the same evaluator, tool and permission gate as every success criterion.
/// </summary>
public sealed partial class Orchestrator
{
    /// <summary>The engine's record, for the review, of the declared checks it ran on what the step changed.</summary>
    internal const string StepChecksTool = "engine_ran_step_checks";

    private const string StepChecksHistorical = "HISTORICAL - the checks were run on an earlier attempt of this step, which has "
        + "since been changed; what they found now is recorded below.";

    /// <summary>What the checks at a step's gate came to: a blocking one that failed, and why blocking ones did not run.</summary>
    /// <param name="Failed">The first blocking check that failed, with its result - the step goes back on the gate's budget.</param>
    /// <param name="NotRun">Which blocking checks did not run and why; the step is then done at best, never confirmed.</param>
    private sealed record StepCheckVerdict((ResolvedCheck Check, CriterionResult Result)? Failed, string? NotRun);

    /// <summary>
    /// Runs one declared check, labelled with where it was owed (<paramref name="at"/>). Never an exception and never a
    /// pass it cannot stand behind: a staged run's check, one the policy would ask about, one that found nothing to run or
    /// did not finish in its time are each <see cref="CriterionOutcome.Unknown"/>, with the reason.
    /// </summary>
    private async Task<CriterionResult> RunDeclaredCheckAsync(ResolvedCheck check, string at, RunScope scope,
        WorkContext context, CancellationToken ct)
    {
        var definition = check.Definition;
        var name = WorkspaceChecks.Label(definition.Id, at);
        var blocking = definition.Severity == CheckSeverity.Blocking;
        var criteria = WorkspaceChecks.Criteria(check, name, _ecosystems, _workspace.RootPath, out var none);
        var command = WorkspaceChecks.CommandOf(check, criteria);
        CriterionResult Unknown(string why) => new(name, command, blocking, CriterionOutcome.Unknown, null, why, CriterionOrigin.Declared);

        if (_settings?.Staged == true)
            return Unknown(WorkspaceChecks.StagedDetail);
        // The engine's own gate for a command nobody is asked about (BuildRegression.WhyNotAllowed): a check runs only when
        // the policy allows its command outright. Asked in the middle of a step, a wave or a run's end, a person approves a
        // command they did not see coming; unattended, nobody answers. So in an ask-mode run a blocking check is often not
        // run - and that is said, "not run: needs permission", never skipped as if there were nothing to check.
        if (BuildRegression.WhyNotAllowed(_tools, _permissions, _policy, WorkspaceChecks.NotAsked) is { } why)
            return Unknown($"{WorkspaceChecks.NeedsPermission} - {why}");
        if (none is not null)
            return Unknown(none);

        // The check's own time, given to the command tool for each of its commands (ToolContext.CommandTimeoutSeconds) -
        // the tool's own limit is a worker's, and a check declared with more was cut at it - and held over the whole check
        // too, for one that runs several commands (an ecosystem's targets). Either way its process is stopped with it.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        try
        {
            var report = await CheckSuccessAsync(criteria, scope.TaskId, scope.RunId, context, limit.Token,
                commandTimeoutSeconds: definition.TimeoutSeconds);
            return WorkspaceChecks.Combine(name, command, blocking, report.Results);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unknown(SuccessEvaluator.DidNotFinish(definition.TimeoutSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unknown("the check could not be run: " + ex.Message);
        }
    }

    /// <summary>
    /// The checks a step's changes are owed, at its gate (Plan Phase 3.2): run now, deferred to a boundary, or - staged -
    /// owed when the files are applied. What ran is said as a criterion each, and put in the journal for the reviewer as
    /// the engine's own record; null when nothing is owed.
    /// </summary>
    private async Task<StepCheckVerdict?> StepChecksAsync(RunSession session, StepAttemptState step, int? stepNumber,
        WorkContext context, IWorkspaceChanges? changes, WorkspaceSnapshot? before, Func<WorkEvent, ValueTask> publish,
        CancellationToken ct)
    {
        var owed = WorkspaceChecks.Firing(session.Checks, CheckFire.Step);
        if (owed.Count == 0) return null;
        var scope = session.Scope;
        var prefix = stepNumber is { } number ? $"[{number}] " : "";

        var changed = WorkspaceChecks.Of(await MeasuredSinceAsync(changes, before, ct),
            step.Journal.Actions.Skip(step.StepStart), step.Store.TouchedPaths, _workspace.RootPath,
            tool => _tools.DefinitionOf(tool)?.Kind == ToolKind.Command);
        var due = changed.Unmeasured is not null ? owed : owed.Where(c => WorkspaceChecks.Covers(c, changed.Paths, _ecosystems)).ToArray();
        if (due.Count == 0) return null;
        if (changed.Unmeasured is { } unmeasured)
            await publish(scope.Ev(EventKind.ContextAssembled,
                $"{prefix}Every step check is owed: {unmeasured} ({string.Join(", ", due.Select(c => c.Id))}).", stepNumber));

        var at = stepNumber is { } no ? $"step {no}" : "the step";
        var now = new List<ResolvedCheck>();
        var deferred = new List<ResolvedCheck>();
        foreach (var check in due)
        {
            var blocking = check.Definition.Severity == CheckSeverity.Blocking;
            if (step.CheckTiming == StepCheckTiming.Staged)
                Owe(session, new PendingCheck(check.Id, CheckBoundary.Apply, WorkspaceChecks.Staged) { Step = step.StepId });
            else if (step.CheckTiming == StepCheckTiming.Deferred && blocking)
                Owe(session, session.Waves is { } waves
                    ? new PendingCheck(check.Id, CheckBoundary.Wave, $"owed at the end of wave {waves.Closed + 1}: steps run side by side, "
                        + "so what it finds cannot be put on one of them") { Step = step.StepId, Wave = waves.Closed + 1 }
                    : new PendingCheck(check.Id, CheckBoundary.Run, "owed at the run's end: steps run side by side, so what it finds "
                        + "cannot be put on one of them") { Step = step.StepId });
            else
            {
                now.Add(check);
                continue;
            }
            deferred.Add(check);
        }
        if (deferred.Count > 0)
            await publish(scope.Ev(EventKind.ContextAssembled, prefix + (step.CheckTiming == StepCheckTiming.Staged
                ? $"Step check(s) {string.Join(", ", deferred.Select(c => c.Id))} not run: {WorkspaceChecks.Staged} - they are owed when "
                  + "the run's proposals are applied, and until then they verify nothing."
                : $"Blocking check(s) {string.Join(", ", deferred.Select(c => c.Id))} owed at "
                  + (session.Waves is { } open ? $"the end of wave {open.Closed + 1}" : "the run's end")
                  + ", once the steps running beside this one have finished: steps run side by side, so what a check finds now "
                  + "could be another step's."), stepNumber));
        if (now.Count == 0) return null;

        var ran = new List<(ResolvedCheck Check, CriterionResult Result)>();
        foreach (var check in now)
        {
            var result = await RunDeclaredCheckAsync(check, at, scope, context, ct);
            ran.Add((check, result));
            await publish(scope.Criterion(result));
        }

        // What the engine ran, for the reviewer - once per attempt, the earlier attempts' marked as history: the step was
        // changed since, and their failures are not about the work as it is now.
        step.Journal.MarkHistorical(step.EvidenceStart, StepChecksTool, StepChecksHistorical);
        step.Journal.Record(stepNumber, StepChecksTool, "{}", ActionOutcome.Succeeded,
            "Run by the engine on what this step changed - the workspace checks a person declared for these paths. Each "
            + "passes on its exit code; an advisory one decides nothing on its own, and whether the step is done is yours to "
            + "judge:\n" + string.Join("\n", ran.Select(r => WorkspaceChecks.EvidenceLine(r.Check, r.Result)))
            + (step.CheckTiming == StepCheckTiming.Deferred
                ? "\nOther steps were running at the same time in the same workspace: what a check found may be theirs." : ""),
            WorkspaceEffect.None, origin: ToolCallOrigin.Engine);

        var notRun = ran.Where(r => r.Result.Required && r.Result.Outcome == CriterionOutcome.Unknown)
            .Select(r => $"the engine's check '{r.Check.Id}' did not run: {r.Result.Detail}").ToArray();
        return new(ran.FirstOrDefault(r => r.Result.Required && r.Result.Outcome == CriterionOutcome.Failed) is { Check: not null } failed ? failed : null,
            notRun.Length == 0 ? null : string.Join("; ", notRun));
    }

    /// <summary>What the workspace measurement found changed since <paramref name="before"/>, or null when nothing was measured.</summary>
    private static async Task<IReadOnlyList<string>?> MeasuredSinceAsync(IWorkspaceChanges? changes, WorkspaceSnapshot? before,
        CancellationToken ct)
    {
        if (changes is null || before is null) return null;
        try
        {
            if (await changes.TakeAsync(ct) is not { } after || await changes.ComparePathsAsync(before, after, ct) is not { } found)
                return null;
            return found.SelectMany(c => c.OldPath is { } old ? new[] { c.Path, old } : [c.Path]).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A check owed later, once per check and boundary: a check two steps both owe at a wave's end runs there once.</summary>
    private static void Owe(RunSession session, PendingCheck pending)
    {
        lock (session.PendingChecks)
            if (!session.PendingChecks.Any(p => string.Equals(p.CheckId, pending.CheckId, StringComparison.OrdinalIgnoreCase)
                                                && p.DeferredTo == pending.DeferredTo && p.Wave == pending.Wave))
                session.PendingChecks.Add(pending);
    }

    /// <summary>Whether a check is owed at the end of the wave now running - while one is, nothing new is started.</summary>
    private static bool OwesAtWave(RunSession session)
    {
        lock (session.PendingChecks)
            return session.PendingChecks.Any(p => p.DeferredTo == CheckBoundary.Wave);
    }

    /// <summary>
    /// The checks owed at the end of a wave (Plan Phase 3.2), run once its steps have all finished and before anything that
    /// builds on it starts - and put on the WAVE, never on one of its steps: they ran side by side in one workspace, and
    /// what a check found there is all of theirs until something tells them apart. A blocking one that failed stops the
    /// run as a wave the engine could not put back does (the dispatcher's boundary): nothing more is started on a
    /// workspace a blocking check failed, and what was waiting is accounted for as skipped. Every owed check is run here -
    /// one left owed would hold the dispatcher for good - and each is discharged once it has run, so a run stopped in the
    /// middle keeps the ones it did not reach. Null when the run goes on.
    /// </summary>
    internal async Task<string?> WaveChecksAsync(RunSession session, int wave, string? span, WorkContext context,
        Func<WorkEvent, ValueTask> publish, CancellationToken ct)
    {
        PendingCheck[] owed;
        lock (session.PendingChecks)
            owed = session.PendingChecks.Where(p => p.DeferredTo == CheckBoundary.Wave).ToArray();
        if (owed.Length == 0) return null;

        var at = span is null ? $"wave {wave}" : $"wave {wave}: {span}";
        var results = new List<CriterionResult>();
        var ran = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var id in owed.Select(p => p.CheckId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var result = await OwedCheckAsync(session, id, at, context, ct);
                results.Add(result);
                ran.Add(id);
                await publish(session.Scope.Criterion(result));
            }
        }
        finally
        {
            // Only what ran is discharged. A run stopped in the middle of them leaves the rest owed - a check that never
            // ran is not a pass, and an obligation dropped on the way out is one a resume could not know to run.
            lock (session.PendingChecks)
                session.PendingChecks.RemoveAll(p => owed.Contains(p) && ran.Contains(p.CheckId));
            lock (session.BoundaryChecks)
                session.BoundaryChecks.AddRange(results);
        }

        var failed = results.Where(r => r.Required && r.Outcome == CriterionOutcome.Failed).ToArray();
        return failed.Length == 0 ? null
            : $"the engine's check(s) {string.Join(", ", failed.Select(r => r.Name))} failed at the end of {at}; no step after it "
              + "runs on a workspace a blocking check failed";
    }

    /// <summary>A check owed at a boundary, run there - or, gone from the run's checks, not run and said so. Never a pass.</summary>
    private async Task<CriterionResult> OwedCheckAsync(RunSession session, string id, string at, WorkContext context, CancellationToken ct)
        => session.Checks?.Find(id) is { } check
            ? await RunDeclaredCheckAsync(check, at, session.Scope, context, ct)
            : new CriterionResult(WorkspaceChecks.Label(id, at), id, Required: true, CriterionOutcome.Unknown, null,
                "it was owed, and is not among the checks this run was started with, so it could not be run", CriterionOrigin.Declared);

    /// <summary>
    /// The declared checks at the run's end (Plan Phase 3.2): every check still owed - deferred from a step whose siblings
    /// ran beside it where no wave was kept, carried over a pause, or owed when staged files are applied - and every check
    /// that fires on the run. Returns what to say now and, for the run's outcome, that with what the waves' checks came to.
    /// A staged run runs none of them against the disk: each is not checked, with the reason, and a blocking one keeps
    /// the run from Completed.
    /// </summary>
    private async Task<(IReadOnlyList<CriterionResult> Said, IReadOnlyList<CriterionResult> Decide)> RunEndChecksAsync(
        RunSession session, WorkContext context, CancellationToken ct)
    {
        PendingCheck[] owed;
        lock (session.PendingChecks)
            owed = session.PendingChecks.ToArray();
        var said = new List<CriterionResult>();
        foreach (var group in owed.GroupBy(p => (Id: p.CheckId.ToLowerInvariant(), Staged: p.DeferredTo == CheckBoundary.Apply)))
        {
            var first = group.First();
            if (group.Key.Staged)
            {
                var check = session.Checks?.Find(first.CheckId);
                said.Add(new CriterionResult(WorkspaceChecks.Label(first.CheckId, "staged, owed when its files are applied"),
                    check is null ? first.CheckId : WorkspaceChecks.CommandOf(check, []),
                    // A pending check whose severity is not known any more is taken as blocking: an obligation is never a pass.
                    Required: check?.Definition.Severity != CheckSeverity.Advisory, CriterionOutcome.Unknown, null,
                    WorkspaceChecks.StagedDetail, CriterionOrigin.Declared));
            }
            else
                said.Add(await OwedCheckAsync(session, first.CheckId, "the run", context, ct));
        }
        foreach (var check in WorkspaceChecks.Firing(session.Checks, CheckFire.Run))
            said.Add(await RunDeclaredCheckAsync(check, "the run", session.Scope, context, ct));
        lock (session.PendingChecks)
            session.PendingChecks.RemoveAll(owed.Contains);

        CriterionResult[] atWaves;
        lock (session.BoundaryChecks)
            atWaves = session.BoundaryChecks.ToArray();
        return (said, [.. atWaves, .. said]);
    }

    /// <summary>
    /// What a step is told when a blocking declared check sent it back: the check's output, said as what it is - a command
    /// a person set to block this kind of change, not a reviewer's judgement - so the step fixes what it printed and does
    /// not argue with a verdict nobody gave. Files stay as they are between attempts, as after a review.
    /// </summary>
    private static void RetryAfterDeclaredCheck(List<Enactive.Core.Chat.ChatMessage> convo, string advice, string what)
        => convo.Add(Enactive.Core.Chat.ChatMessage.User(
            $"The engine's check did not pass the previous attempt of {what}.\n" + advice + "\n"
            + "What you already read, ran and wrote above still stands, and files you wrote are still there as you left them: "
            + "make the check pass by changing what it is about, and keep the rest."));
}

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
    /// <param name="Failed">The first blocking check that failed, with its result - the step goes back on the gate's budget -
    /// and, where it was known, how it stood when the step began, for the step to be told (StepStartCheck).</param>
    /// <param name="NotRun">Which blocking checks did not run and why - or failed before the step too, with nothing to
    /// compare - the step is then done at best, never confirmed.</param>
    /// <param name="NotRunCause">Why it is not confirmed, as a code: a check that did not run, or only ones that said
    /// nothing about the step (OutcomeCause.CheckFailedBefore).</param>
    private sealed record StepCheckVerdict((ResolvedCheck Check, CriterionResult Result, string? Before)? Failed, string? NotRun,
        Enactive.Core.Tasks.OutcomeCause NotRunCause = Enactive.Core.Tasks.OutcomeCause.CheckNotRun);

    /// <summary>
    /// Runs one declared check, labelled with where it was owed (<paramref name="at"/>). Never an exception and never a
    /// pass it cannot stand behind: a staged run's check, one the policy would ask about, one that found nothing to run or
    /// did not finish in its time are each <see cref="CriterionOutcome.Unknown"/>, with the reason.
    /// </summary>
    /// <param name="firing">Where it fires, for its record (Plan Phase 3.3): said in its event, and where its output is kept.</param>
    private async Task<CriterionResult> RunDeclaredCheckAsync(ResolvedCheck check, string at, RunScope scope,
        WorkContext context, CheckFiring firing, CancellationToken ct)
        => Recorded(await RunDeclaredCheckAsync(check, at, scope.TaskId, scope.RunId, context, ct), firing, scope.RunId);

    /// <summary>
    /// "Run checks now" on a staged run (Plan Phase 3.2), after a person has decided its proposals: the run's SNAPSHOTTED
    /// checks, each through the one runner every declared check goes through (RunDeclaredCheckAsync) - the same permission
    /// gate, the same tool, its own timeout - against the workspace as it is now. Nothing is published and nothing of the
    /// run is changed: the results are for a check record beside the run, and each one's whole output is kept in the run's
    /// folder under <paramref name="stem"/>, a name of this press's own, so a later press does not take its files.
    ///
    /// <para>The engine this is called on must be composed for it: the policy of the moment, and NOT staged - a staged
    /// engine reports every check "staged: needs the applied files", which is exactly what this is run after.</para>
    /// </summary>
    /// <param name="runId">The staged run whose checks these are - its folder keeps the output.</param>
    /// <param name="checks">The checks to run, from the run's snapshot (WorkspaceChecks.Runnable).</param>
    /// <param name="stem">The start of each detail file's name - "now-20261017-140212" - distinct per record.</param>
    public async Task<IReadOnlyList<CriterionResult>> RunChecksNowAsync(Guid runId, Guid taskId, IReadOnlyList<ResolvedCheck> checks,
        string stem, WorkContext context, CancellationToken ct)
    {
        var results = new List<CriterionResult>(checks.Count);
        foreach (var check in checks)
            results.Add(Recorded(await RunDeclaredCheckAsync(check, "run now", taskId, runId, context, ct),
                new CheckFiring(check.Id, CheckBoundary.Apply), runId, stem: stem));
        return results;
    }

    /// <summary>
    /// A declared check's result with its record (Plan Phase 3.3): where it fired, and its whole output kept in the run's
    /// folder - the event's summary keeps only the start and the end of it (SuccessEvaluator.Trim). Written here, where the
    /// result is made and before it is published, because the output is not in the event and is gone with the result.
    /// </summary>
    /// <param name="replace">Write over the last file of this check here, rather than beside it - see CheckRecords.WriteDetail.</param>
    /// <param name="stem">The detail file's name before the check's id, when not the firing's own (DetailStem) - "Run checks now" names its press.</param>
    private CriterionResult Recorded(CriterionResult result, CheckFiring firing, Guid runId, bool replace = false, string? stem = null)
        => result with
        {
            Check = firing with
            {
                DetailPath = string.IsNullOrEmpty(result.Output) ? null
                    : CheckRecords.WriteDetail(_workspace.RootPath, runId, stem ?? DetailStem(firing), firing.CheckId, result.Output!, replace)
            }
        };

    /// <summary>A detail file's name before the check's id: where it fired, and the step or wave - "step-3", "wave-1", "run".</summary>
    private static string DetailStem(CheckFiring firing) => firing.FiredOn switch
    {
        CheckBoundary.Step => firing.Step is { } step ? $"step-{step}" : "step",
        CheckBoundary.Write => firing.Step is { } step ? $"step-{step}-write" : "step-write",
        CheckBoundary.Wave => firing.Wave is { } wave ? $"wave-{wave}" : "wave",
        _ => CheckRecords.NameOf(firing.FiredOn)
    };

    /// <summary>
    /// The check run, before its record is put on it - every caller puts it on: through the overload above, or, at a step's
    /// gate, StepChecksAsync, which runs RunDeclaredCheckPartsAsync for the parts and calls Recorded itself.
    /// </summary>
    private async Task<CriterionResult> RunDeclaredCheckAsync(ResolvedCheck check, string at, Guid taskId, Guid runId,
        WorkContext context, CancellationToken ct)
        => (await RunDeclaredCheckPartsAsync(check, at, taskId, runId, context, ct)).Result;

    /// <summary>
    /// The check run, with each command it ran as and what that came to - an ecosystem check's one per target - for a step
    /// check compared with how it stood before the step (StepStartCheck). Empty when nothing ran.
    /// </summary>
    private async Task<(CriterionResult Result, IReadOnlyList<StepStartCheck.Part> Parts)> RunDeclaredCheckPartsAsync(
        ResolvedCheck check, string at, Guid taskId, Guid runId, WorkContext context, CancellationToken ct)
    {
        var definition = check.Definition;
        var name = WorkspaceChecks.Label(definition.Id, at);
        var blocking = definition.Severity == CheckSeverity.Blocking;
        var criteria = WorkspaceChecks.Criteria(check, name, _ecosystems, _workspace.RootPath, out var none, out var sources);
        var command = WorkspaceChecks.CommandOf(check, criteria);
        (CriterionResult, IReadOnlyList<StepStartCheck.Part>) Unknown(string why)
            => (new(name, command, blocking, CriterionOutcome.Unknown, null, why, CriterionOrigin.Declared), []);

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
            var report = await CheckSuccessAsync(criteria, taskId, runId, context, limit.Token,
                commandTimeoutSeconds: definition.TimeoutSeconds);
            return (WorkspaceChecks.Combine(name, command, blocking, report.Results),
                report.Results.Count == sources.Count
                    ? report.Results.Select((r, i) => new StepStartCheck.Part(sources[i].Ecosystem, sources[i].Target, sources[i].Kind, r)).ToArray()
                    : []);
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
    /// <param name="measure">What the step changed (StepChangesAsync) - measured once at the gate for these checks and the
    /// protected files both - and the workspace it was measured in, which the checks run on.</param>
    /// <param name="changes">The measurement, and <paramref name="before"/> the workspace as the step found it: how a
    /// blocking check that fails now stood then is looked up against them (StepStartCheck).</param>
    private async Task<StepCheckVerdict?> StepChecksAsync(RunSession session, StepAttemptState step, int? stepNumber,
        WorkContext context, StepMeasure measure, IWorkspaceChanges? changes, WorkspaceSnapshot? before,
        Func<WorkEvent, ValueTask> publish, CancellationToken ct)
    {
        var changed = measure.Changed;
        var owed = WorkspaceChecks.Firing(session.Checks, CheckFire.Step);
        if (owed.Count == 0) return null;
        var scope = session.Scope;
        var prefix = stepNumber is { } number ? $"[{number}] " : "";

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

        var ran = new List<(ResolvedCheck Check, CriterionResult Result, StepStartCheck.Judgement? Before)>();
        foreach (var check in now)
        {
            var (raw, parts) = await RunDeclaredCheckPartsAsync(check, at, scope.TaskId, scope.RunId, context, ct);
            var result = Recorded(raw, new CheckFiring(check.Id, CheckBoundary.Step) { Step = stepNumber, Attempt = step.LaterAttempt }, scope.RunId);
            StepStartCheck.Judgement? judged = null;
            // Only at a gate of steps one at a time: there the workspace is this step's alone, before and after. Deferred,
            // what runs here is advisory and may be a sibling's; staged, nothing runs.
            if (step.CheckTiming == StepCheckTiming.AtGate)
            {
                // Looked up only for a blocking check that failed - the one result that sends a step back - and never by
                // running the check again: how it stood before the step is taken from a result the run already has (see
                // BeforeThisStepAsync), so a check that passes costs nothing more, and one that fails costs a comparison.
                if (result.Required && result.Outcome == CriterionOutcome.Failed)
                {
                    judged = await BeforeThisStepAsync(session, check, parts, changes, before, ct);
                    result = result with { Detail = WithBefore(result.Detail, judged) };
                }
                if (measure.After is { } after && result.Outcome != CriterionOutcome.Unknown && parts.Count > 0)
                    Remember(session, new StepStartCheck.Known(check.Id, after, parts));
            }
            ran.Add((check, result, judged));
            await publish(scope.Criterion(result));
        }

        // What the engine ran, for the reviewer - once per attempt, the earlier attempts' marked as history: the step was
        // changed since, and their failures are not about the work as it is now. From the step's own start, not from where
        // its evidence starts: steps one after the other share one journal (EvidenceStart 0), and from there the second
        // step's gate rewrote the FIRST step's record as "an earlier attempt of this step" in every review after it.
        step.Journal.MarkHistorical(step.StepStart, StepChecksTool, StepChecksHistorical);
        step.Journal.Record(stepNumber, StepChecksTool, "{}", ActionOutcome.Succeeded,
            "Run by the engine on what this step changed - the workspace checks a person declared for these paths. Each "
            + "passes on its exit code; an advisory one decides nothing on its own, and whether the step is done is yours to "
            + "judge:\n" + string.Join("\n", ran.Select(r => WorkspaceChecks.EvidenceLine(r.Check, r.Result)))
            + (step.CheckTiming == StepCheckTiming.Deferred
                ? "\nOther steps were running at the same time in the same workspace: what a check found may be theirs." : ""),
            WorkspaceEffect.None, origin: ToolCallOrigin.Engine);

        var notRun = ran.Where(r => r.Result.Required && r.Result.Outcome == CriterionOutcome.Unknown)
            .Select(r => $"the engine's check '{r.Check.Id}' did not run: {r.Result.Detail}").ToArray();
        // Failed before the step too, and what it printed cannot be compared: it verified nothing about this step - not a
        // failure to send it back for, and not a check it passed. Done at best, as for a check that did not run.
        var saidNothing = ran.Where(r => r.Before?.Standing == StepStartCheck.Standing.Inconclusive)
            .Select(r => $"the engine's check '{r.Check.Id}' failed before this step too, and whether this step made it worse "
                         + $"cannot be told: {r.Before!.Note}").ToArray();
        var unconfirmed = notRun.Concat(saidNothing).ToArray();
        var failed = ran.FirstOrDefault(r => r.Result.Required && r.Result.Outcome == CriterionOutcome.Failed
                                             && r.Before?.Standing is not (StepStartCheck.Standing.Theirs or StepStartCheck.Standing.Inconclusive));
        return new(failed.Check is not null ? (failed.Check, failed.Result, ToldOfBefore(failed.Before)) : null,
            unconfirmed.Length == 0 ? null : string.Join("; ", unconfirmed),
            notRun.Length == 0 && saidNothing.Length > 0 ? Enactive.Core.Tasks.OutcomeCause.CheckFailedBefore : Enactive.Core.Tasks.OutcomeCause.CheckNotRun);
    }

    /// <summary>
    /// How a blocking step check that failed after the step stood on the workspace as the step found it (StepStartCheck),
    /// from a result the run already has: an earlier step's gate, or - for an ecosystem's build or test - the run's
    /// baseline, each while nothing the check covers has changed between where it ran and where this step began (the
    /// premise a step is owed a check on: a change to nothing it covers does not change what it says).
    ///
    /// <para><b>Why never by running it.</b> The state the step began from is gone from the disk by its gate: run there,
    /// the check would need the step's work put aside and back again, which a crash between the two would lose; run at
    /// every step's start instead, it would cost a build or a test run per step whether or not anything fails. A result
    /// the run already has costs a comparison of two measurements. Where there is none - the first step of a run with a
    /// check of its own command, a step whose start was not measured - it is not known, and today's behaviour stands.</para>
    /// </summary>
    private async Task<StepStartCheck.Judgement> BeforeThisStepAsync(RunSession session, ResolvedCheck check,
        IReadOnlyList<StepStartCheck.Part> parts, IWorkspaceChanges? changes, WorkspaceSnapshot? before, CancellationToken ct)
    {
        if (parts.Count == 0)
            return StepStartCheck.NotKnown("what it ran could not be told apart to compare");
        if (changes is null || before is null)
            return StepStartCheck.NotKnown("the workspace was not measured when the step began");

        StepStartCheck.Known[] known;
        lock (session.KnownChecks)
            known = session.KnownChecks.Where(k => string.Equals(k.CheckId, check.Id, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray();
        foreach (var earlier in known)
            if (await NothingCoveredChangedAsync(check, changes, earlier.At, before, ct))
                return StepStartCheck.Judge(parts, p => StepStartCheck.Matching(earlier.Parts, p), _workspace.RootPath);

        // The baseline is of this run's start only: one kept from before an earlier attempt describes a workspace that
        // attempt then worked in (BuildBaseline.FromAnEarlierAttempt).
        var builds = session.Builds.Where(b => !b.FromAnEarlierAttempt).ToArray();
        if (check.RunsThroughEcosystem && builds.Length > 0 && session.BaselineAt is { } start
            && await NothingCoveredChangedAsync(check, changes, start, before, ct))
            return StepStartCheck.Judge(parts, p => StepStartCheck.FromBaseline(builds, p), _workspace.RootPath);

        return StepStartCheck.NotKnown("the run has no result of it on the workspace as this step found it - it has not run "
            + "on it, and is not run again only to find out");
    }

    /// <summary>Whether no path <paramref name="check"/> covers differs between two measurements; false when they cannot be compared.</summary>
    private async Task<bool> NothingCoveredChangedAsync(ResolvedCheck check, IWorkspaceChanges changes, WorkspaceSnapshot from,
        WorkspaceSnapshot to, CancellationToken ct)
    {
        try
        {
            if (await changes.ComparePathsAsync(from, to, ct) is not { } found) return false;
            return !WorkspaceChecks.Covers(check, found.SelectMany(c => c.OldPath is { } old ? new[] { c.Path, old } : [c.Path]), _ecosystems);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A check's result at a gate, kept for the steps after it - the newest few per check (StepStartCheck.KeptPerCheck).</summary>
    private static void Remember(RunSession session, StepStartCheck.Known known)
    {
        lock (session.KnownChecks)
        {
            session.KnownChecks.Add(known);
            var same = session.KnownChecks.Where(k => string.Equals(k.CheckId, known.CheckId, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var old in same.Take(Math.Max(0, same.Length - StepStartCheck.KeptPerCheck)))
                session.KnownChecks.Remove(old);
        }
    }

    /// <summary>
    /// A failed blocking check's detail with how it stood before the step. First where it decides the step - failed before
    /// too, so it is not what sends the step back; last otherwise, after the output, which stays what a person reads first.
    /// </summary>
    private static string? WithBefore(string? detail, StepStartCheck.Judgement judged) => judged.Standing switch
    {
        StepStartCheck.Standing.Theirs => "Failed before this step too, and nothing in it is new - not this step's failure, and the "
            + $"step is not sent back for it: {judged.Note}." + (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail),
        StepStartCheck.Standing.Inconclusive => "Failed before this step too, and whether this step made it worse cannot be told - "
            + $"the step is not sent back for it, and not confirmed by it: {judged.Note}." + (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail),
        StepStartCheck.Standing.Ours => (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n") + $"Against this step's start: {judged.Note}.",
        _ => (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n") + $"Whether it failed before this step too is not known: {judged.Note}."
    };

    /// <summary>
    /// What a step sent back by a check that is its own is told of how the check stood before it: what is new, so it fixes
    /// that and not what was there before it began - the bench's chase after an old error. Nothing when it is not known.
    /// </summary>
    private static string? ToldOfBefore(StepStartCheck.Judgement? judged)
        => judged is { Standing: StepStartCheck.Standing.Ours } ours
            ? $"Against how the check stood when this step began: {ours.Note}."
              // Only where there was an old failure: a step whose check passed before it began has none to leave alone.
              + (ours.FailedBefore ? " Only what this step changed is this step's to fix; a failure that was there before it began is not." : "")
            : null;

    /// <summary>
    /// What a step changed, as its gate reads it (Plan Phase 3.2 / 3.4): the paths, for the checks owed and the protected
    /// files; and what the measurement found - each change's kind and its old path - with the snapshot it was found in,
    /// for a protected file's diff. Measured once, for both: a git status twice per step is a cost with no second answer.
    /// </summary>
    internal sealed record StepMeasure(StepChanges Changed, IReadOnlyList<FileChange>? Measured, WorkspaceSnapshot? After);

    private async Task<StepMeasure> StepChangesAsync(StepAttemptState step, IWorkspaceChanges? changes, WorkspaceSnapshot? before,
        CancellationToken ct)
    {
        var (after, found) = await MeasuredSinceAsync(changes, before, ct);
        var changed = WorkspaceChecks.Of(found?.SelectMany(c => c.OldPath is { } old ? new[] { c.Path, old } : [c.Path]),
            step.Journal.Actions.Skip(step.StepStart), step.Store.TouchedPaths, _workspace.RootPath,
            tool => _tools.DefinitionOf(tool)?.Kind == ToolKind.Command);
        return new(changed, found, after);
    }

    /// <summary>What the workspace measurement found changed since <paramref name="before"/>, or null when nothing was measured.</summary>
    private static async Task<(WorkspaceSnapshot? After, IReadOnlyList<FileChange>? Found)> MeasuredSinceAsync(IWorkspaceChanges? changes,
        WorkspaceSnapshot? before, CancellationToken ct)
    {
        if (changes is null || before is null) return (null, null);
        try
        {
            if (await changes.TakeAsync(ct) is not { } after || await changes.ComparePathsAsync(before, after, ct) is not { } found)
                return (null, null);
            return (after, found);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    /// <summary>
    /// A check owed later, once per check and boundary: a check two steps both owe at a wave's end runs there once.
    ///
    /// <para>A protected-files obligation (PendingCheck.Protected) is its own, beside the check's command: two steps that each
    /// removed a file the check protects owe one look at the boundary, for both files.</para>
    /// </summary>
    private static void Owe(RunSession session, PendingCheck pending)
    {
        lock (session.PendingChecks)
        {
            var at = session.PendingChecks.FindIndex(p => string.Equals(p.CheckId, pending.CheckId, StringComparison.OrdinalIgnoreCase)
                                                          && p.DeferredTo == pending.DeferredTo && p.Wave == pending.Wave
                                                          && (p.Protected is null) == (pending.Protected is null));
            if (at < 0)
                session.PendingChecks.Add(pending);
            else if (pending.Protected is { } more && session.PendingChecks[at].Protected is { } already)
                session.PendingChecks[at] = session.PendingChecks[at] with
                {
                    Protected = already.Union(more, StringComparer.OrdinalIgnoreCase).ToArray()
                };
        }
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
        var ran = new HashSet<PendingCheck>();
        try
        {
            // A check's command once, however many steps owed it; its protected files once, for every file they removed.
            foreach (var group in owed.GroupBy(p => (Id: p.CheckId.ToLowerInvariant(), Protected: p.Protected is not null)))
            {
                var first = group.First();
                var firing = new CheckFiring(first.CheckId, CheckBoundary.Wave) { Wave = wave, Deferred = first.Reason };
                var result = group.Key.Protected
                    ? ProtectedOwed(session, first.CheckId, group.SelectMany(p => p.Protected!), at, firing)
                    : await OwedCheckAsync(session, first.CheckId, at, context, firing, ct);
                results.Add(result);
                ran.UnionWith(group);
                await publish(session.Scope.Criterion(result));
            }
        }
        finally
        {
            // Only what ran is discharged. A run stopped in the middle of them leaves the rest owed - a check that never
            // ran is not a pass, and an obligation dropped on the way out is one a resume could not know to run.
            lock (session.PendingChecks)
                session.PendingChecks.RemoveAll(ran.Contains);
            lock (session.BoundaryChecks)
                session.BoundaryChecks.AddRange(results);
        }

        var failed = results.Where(r => r.Required && r.Outcome == CriterionOutcome.Failed).ToArray();
        return failed.Length == 0 ? null
            : $"the engine's check(s) {string.Join(", ", failed.Select(r => r.Name))} failed at the end of {at}; no step after it "
              + "runs on a workspace a blocking check failed";
    }

    /// <summary>A check owed at a boundary, run there - or, gone from the run's checks, not run and said so. Never a pass.</summary>
    private async Task<CriterionResult> OwedCheckAsync(RunSession session, string id, string at, WorkContext context,
        CheckFiring firing, CancellationToken ct)
        => session.Checks?.Find(id) is { } check
            ? await RunDeclaredCheckAsync(check, at, session.Scope, context, firing, ct)
            : new CriterionResult(WorkspaceChecks.Label(id, at), id, Required: true, CriterionOutcome.Unknown, null,
                "it was owed, and is not among the checks this run was started with, so it could not be run", CriterionOrigin.Declared)
                { Check = firing };

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
        foreach (var group in owed.GroupBy(p => (Id: p.CheckId.ToLowerInvariant(), Staged: p.DeferredTo == CheckBoundary.Apply,
                     Protected: p.Protected is not null)))
        {
            var first = group.First();
            if (group.Key.Staged)
            {
                var check = session.Checks?.Find(first.CheckId);
                const string applied = "staged, owed when its files are applied";
                said.Add(new CriterionResult(group.Key.Protected ? ProtectedLabel(first.CheckId, applied) : WorkspaceChecks.Label(first.CheckId, applied),
                    group.Key.Protected ? ProtectedCommand(first.CheckId, group.SelectMany(p => p.Protected!))
                        : check is null ? first.CheckId : WorkspaceChecks.CommandOf(check, []),
                    // A pending check whose severity is not known any more is taken as blocking: an obligation is never a pass.
                    // A removed protected file is blocking whatever the check's severity (Plan Phase 3.4).
                    Required: group.Key.Protected || check?.Definition.Severity != CheckSeverity.Advisory, CriterionOutcome.Unknown, null,
                    WorkspaceChecks.StagedDetail, CriterionOrigin.Declared)
                    { Check = new CheckFiring(first.CheckId, CheckBoundary.Apply) { Deferred = first.Reason } });
            }
            else
            {
                var firing = new CheckFiring(first.CheckId, CheckBoundary.Run) { Deferred = first.Reason };
                said.Add(group.Key.Protected
                    ? ProtectedOwed(session, first.CheckId, group.SelectMany(p => p.Protected!), "the run", firing)
                    : await OwedCheckAsync(session, first.CheckId, "the run", context, firing, ct));
            }
        }
        foreach (var check in WorkspaceChecks.Firing(session.Checks, CheckFire.Run))
            said.Add(await RunDeclaredCheckAsync(check, "the run", session.Scope, context, new CheckFiring(check.Id, CheckBoundary.Run), ct));
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

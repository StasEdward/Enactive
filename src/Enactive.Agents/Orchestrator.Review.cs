namespace Enactive.Agents;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
// The pure parsing/formatting members moved to ToolCallParsing. Imported
// statically so every call site here reads exactly as it did before the move: a refactor cannot be
// verified differentially, so the less of it is visible at the call sites, the better.
using static Enactive.Agents.ToolCallParsing;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

public sealed partial class Orchestrator
{
    /// <summary>The name the engine's own measurement before the work is recorded under - no tool the model can call.</summary>
    internal const string MeasuredBeforeTool = "engine_measured_before_the_work";

    /// <summary>The engine's record, for the review, of the tools this run kept back from the step.</summary>
    internal const string KeptBackTool = "engine_kept_back_tools";

    /// <summary>The engine's record, for the review, of a step's own report that it cannot go on.</summary>
    internal const string SaidBlockedTool = "engine_step_said_blocked";

    /// <param name="ByGate">The verdict is the engine's coverage check's (StepCoverageGate), not a reviewer's: it is
    /// retried on the gate's own budget.</param>
    /// <param name="Coverage">What the gate found for the attempt the review judged, when it passed - its exclusions are
    /// named if no verdict comes back to permit them.</param>
    /// <param name="NotAskedAboutExclusions">Why the review could not judge the items the step left out - null when it
    /// was asked about every one, or there were none. A pass from such a review does not approve them.</param>
    private sealed record AttemptReview(ReviewResult Review, bool ByGate = false, StepCoverageGate.Outcome? Coverage = null,
        string? NotAskedAboutExclusions = null)
    {
        /// <summary>The declared check whose failure the gate's verdict is (Plan Phase 3.2); null for the coverage check's.</summary>
        public string? ByCheck { get; init; }

        /// <summary>Which blocking declared checks did not run at the gate, and why: a pass then makes the step done, not confirmed.</summary>
        public string? ChecksNotRun { get; init; }

        /// <summary>Why <see cref="ChecksNotRun"/> leaves the step unconfirmed, as a code - see StepCheckVerdict.NotRunCause.</summary>
        public Enactive.Core.Tasks.OutcomeCause ChecksNotRunCause { get; init; } = Enactive.Core.Tasks.OutcomeCause.CheckNotRun;
    }

    /// <summary>
    /// Shared review/proof phase of an attempt. Callers own retries, rollback, checkpoints and
    /// transcript lifetime, and explicitly supply both evidence and current-step boundaries.
    /// </summary>
    private async Task<AttemptReview> ReviewAttemptAsync(
        string title, List<ChatMessage> messages, ExecutionJournal journal, int evidenceStart, int stepStart,
        IArtifactScope store, RunScope scope, RunModels models, int? stepNumber,
        IWorkspaceChanges? changes, WorkspaceSnapshot? before, string request,
        Func<WorkEvent, ValueTask> publish, CancellationToken ct, IReadOnlyList<string>? planSteps = null,
        RequestObligations? obligations = null, string? handedOn = null,
        IReadOnlyList<SuccessCriterionDefinition>? stepCriteria = null, System.Text.Json.Nodes.JsonObject? handedValues = null,
        IReadOnlyList<BuildBaseline>? measuredBefore = null, ReadLedger? reads = null,
        IReadOnlyList<TaskRestriction>? restrictions = null, string? keptBack = null, string? saidBlocked = null,
        StepCoverageGate.Outcome? coverage = null)
    {
        var prefix = stepNumber is { } number ? $"[{number}] " : "";
        ValueTask Emit(EventKind kind, string summary) => publish(scope.Ev(kind, prefix + summary, stepNumber));
        async ValueTask Usage(int prompt, int completion, int? cached, int? created)
        {
            if (prompt + completion > 0)
                await publish(scope.Usage(WorkEventPayload.WorkPurpose.Review, models.Review!,
                    prompt, completion, stepNumber, cached, created));
        }

        if (scope.Budget.TurnExhausted is { } beforeReview)
            return new(new ReviewResult(new ReviewVerdict.OutOfBudget(beforeReview)));
        await Emit(EventKind.ReviewRequested, stepNumber is null ? "reviewing…" : "reviewing with reasoner…");

        // The places the report and the handed-on result cite, opened by the engine now and recorded as its
        // own observations, so the reviewer judges a claim about "Program.cs:223" against line 223 and not
        // against whatever part of the file the step happened to read and the evidence happened to keep.
        //
        // Where the step hands on a result, that result is what is checked, at every attempt: its closing message is its
        // account of the work - after a correction, "replaced Program.cs:113 by the full path", whose old place is not a
        // claim any more - and whether this is a correction is not something the places found so far can tell (a first
        // result that cited nothing leaves none). What the engine found for an earlier review is history, and said to be.
        // Run f45e14, 2026-09-29: the second review was shown the first review's "NOT in" beside the new ones, and the old
        // place again from the account of the fix, and read them all as the current citations.
        //
        // From the step's own start (stepStart), not where its evidence starts: steps one after the other share one journal
        // (evidenceStart 0), and from the second step's review on, every earlier step's cited places were rewritten as "an
        // earlier attempt of this step, since corrected" - the original output lost, the reviewer told something false.
        journal.MarkHistorical(stepStart,CitedPlaces.ToolName, CitedPlaces.Historical);
        var cited = CitedPlaces.Observe(handedOn ?? LastAssistant(messages), _workspace.RootPath,
            () => CitedPlaces.Sweep(_workspace.RootPath));
        foreach (var (place, observed) in cited)
            journal.Record(stepNumber, CitedPlaces.ToolName, JsonSerializer.Serialize(new { cited = place }),
                ActionOutcome.Succeeded, observed, WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // What the ENGINE measured before any work: the build and the tests, run by it and read by it. A fact for the
        // reviewer, once per step. Run 148e77, 2026-09-29: the engine's own test run said 129 passed; the step said
        // "129 tests", its breakdown by file counted 103 test methods, and the reviewer - never shown the engine's
        // number - failed the step for an arithmetic error that was a theory's cases.
        if (measuredBefore is { Count: > 0 } && measuredBefore.Where(b => b.Taken).ToArray() is { Length: > 0 } taken
            && !journal.Actions.Skip(evidenceStart).Any(a => a.Tool == MeasuredBeforeTool))
            journal.Record(stepNumber, MeasuredBeforeTool, "{}", ActionOutcome.Succeeded,
                (taken.FirstOrDefault(b => b.FromAnEarlierAttempt) is { } earlier
                    ? $"Measured by the engine itself, {BuildBaseline.EarlierAttemptNote(earlier.TakenAt)} - the step's own calls show "
                      + "what it found. Its own runs, not the step's claims; they say where things stood BEFORE any attempt, not after it:\n"
                    : "Measured by the engine itself before any work in this run - its own runs, not the step's claims. They say where "
                      + "things stood BEFORE the work, not after it:\n")
                + string.Join("\n", taken.Select(b => "- " + b.Describe())),
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // What the ENGINE kept back from the step: a fact for the reviewer, once per step. The step cannot call a
        // tool it was never shown, so no call shows it trying - and a review never told that read "I could not run
        // it" as a claim nothing supports and failed an honest report twice (run 9384e2, 2026-10-03).
        if (keptBack is not null && !journal.Actions.Skip(evidenceStart).Any(a => a.Tool == KeptBackTool))
            journal.Record(stepNumber, KeptBackTool, "{}", ActionOutcome.Succeeded,
                $"Kept back from this step by the engine, before any work: {keptBack}. The step could not have called them: "
                + "where it says it could not use one, that is so, and no call would show an attempt. Whether what it did "
                + "without them is what the step is for is yours to judge.",
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // The step's own report that it cannot go on, with nothing the engine found behind it (SaidBlockedOnly): what the
        // review is asked, said where it reads the calls, once per step.
        if (saidBlocked is not null && !journal.Actions.Skip(evidenceStart).Any(a => a.Tool == SaidBlockedTool))
            journal.Record(stepNumber, SaidBlockedTool, "{}", ActionOutcome.Succeeded,
                $"The step reported that it cannot go on: {saidBlocked}. Nothing the engine measured stops it. Judge it as any "
                + "step: whether THIS step's own work is done, by its calls. Pass it if it is - its report then stands as a note, "
                + "and the steps after it go on. Fail it if it is not, and it ends blocked as it reported.",
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // What the plan checks this step on, decided by the engine now - a fact for the reviewer, not a judgement.
        if (stepCriteria is { Count: > 0 } && stepNumber is { } planNo
            && TypedCriteria.OfStep(stepCriteria, planNo - 1, _workspace.RootPath) is { Count: > 0 } checkedNow)
            journal.Record(stepNumber, "engine_checked_step_criteria", "{}", ActionOutcome.Succeeded,
                "Checked by the engine when this step was reviewed - the criteria the plan attached to this step:\n"
                + string.Join("\n", checkedNow.Select(r => $"- {r.Name}: {(r.Outcome == CriterionOutcome.Passed ? "PASS" : r.Outcome == CriterionOutcome.Failed ? "FAIL" : "NOT CHECKED")}"
                    + (string.IsNullOrWhiteSpace(r.Detail) ? "" : $" - {r.Detail}"))),
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // What the step did for each item of the lists it consumes, as the gate found it (StepCoverageGate): a fact for the
        // reviewer, item by item - the weakly backed ones in full - and not a judgement: the engine counted that evidence
        // is there, the reviewer judges the work. A section of the reviewer's input with room of its own, taken out of the
        // evidence budget: recorded as a call it was given a call's share and cut in the middle once a dozen reads filled
        // the budget (StepCoverageGate.SectionHeading). The two together keep to the budget: the account gets half of it at
        // most, and never so much that the calls are left less than the journal's minimum - below that their view cannot
        // hold its own header, and a review without the calls cannot cite anything. So it is the account that gives way:
        // under a budget too small for both (one set near the minimum) it is shortened, and left out when what is left
        // could not hold more than its opening lines - the gate has already decided the count; the reviewer loses only
        // the per-item detail, never the calls. Made for this attempt only, so no earlier account goes with it.
        var accountRoom = Math.Min(_evidenceBudget / 2, _evidenceBudget - ExecutionJournal.MinimumBudget);
        var itemAccount = coverage is null || accountRoom < StepCoverageGate.MinimumAccountChars ? null
            : StepCoverageGate.ReviewerBlock(coverage, journal.Actions.Skip(stepStart).ToArray(), accountRoom);
        var callBudget = _evidenceBudget - (itemAccount?.Length ?? 0);
        // The file this step handed on as the result a criterion checks: the engine decides that it is there,
        // the reviewer decides whether it is what was asked - so it is shown whole, as it is now, even when the
        // step wrote none of it (run 68f92f: an earlier run's report, handed on as this run's).
        if (stepCriteria is { Count: > 0 } && stepNumber is { } handingNo && handedValues is not null)
            foreach (var field in stepCriteria.Where(c => c.Typed?.PathFromStep == handingNo - 1)
                         .Select(c => c.Typed!.PathFromField!).Distinct(StringComparer.Ordinal))
                if (handedValues[field] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var handedPath)
                    && !string.IsNullOrWhiteSpace(handedPath))
                    journal.Record(stepNumber, "engine_opened_handed_file", JsonSerializer.Serialize(new { field, path = handedPath }),
                        ActionOutcome.Succeeded, TypedCriteria.ShowHanded(handedPath, field, _workspace.RootPath),
                        WorkspaceEffect.None, origin: ToolCallOrigin.Engine);

        if (cited.Count > 0)
            await Emit(EventKind.ContextAssembled,
                $"Opened {cited.Count} place(s) the step's report and result cite, for the review: {string.Join(", ", cited.Select(c => c.Cited))}");
        // Phase 1.4: a step whose plan set semantic criteria is judged against those, and only those - its report is
        // a claim, and each verdict stands on evidence of a kind the criterion allows. A step without them is reviewed
        // as it always was.
        var (review, mode) = _options.SemanticCriteria && stepNumber is { } judgedNo
            && stepCriteria?.Where(c => c.Typed?.Kind == TypedCriterionKind.Semantic).ToArray() is { Length: > 0 } judged
            ? (await CriteriaReview.RunAsync(
                    new CriteriaReviewInput(title, judgedNo, LastAssistant(messages), handedOn,
                        await StepFilesNowAsync(changes, before, journal, stepStart, store, ct),
                        journal.Describe(evidenceStart, callBudget),
                        obligations ?? RequestObligations.Create(request, title, stepNumber, planSteps), itemAccount),
                    judged, (action, kind) => Admits(action, kind, reads, judgedNo), models.ReviewProvider!, models.ReviewModel,
                    scope.Budget.TurnExhaustedAfter, ct),
                ReviewMode.Criteria)
            // The user's model (2026-09-29): one short verdict per step - done, and its report true? - and the task is
            // done when every step is.
            : (await StepVerdictReview.RunAsync(
                        new StepVerdictInput(request, title, stepNumber,
                            planSteps?.Select((t, i) => (t, i)).Where(p => stepNumber is not { } n || p.i != n - 1)
                                .Select(p => $"{p.i + 1}. {p.t}").ToArray() ?? [],
                            LastAssistant(messages), handedOn,
                            await StepFilesNowAsync(changes, before, journal, stepStart, store, ct),
                            journal.Describe(evidenceStart, callBudget), obligations?.ScopeNote,
                            obligations?.Owned().Select(o => $"{o.Unit.Id}: {o.Unit.Text.Trim()}"
                                + (o.AlsoTo.Count > 0 ? $" (also given to step {string.Join(", ", o.AlsoTo.Select(scope => scope.TrimStart('S')))})" : "")).ToArray(),
                            restrictions, itemAccount),
                        models.ReviewProvider!, models.ReviewModel, scope.Budget.TurnExhaustedAfter, ct),
                    ReviewMode.Step);
        await Usage(review.Usage.Prompt, review.Usage.Completion, review.Usage.Cached, review.Usage.Created);
        // A verdict, said; a missing one is said by the attempt, with what it makes of the step.
        switch (review.Verdict)
        {
            case ReviewVerdict.Fail fail:
                await Emit(EventKind.ReviewFailed, $"FAIL ({mode} review): {fail.Notes}");
                break;
            case ReviewVerdict.Pass pass:
                await Emit(EventKind.ReviewPassed,
                    $"PASS ({mode} review){(string.IsNullOrEmpty(pass.Notes) ? "" : ": " + pass.Notes)}");
                break;
            case ReviewVerdict.Unreachable unreachable:
                await Emit(EventKind.ReviewFailed, $"NOT REACHABLE within the request ({mode} review): {unreachable.Notes}");
                break;
        }
        // Whether the review could judge what a filtering step left out: only the step review, and only when the account
        // put every exclusion to it in full under its question. A criteria review judges its criteria and nothing else,
        // and an account left out for room (or cut short) asked nothing - a pass from either does not approve an omission.
        // It does approve items with only a mention behind them: those the engine counted from calls the review read, and
        // an omission is the step's word, which needs a review asked about it (StepCoverageGate.Approval says it in full).
        string? notAsked = coverage?.Exclusions is not { Count: > 0 } left ? null
            : mode == ReviewMode.Criteria ? "the step was judged on its semantic criteria alone, which do not ask about them"
            : !StepCoverageGate.AsksAboutEvery(left, itemAccount) ? "the review that passed it was not shown them - the evidence budget left no room for them"
            : null;
        return new(review) { NotAskedAboutExclusions = notAsked };
    }

    /// <summary>
    /// Whether a recorded call is evidence of a kind (Phase 5.2): a read that covered its file WHOLE (1.3) or the
    /// engine's own observation, a command that ran, a call that succeeded. No tool is named: what a call is comes
    /// from its definition and its record.
    /// </summary>
    /// <param name="step">The step under review: only its own calls are write evidence for it.</param>
    private bool Admits(ExecutedAction action, EvidenceKind kind, ReadLedger? reads, int? step) => kind switch
    {
        EvidenceKind.Command => action.ExitCode is not null && action.Outcome != ActionOutcome.Refused,
        EvidenceKind.Call => action.Outcome == ActionOutcome.Succeeded,
        EvidenceKind.FileRead => action.Origin == ToolCallOrigin.Engine
            || (action.Outcome == ActionOutcome.Succeeded
                && _tools.DefinitionOf(action.Tool)?.FileCoverage == FileCoverageBehavior.Read
                && PathsIn(action.Arguments).Any(p => reads?.SeenWhole(p).Whole == true)),
        // A call that succeeded and recorded which paths it changed: a write, an edit, a deletion, a rename. A command
        // does not record its writes by file, so it is command evidence, never write evidence. And a call of THIS step:
        // with one conversation for the whole plan the review is shown the earlier steps' calls too, and an earlier
        // step's write is not the work of the step under review (as EvidenceCoverage.Gather counts only the step's own).
        EvidenceKind.FileWrite => action.Outcome == ActionOutcome.Succeeded && action.ChangedPaths is { Count: > 0 }
                                  && (step is null || action.Step == step),
        _ => false
    };

    private static IEnumerable<string> PathsIn(string arguments)
    {
        var found = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return found;
            if (doc.RootElement.TryGetProperty("path", out var one) && one.ValueKind == JsonValueKind.String) found.Add(one.GetString()!);
            if (doc.RootElement.TryGetProperty("paths", out var many) && many.ValueKind == JsonValueKind.Array)
                found.AddRange(many.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
        }
        catch (JsonException) { }
        return found;
    }

    private const int CriteriaFileChars = 16_000;

    /// <summary>
    /// What this step changed, for the short review and the criteria review: each file as a diff with the file as it is
    /// now where it fits, a new file whole, a deletion as a deletion - and who changed it, from the journal. The paths
    /// alone read back as they are now dropped a deleted file without a word and showed a one-line edit to a long file as
    /// its head and tail (code review of the move to one short review, 2026-09-30); the earlier review was shown all of it.
    /// </summary>
    private async Task<IReadOnlyList<ShownFile>> StepFilesNowAsync(IWorkspaceChanges? changes,
        WorkspaceSnapshot? before, ExecutionJournal journal, int stepStart, IArtifactScope store, CancellationToken ct)
    {
        var files = new List<ShownFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shared = store.SharedWithAnotherStep;
        string Shared(string path) => shared.Any(s => string.Equals(s.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase))
            ? " Another step running at the same time also wrote it: what is shown is the two together." : "";
        static bool Scratch(string path) => path.StartsWith(WorkspaceGuard.ScratchPrefix + "/", StringComparison.OrdinalIgnoreCase);
        (string Text, bool Whole)? Now(string? text)
            => text is null ? null : text.Length <= CriteriaFileChars ? (text, true) : (Shortening.HeadAndTail(text, CriteriaFileChars), false);

        var compared = false;
        IReadOnlySet<string>? measured = null;
        if (changes is not null && before is not null
            && await changes.TakeAsync(ct) is { } after && await changes.CompareAsync(before, after, ct) is { } found)
        {
            compared = true;
            measured = await changes.PathsAsync(after, ct);
            var actions = journal.Actions.Skip(stepStart).ToArray();
            foreach (var change in found)
            {
                var path = change.Path.Replace('\\', '/');
                if (Scratch(path) || !seen.Add(path)) continue;
                var by = ChangeAuthorship.By(ChangeAuthorship.Of(change.Path, change.OldPath, actions));
                var renamed = change.Kind == FileChangeKind.Renamed ? $"RENAMED from {change.OldPath}, and " : "";
                if (change.Kind == FileChangeKind.Deleted)
                {
                    files.Add(new(path, "(it is not there now)", false, $"DELETED{by}.{Shared(path)}"));
                    continue;
                }
                if (change.Binary)
                {
                    files.Add(new(path, "(a binary file - its contents are not shown)", false, $"{renamed}CHANGED{by} (binary).{Shared(path)}"));
                    continue;
                }
                var now = Now(await ReadOrNullAsync(path, ct));
                if (change.Kind == FileChangeKind.Added)
                    files.Add(new(path, now?.Text ?? "(could not be read back)", now?.Whole == true, $"NEW FILE, created{by}.{Shared(path)}"));
                else if (change.Diff is { } diff)
                {
                    var hunks = Hunks(diff);
                    if (hunks.Length > CriteriaFileChars) hunks = Shortening.HeadAndTail(hunks, CriteriaFileChars);
                    files.Add(now is { Whole: true } whole
                        ? new(path, hunks + "\n--- as it is now, whole:\n" + whole.Text, true,
                            $"{renamed}CHANGED{by} - a unified diff ('+' added, '-' removed, ' ' unchanged), then the file as it is now.{Shared(path)}")
                        : new(path, hunks, false,
                            $"{renamed}CHANGED{by} - a unified diff ('+' added, '-' removed, ' ' unchanged); the file is too long to show whole as well.{Shared(path)}"));
                }
                else
                    files.Add(new(path, now?.Text ?? "(could not be read back)", now?.Whole == true,
                        $"{renamed}CHANGED{by}. There is no record of how it was before (not a git repository), so this is how it is now.{Shared(path)}"));
            }
        }
        // What the step wrote where the comparison does not look - a file git ignores - or with no comparison at all.
        foreach (var path in store.TouchedPaths.Select(p => p.Replace('\\', '/')))
        {
            if (Scratch(path) || !seen.Add(path)) continue;
            string? pending = null, text;
            try { text = (pending = await store.TryReadPendingAsync(path, ct)) ?? await ReadOrNullAsync(path, ct); }   // a staged run holds it in memory
            catch (Exception ex) when (ex is not OperationCanceledException) { text = null; }
            // Measured, and no change in it: the step wrote it and it is as it was. Benchmark build-error, 2026-09-30: a step
            // put back a file the request said to leave alone; shown it as "written where the comparison does not look",
            // the review took it for a change left in it and called the true report false.
            if (compared && pending is null && measured?.Contains(path) == true && Now(text) is { } asItWas)
            {
                files.Add(new(path, asItWas.Text, asItWas.Whole,
                    $"WRITTEN by this step, and now just as it was before the step began.{Shared(path)}"));
                continue;
            }
            // Run 16d57849: a report the step wrote into a folder git ignores was never shown to the review at all.
            var where = compared && text is not null ? " where the workspace comparison does not look (a file git ignores, or a folder it skips)" : "";
            files.Add(Now(text) is { } now
                ? new(path, now.Text, now.Whole, $"WRITTEN by this step{where} - how it is now.{Shared(path)}")
                : new(path, "(it is not there now, or could not be read back)", false, $"WRITTEN by this step{where}.{Shared(path)}"));
        }
        return files;
    }


}

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

    private sealed record AttemptReview(ReviewResult Review, string? BudgetExhausted = null);

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
        IReadOnlyList<TaskRestriction>? restrictions = null)
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
            return new(new ReviewResult(false, beforeReview), BudgetExhausted: beforeReview);
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
        journal.MarkHistorical(evidenceStart, CitedPlaces.ToolName, CitedPlaces.Historical);
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
                "Measured by the engine itself before any work in this run - its own runs, not the step's claims. They say where "
                + "things stood BEFORE the work, not after it:\n" + string.Join("\n", taken.Select(b => "- " + b.Describe())),
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // What the plan checks this step on, decided by the engine now - a fact for the reviewer, not a judgement.
        if (stepCriteria is { Count: > 0 } && stepNumber is { } planNo
            && TypedCriteria.OfStep(stepCriteria, planNo - 1, _workspace.RootPath) is { Count: > 0 } checkedNow)
            journal.Record(stepNumber, "engine_checked_step_criteria", "{}", ActionOutcome.Succeeded,
                "Checked by the engine when this step was reviewed - the criteria the plan attached to this step:\n"
                + string.Join("\n", checkedNow.Select(r => $"- {r.Name}: {(r.Outcome == CriterionOutcome.Passed ? "PASS" : r.Outcome == CriterionOutcome.Failed ? "FAIL" : "NOT CHECKED")}"
                    + (string.IsNullOrWhiteSpace(r.Detail) ? "" : $" - {r.Detail}"))),
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
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
        var (review, mode) = _semanticCriteria && stepNumber is { } judgedNo
            && stepCriteria?.Where(c => c.Typed?.Kind == TypedCriterionKind.Semantic).ToArray() is { Length: > 0 } judged
            ? (await CriteriaReview.RunAsync(
                    new CriteriaReviewInput(title, judgedNo, LastAssistant(messages), handedOn,
                        await StepFilesNowAsync(changes, before, journal, stepStart, store, ct),
                        journal.Describe(evidenceStart, _evidenceBudget),
                        obligations ?? RequestObligations.Create(request, title, stepNumber, planSteps)),
                    judged, (action, kind) => Admits(action, kind, reads), models.ReviewProvider!, models.ReviewModel,
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
                            journal.Describe(evidenceStart, _evidenceBudget), obligations?.ScopeNote,
                            obligations?.Owned().Select(o => $"{o.Unit.Id}: {o.Unit.Text.Trim()}"
                                + (o.AlsoTo.Count > 0 ? $" (also given to step {string.Join(", ", o.AlsoTo.Select(scope => scope.TrimStart('S')))})" : "")).ToArray(),
                            restrictions),
                        models.ReviewProvider!, models.ReviewModel, scope.Budget.TurnExhaustedAfter, ct, _checkDerivedFigures),
                    ReviewMode.Step);
        await Usage(review.PromptTokens, review.CompletionTokens, review.CachedPromptTokens, review.CacheCreationPromptTokens);
        if (review.BudgetExhausted is { } reviewSpent)
            return new(review, BudgetExhausted: reviewSpent);
        if (review.IncompleteReason is not null) return new(review);
        if (!review.Pass)
        {
            await Emit(EventKind.ReviewFailed, $"FAIL ({mode} review): {review.Notes}");
            return new(review);
        }

        await Emit(EventKind.ReviewPassed,
            $"PASS ({mode} review){(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}");
        return new(review);
    }

    /// <summary>
    /// Whether a recorded call is evidence of a kind (Phase 5.2): a read that covered its file WHOLE (1.3) or the
    /// engine's own observation, a command that ran, a call that succeeded. No tool is named: what a call is comes
    /// from its definition and its record.
    /// </summary>
    private bool Admits(ExecutedAction action, EvidenceKind kind, ReadLedger? reads) => kind switch
    {
        EvidenceKind.Command => action.ExitCode is not null && action.Outcome != ActionOutcome.Refused,
        EvidenceKind.Call => action.Outcome == ActionOutcome.Succeeded,
        EvidenceKind.FileRead => action.Origin == ToolCallOrigin.Engine
            || (action.Outcome == ActionOutcome.Succeeded
                && _tools.DefinitionOf(action.Tool)?.FileCoverage == FileCoverageBehavior.Read
                && PathsIn(action.Arguments).Any(p => reads?.SeenWhole(p).Whole == true)),
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

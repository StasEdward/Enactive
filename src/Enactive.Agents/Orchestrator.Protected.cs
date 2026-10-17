namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// The files a person protected with a check's <c>protects</c> globs (Plan Phase 3.4), at a step's gate. A step that removed
/// or renamed one is sent back on the gate's budget - no reviewer is asked: losing a test is not a judgement call, and a
/// reviewer shown a step that deleted the failing test reads a green run. One it edited is shown to the reviewer, with its
/// diff, as a protected file: an edit to a test may be exactly what the request asked for, and only a reading of the request
/// can say. A step the planner marked as one the request asks to change protected files is only shown what it removed.
/// </summary>
public sealed partial class Orchestrator
{
    /// <summary>The engine's record, for the review, of the protected files a step changed.</summary>
    internal const string ProtectedFilesTool = "engine_protected_files";

    /// <summary>
    /// How much of an edited protected file's diff the reviewer is shown, in characters: its start and its end, with the cut
    /// said. A step that rewrote a test file whole has a diff as long as the file, and shown whole it would crowd the rest of
    /// the step's evidence out of the review - the reviewer is shown the step's own changes besides, at their own limit.
    /// </summary>
    internal const int ProtectedDiffChars = 3000;

    private const string ProtectedHistorical = "HISTORICAL - the protected files were looked at on an earlier attempt of this step, "
        + "which has since been changed; what they are now is recorded below.";

    /// <summary>A protected file the step took from where the run found it, at the gate: the step goes back with this.</summary>
    /// <param name="CheckId">The check that protects the (first) file - the step is "rejected by the engine's check" of it.</param>
    private sealed record ProtectedVerdict(string CheckId, string Notes, string Advice);

    /// <summary>A protected-files result's name: the check, and where it was looked at - beside the check's own command's.</summary>
    internal static string ProtectedLabel(string id, string at) => $"{id}: protected files ({at})";

    /// <summary>What a protected-files result looked at, where a check's result says its command.</summary>
    internal static string ProtectedCommand(string id, IEnumerable<string> paths)
        => $"the files check '{id}' protects: {string.Join(", ", paths.Distinct(StringComparer.OrdinalIgnoreCase))}";

    /// <summary>What a step that removed a protected file is told - the plan's words.</summary>
    internal static string ProtectedAdvice(string path, string id)
        => $"{path} is protected by check '{id}'; restore it or say why the request requires removing it";

    /// <summary>One protected file, for the reviewer: what became of it and - edited - its diff, cut to <see cref="ProtectedDiffChars"/>.</summary>
    internal static string ProtectedLine(string path, string id, string what, string? diff)
        => $"- {path} (protected by check '{id}'): {what}"
           + (string.IsNullOrWhiteSpace(diff) ? "" : "\n" + Shortening.HeadAndTail(diff.TrimEnd(), ProtectedDiffChars, Shortening.FileHead));

    /// <summary>
    /// The protected files at a step's gate. Which files are looked at: the ones among what the step changed (the same
    /// account the step checks use - measured, recorded by its calls, held by its store); where what it changed is not
    /// known - it ran commands nothing measured - every protected file, as every step check is owed then: unknown
    /// attribution must not mean "touched none". What is found is decided against the hashes the run's baseline took, and
    /// the disk now: not there is removed (or renamed), different is edited.
    ///
    /// <para>Removed, the finding is the gate's - but only where what is on disk is this step's (StepCheckTiming), as for a
    /// blocking check, by the same decision: steps side by side put it off to the wave's end (or the run's), and a staged
    /// run - whose removals are proposals, not gone from the disk - to when its files are applied.</para>
    /// </summary>
    private async Task<ProtectedVerdict?> ProtectedFilesAsync(RunSession session, StepAttemptState step, int? stepNumber,
        StepMeasure measure, IWorkspaceChanges? changes, WorkspaceSnapshot? before, Func<WorkEvent, ValueTask> publish,
        CancellationToken ct)
    {
        if (session.Protected.Count == 0) return null;
        var root = _workspace.RootPath;
        var scope = session.Scope;
        var unmeasured = measure.Changed.Unmeasured is not null;
        HashSet<string> released;
        lock (session.ProtectedReleased) released = new(session.ProtectedReleased, StringComparer.OrdinalIgnoreCase);
        var candidates = session.Protected
            .Where(p => unmeasured ? !released.Contains(p.Path) : measure.Changed.Paths.Contains(p.Path))
            .ToArray();
        if (candidates.Length == 0) return null;

        var removed = new List<(ProtectedFile File, string? RenamedTo)>();
        var edited = new List<ProtectedFile>();
        if (step.CheckTiming == StepCheckTiming.Staged)
        {
            // A staged step's writes are proposals: the disk still has every file. What it would remove is what its own calls
            // proposed to delete - an edit cannot be told from the disk, and is not said.
            var proposed = step.Journal.Actions.Skip(step.StepStart)
                .Where(a => a.Origin != ToolCallOrigin.Engine && a.Outcome != ActionOutcome.Refused && a.FileDeletion)
                .SelectMany(a => a.ChangedPaths ?? [])
                .Select(p => WaveCapture.Relative(p, root)).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            removed.AddRange(candidates.Where(c => proposed.Contains(c.Path)).Select(c => (c, (string?)null)));
        }
        else
            foreach (var file in candidates)
            {
                var full = Path.Combine(root, file.Path);
                if (!File.Exists(full))
                    removed.Add((file, measure.Measured?.FirstOrDefault(m => m.Kind == FileChangeKind.Renamed
                        && string.Equals(m.OldPath?.Replace('\\', '/'), file.Path, StringComparison.OrdinalIgnoreCase))?.Path.Replace('\\', '/')));
                else if (ProtectedFiles.HashOf(full) is { } now && !string.Equals(now, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    edited.Add(file);
            }
        if (removed.Count == 0 && edited.Count == 0) return null;

        var prefix = stepNumber is { } number ? $"[{number}] " : "";
        var at = stepNumber is { } no ? $"step {no}" : "the step";
        string Gone((ProtectedFile File, string? RenamedTo) r) => r.RenamedTo is { } to ? $"renamed to {to}" : "removed (or renamed)";

        ProtectedVerdict? verdict = null;
        var lines = new List<string>();
        if (removed.Count > 0 && step.ChangesProtected)
        {
            // The request asks for it, as the planner read it: the reviewer is shown what went, and decides.
            lock (session.ProtectedReleased)
                foreach (var r in removed) session.ProtectedReleased.Add(r.File.Path);
            lines.AddRange(removed.Select(r => ProtectedLine(r.File.Path, r.File.CheckId, "REMOVED - " + Gone(r) + ". The plan marks this "
                + "step as one the request asks to change protected files, so it is not sent back for it: judge whether the request "
                + "asks for this removal.", null)));
        }
        else if (removed.Count > 0 && step.CheckTiming == StepCheckTiming.AtGate)
        {
            var first = removed[0].File.CheckId;
            var advice = string.Join("\n", removed.Select(r => ProtectedAdvice(r.File.Path, r.File.CheckId)));
            var notes = $"the engine's check '{first}' found protected file(s) gone ("
                        + string.Join(", ", removed.Select(r => $"{r.File.Path} {Gone(r)}")) + "): " + string.Join("; ", removed.Select(r => ProtectedAdvice(r.File.Path, r.File.CheckId)));
            await publish(scope.Criterion(new CriterionResult(ProtectedLabel(first, at), ProtectedCommand(first, removed.Select(r => r.File.Path)),
                Required: true, CriterionOutcome.Failed, null, advice, CriterionOrigin.Declared)
                { Check = new CheckFiring(first, CheckBoundary.Step) { Step = stepNumber } }));
            verdict = new ProtectedVerdict(first, notes,
                $"The engine's check '{first}' protects files a person said a run must not lose, and this step took them from where "
                + "the run found them - not a reviewer's judgement:\n" + advice);
        }
        else if (removed.Count > 0)
        {
            // Not this step's to answer for alone: looked for again where what is on disk is settled - the same decision a
            // blocking check is put off by (StepChecksAsync).
            foreach (var group in removed.GroupBy(r => r.File.CheckId, StringComparer.OrdinalIgnoreCase))
            {
                var paths = group.Select(r => r.File.Path).ToArray();
                Owe(session, step.CheckTiming == StepCheckTiming.Staged
                    ? new PendingCheck(group.Key, CheckBoundary.Apply, WorkspaceChecks.Staged) { Step = step.StepId, Protected = paths }
                    : session.Waves is { } waves
                        ? new PendingCheck(group.Key, CheckBoundary.Wave, $"owed at the end of wave {waves.Closed + 1}: steps run side by side, "
                            + "so a file gone now cannot be put on one of them") { Step = step.StepId, Wave = waves.Closed + 1, Protected = paths }
                        : new PendingCheck(group.Key, CheckBoundary.Run, "owed at the run's end: steps run side by side, so a file gone "
                            + "now cannot be put on one of them") { Step = step.StepId, Protected = paths });
            }
            var when = step.CheckTiming == StepCheckTiming.Staged ? "when the run's proposals are applied"
                : session.Waves is { } open ? $"at the end of wave {open.Closed + 1}" : "at the run's end";
            await publish(scope.Ev(EventKind.ContextAssembled, prefix + $"Protected file(s) {string.Join(", ", removed.Select(r => r.File.Path))} "
                + (step.CheckTiming == StepCheckTiming.Staged ? "proposed for removal" : "gone") + $": looked for again {when}"
                + (step.CheckTiming == StepCheckTiming.Staged ? " - until then nothing has removed them."
                    : ", once the steps beside this one have finished - steps run side by side, so it may be another step's doing."), stepNumber));
            lines.AddRange(removed.Select(r => ProtectedLine(r.File.Path, r.File.CheckId, "REMOVED - " + Gone(r) + $". Looked for again {when}: "
                + "a protected file still gone then stops the run.", null)));
        }

        if (edited.Count > 0)
        {
            var diffs = await DiffsAsync(changes, before, measure.After, edited.Select(e => e.Path), ct);
            lines.AddRange(edited.Select(e => ProtectedLine(e.Path, e.CheckId,
                "EDITED - a protected file; judge whether the request asks for this change to it."
                + (diffs.TryGetValue(e.Path, out var diff) ? "" : " (No diff can be made here - the workspace is not a git work tree, "
                                                                 + "or the file is not text: read it.)"),
                diffs.GetValueOrDefault(e.Path))));
        }

        if (lines.Count > 0)
        {
            // What the engine found, for the reviewer - once per attempt, the earlier attempts' marked as history.
            step.Journal.MarkHistorical(step.EvidenceStart, ProtectedFilesTool, ProtectedHistorical);
            step.Journal.Record(stepNumber, ProtectedFilesTool, "{}", ActionOutcome.Succeeded,
                "Found by the engine on what this step changed: files a person protected with a workspace check (its protects "
                + "globs). A protected file is kept as the run found it unless the request asks for the change:\n"
                + string.Join("\n", lines)
                + (unmeasured ? "\nWhat this step changed was not measured, so each of these is said as it differs from the run's start - "
                                + "an earlier step's change among them." : "")
                + (step.CheckTiming == StepCheckTiming.Deferred
                    ? "\nOther steps were running at the same time in the same workspace: a change here may be theirs." : ""),
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        }
        return verdict;
    }

    /// <summary>
    /// The diffs of <paramref name="paths"/> between the step's start and its gate, by path - what a git work tree can show;
    /// nothing outside one, or when the measurement cannot compare. Never a reason to fail the gate.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> DiffsAsync(IWorkspaceChanges? changes, WorkspaceSnapshot? before,
        WorkspaceSnapshot? after, IEnumerable<string> paths, CancellationToken ct)
    {
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (changes is null || before is null || after is null) return found;
        try
        {
            foreach (var change in await changes.CompareAsync(before, after, ct) ?? [])
                if (change.Diff is { Length: > 0 } diff && wanted.Contains(change.Path.Replace('\\', '/')))
                    found[change.Path.Replace('\\', '/')] = diff;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return found;
    }

    /// <summary>
    /// The protected files a step removed while steps ran side by side (or a staged run proposed to), looked for again at the
    /// boundary they were put off to: still gone - and not released since by a step the request asks to remove them - is a
    /// failure there; back where the run found them, nothing is lost. Blocking whatever the check's own severity: a removed
    /// protected file is the finding, not the check's command.
    /// </summary>
    private CriterionResult ProtectedOwed(RunSession session, string id, IEnumerable<string> paths, string at, CheckFiring firing)
    {
        HashSet<string> released;
        lock (session.ProtectedReleased) released = new(session.ProtectedReleased, StringComparer.OrdinalIgnoreCase);
        var all = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var gone = all.Where(p => !released.Contains(p) && !File.Exists(Path.Combine(_workspace.RootPath, p))).ToArray();
        return new CriterionResult(ProtectedLabel(id, at), ProtectedCommand(id, all), Required: true,
            gone.Length > 0 ? CriterionOutcome.Failed : CriterionOutcome.Passed, null,
            gone.Length > 0 ? string.Join("\n", gone.Select(p => ProtectedAdvice(p, id)))
                : "every protected file a step removed is where the run found it, or was removed by a step the request asks to",
            CriterionOrigin.Declared) { Check = firing };
    }
}

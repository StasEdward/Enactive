namespace Enactive.Application;

using Enactive.Application.Projections;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>
/// A question a host puts to a person before something that cannot be taken back. Its words are the same in every
/// host; how it is asked - a dialog, a page - is the host's.
/// </summary>
/// <param name="Alternative">A second way to go ahead, between confirming and cancelling; null when there is none.</param>
internal sealed record Question(string Headline, string Body, string Confirm, string Cancel, string? Alternative = null);

/// <summary>A workspace's history as a column shows it: interrupted runs that can be carried on, and finished ones.</summary>
/// <param name="Problem">Why the finished runs could not be read; the interrupted ones are read on their own.</param>
internal sealed record HistoryList(IReadOnlyList<RunCheckpoint> Resumable, IReadOnlyList<RunSummary> Runs, string? Problem);

/// <summary>What carrying on a past run would start: its goal, the saved task it was, and the task it is an attempt at.</summary>
internal sealed record Rerun(string Goal, ResolvedTaskSpec? Spec, Guid TaskId);

/// <summary>
/// The history of runs in a workspace: reading it, opening one, forgetting runs, an unfinished run or a file a run
/// wrote, and starting a past run again. The window did all of it in its code-behind, between dialogs; the service
/// shows the same history to people in a browser, and asks the same questions in the same words.
///
/// <para>Questions are returned, never asked: a host asks in its own way and calls the action with the answer. The
/// actions report what went wrong as words to show, never by throwing at the host.</para>
/// </summary>
/// <param name="keepRuns">The newest runs a workspace keeps; 0 keeps them all. Read at each load - it is a setting.</param>
internal sealed class RunHistoryService(Func<int> keepRuns)
{
    /// <summary>
    /// The workspace's runs, retention applied. Summaries, not records: a column shows a few fields per row, and reading
    /// every event of every run to fill them is a cost that grows with how much work has been done there.
    /// </summary>
    public async Task<HistoryList> LoadAsync(string root, CancellationToken ct)
    {
        var workspace = WorkspaceInfo.For(root);

        // Interrupted runs on their own: a checkpoint folder that cannot be read must not cost the history list, which
        // is the thing somebody opened the column for.
        IReadOnlyList<RunCheckpoint> resumable;
        try { resumable = await new JsonCheckpointStore(workspace).LoadAllAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { resumable = []; }

        try
        {
            var store = RunStoreFactory.Create(workspace);
            var runs = await store.LoadSummariesAsync(ct);
            // Retention, applied where the list is read rather than on a timer: this is the moment the number of runs
            // matters, and a background trimmer would be deleting somebody's history while they were not looking.
            return new HistoryList(resumable, await TrimAsync(store, runs, ct), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A store that cannot be reached is a fact to show, not a crash.
            return new HistoryList(resumable, [], ex.Message);
        }
    }

    /// <summary>
    /// Off by default, and a COUNT rather than an age: what makes a list unusable is how many rows are in it, and an
    /// age would leave the busiest workspace - the one with the problem - untouched. A delete that fails stops the
    /// trim and leaves the list as it was: a row still in the store belongs on screen, and the next load tries again.
    /// </summary>
    private async Task<IReadOnlyList<RunSummary>> TrimAsync(IRunStore store, IReadOnlyList<RunSummary> runs, CancellationToken ct)
    {
        var doomed = RunHousekeeping.BeyondTheNewest(runs, keepRuns());
        if (doomed.Count == 0)
            return runs;

        foreach (var run in doomed)
        {
            try { await store.DeleteAsync(run.RunId, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { return runs; }
        }
        return runs.Except(doomed).ToArray();
    }

    /// <summary>One run, whole - or why it cannot be shown.</summary>
    public async Task<(RunRecord? Record, string? Problem)> OpenAsync(string root, RunSummary summary, CancellationToken ct)
    {
        try
        {
            return await RunStoreFactory.Create(WorkspaceInfo.For(root)).LoadAsync(summary.RunId, ct) is { } record
                ? (record, null)
                : (null, $"That run is no longer in the store: {RunTitle.For(summary)}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    // ── forgetting runs ─────────────────────────────────────────────────────

    /// <summary>
    /// What goes is the RECORD - what was asked, what the engine did, the paths it wrote. The files stay: they are the
    /// person's work, and somebody pruning a list of old runs is tidying a list, not asking for their code back. The
    /// question says so, because a delete vaguer than what it does gets answered by guessing.
    /// </summary>
    public static Question DeleteOne(RunSummary run)
        => new($"Delete the run “{RunTitle.For(run)}”?",
            "Its history goes: what was asked, every step, and every event behind it. This cannot be undone."
            + (run.Artifacts.Count > 0
                ? $" The {run.Artifacts.Count} file(s) it wrote stay in your workspace — only the record of them goes."
                : ""),
            "Delete", "Keep");

    /// <summary>The count is named, because "delete these" is where the person's "these" and the program's must agree.</summary>
    public static Question DeleteMany(IReadOnlyList<RunSummary> runs)
    {
        var files = runs.Sum(r => r.Artifacts.Count);
        return new($"Delete {runs.Count} run{(runs.Count == 1 ? "" : "s")}?",
            "Their history goes: what was asked, every step, and every event behind it. This cannot be undone."
            + (files > 0 ? $" The {files} file(s) they wrote stay in your workspace — only the record of them goes." : ""),
            $"Delete {runs.Count}", "Keep");
    }

    /// <summary>Forgets one run; why it could not be, or null.</summary>
    public async Task<string?> DeleteAsync(string root, RunSummary run, CancellationToken ct)
    {
        try
        {
            await RunStoreFactory.Create(WorkspaceInfo.For(root)).DeleteAsync(run.RunId, ct);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Said, and the list left alone: a row that vanishes while the record is still in the store is a lie the
            // next refresh exposes.
            return "Could not delete that run: " + ex.Message;
        }
    }

    /// <summary>
    /// Forgets several; what could not be, said, or null. One failure does not stop the rest - a bulk delete half done
    /// with no way to tell how far it got is worse than one that names how many it could not do.
    /// </summary>
    public async Task<string?> DeleteAsync(string root, IReadOnlyList<RunSummary> runs, CancellationToken ct)
    {
        var store = RunStoreFactory.Create(WorkspaceInfo.For(root));
        var refused = 0;
        foreach (var run in runs)
        {
            try { await store.DeleteAsync(run.RunId, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { refused++; }
        }
        return refused > 0 ? $"{refused} of {runs.Count} could not be deleted; the rest are gone." : null;
    }

    /// <summary>
    /// Forgetting an interrupted run takes away the offer to resume it. What it changed is named from the record the
    /// store kept on disk, and putting it back is a choice of its own; with no record, there is nothing to offer.
    /// </summary>
    public static Question ForgetUnfinished(string root, RunCheckpoint checkpoint)
    {
        var changes = DiskArtifactStore.ChangesOf(WorkspaceInfo.For(root), checkpoint.RunId);
        var headline = $"Delete the unfinished run “{RunTitle.OneLine(checkpoint.Title)}”?";
        var cannotResume = $"It can no longer be resumed: the {checkpoint.Remaining} step(s) left will not be run. "
                           + "Its history, if it has any, stays in the list.";
        return changes.Count == 0
            ? new(headline, cannotResume + " No record of the files it changed is kept, so they stay as they are.", "Delete", "Keep")
            : new(headline, cannotResume + "\n\n" + UnfinishedChanges.Describe(changes),
                "Delete and put the files back", "Keep", Alternative: "Delete, keep the files");
    }

    /// <summary>Forgets an interrupted run, putting its files back first when asked to.</summary>
    /// <returns>What putting back left as it was, and why the run could not be forgotten - each null when nothing.</returns>
    public async Task<(string? PutBack, string? Problem)> ForgetUnfinishedAsync(
        string root, RunCheckpoint checkpoint, bool putBack, CancellationToken ct)
    {
        var workspace = WorkspaceInfo.For(root);
        string? kept = null;
        if (putBack)
        {
            var report = await DiskArtifactStore.PutBackRunAsync(workspace, checkpoint.RunId, ct);
            if (report.Kept.Count > 0)
                kept = $"Put back {report.Reverted.Count} file(s); left {report.Kept.Count} as they are: "
                       + string.Join("; ", report.Kept.Select(p => $"{p} ({report.Reasons?.GetValueOrDefault(p) ?? "kept"})"));
        }

        try
        {
            await new JsonCheckpointStore(workspace).DeleteAsync(checkpoint.RunId, ct);
            return (kept, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Said, and the row left: one that vanishes while its checkpoint is still on disk comes back next refresh.
            return (kept, "Could not delete that unfinished run: " + ex.Message);
        }
    }

    // ── a file a past run wrote ─────────────────────────────────────────────

    /// <summary>
    /// The file as it is on disk now, under the workspace; null when it is not there - or not under the workspace at
    /// all. A run record names its files as paths relative to the folder, and one that climbs out of it ("..\") is not
    /// a file of this workspace to show or delete, whoever wrote the record: the service reads records for people who
    /// may not open that folder themselves.
    /// </summary>
    public static string? FileOf(string root, string relative)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// Asked with the file's own date: a week of edits does not show in a run record, and this deletes what is on disk
    /// NOW, not what the run wrote. Null when the file is already gone (or not the workspace's).
    /// </summary>
    public static Question? DeleteFile(string root, string relative)
        => FileOf(root, relative) is { } full && File.Exists(full)
            ? new($"Delete “{relative}”?",
                $"This deletes the file as it is on disk now, last changed {File.GetLastWriteTime(full):yyyy-MM-dd HH:mm} "
                + "- not the version this run wrote. There is no undo.",
                "Delete", "Keep")
            : null;

    /// <summary>Deletes it, and says what became of it - the words the file's card shows.</summary>
    public static (string Status, bool Done) DeleteFileNow(string root, string relative)
    {
        if (FileOf(root, relative) is not { } full)
            return ("not in this workspace", true);
        if (!File.Exists(full))
            return ("already gone", true);
        try
        {
            File.Delete(full);
            return ("deleted", true);
        }
        catch (Exception ex)
        {
            return ("error: " + ex.Message, false);
        }
    }

    // ── starting a past run again ───────────────────────────────────────────

    /// <summary>
    /// The same run again, exactly as it was: the specification it recorded, or the request a typed run recorded. It
    /// keeps the task id - these are attempts at ONE task, not unrelated runs that happen to say the same thing.
    /// Null when there is nothing to run again.
    /// </summary>
    public static Rerun? Retry(RunRecord record)
    {
        var taskId = record.TaskId == Guid.Empty ? Guid.NewGuid() : record.TaskId;
        if (ResolvedTaskSpec.Parse(record.Spec) is { } spec)
            return new Rerun(spec.Goal, spec, taskId);
        return RunHistory.RequestOf(record) is { Length: > 0 } request ? new Rerun(request, null, taskId) : null;
    }

    /// <summary>
    /// The template as it stands NOW, with the answers this run used - the current version of the same question, where
    /// Retry repeats what happened. Null with no problem when the run was no template's, or the template is gone.
    /// </summary>
    public static (Rerun? Run, string? Problem) RunAgain(string root, RunRecord record, PermissionPolicy policy)
    {
        if (ResolvedTaskSpec.Parse(record.Spec) is not { } old || new TemplateStore(root).Find(old.TemplateId) is not { } template)
            return (null, null);

        var resolved = TemplateResolution.Resolve(template, WorkspaceInfo.For(root), policy, old.Parameters);
        if (resolved.Spec is not { } spec)
            // The template changed under it - a new required parameter, a value its type no longer accepts. Said, rather
            // than nothing happening when a button is pressed.
            return (null, $"'{template.Name}' has changed since this run: "
                          + string.Join("; ", resolved.Problems.Select(p => p.Message))
                          + " Open Templates and fill it in again.");

        return (new Rerun(spec.Goal, spec, record.TaskId == Guid.Empty ? Guid.NewGuid() : record.TaskId), null);
    }
}

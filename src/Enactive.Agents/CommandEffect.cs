namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Tools;

/// <summary>
/// What a call of a tool that names no paths - a shell, PowerShell, git, docker, an MCP tool with an effect - changed of
/// the files the run found, held to the request's limit on changes after the call ran.
///
/// <para><b>Why.</b> Bench build-error, 2026-10-18: refused twice on edit_file by the change limit, the worker rewrote the
/// forbidden Report.cs with <c>powershell ... -replace ... | Set-Content</c>. <see cref="ChangeLimitGuard"/> is asked only
/// of tools that name their paths, so nothing held the command to the limit, and the run ended with the file changed.
/// A command cannot be asked about beforehand - what a one-liner or a script will change is a guess - so the workspace is
/// measured right before and right after it. What changed in that window is the call's, not a person's.</para>
///
/// <para>A refused file is put back only byte for byte (<see cref="IWorkspaceChanges.PutBackAsync"/>), and named where it
/// cannot be. The command did run: its call stays as it went, and the step is told in its result.</para>
/// </summary>
internal sealed class CommandEffect(ChangeLimitGuard guard, StepFrame frame, string? stepTitle)
{
    /// <summary>How many files the step is told by name; the rest are counted. A formatter can change hundreds, and a
    /// line that lists them all hides the one that matters - every path is in the event's payload all the same.</summary>
    private const int Named = 10;

    /// <summary>What the call's result is to say (null: nothing refused), and the events to record.</summary>
    internal sealed record Outcome(string? Appended, IReadOnlyList<WorkEvent> Events)
    {
        public static Outcome Nothing { get; } = new(null, []);
    }

    /// <summary>
    /// Only where all three hold: the step is under the request's limit; the step is measured (steps side by side have no
    /// <see cref="StepFrame.StepStart"/>, and a change in the window could be another step's); the tool changes the
    /// workspace and names no paths. Anywhere else nothing is measured and nothing asked, as before.
    /// </summary>
    public static bool Applies(ChangeLimitGuard? guard, StepFrame frame, ToolDefinition? definition)
        => guard is not null && frame.Changes is not null && frame.StepStart is not null
           && definition is not null && WriteBoundary.Unchecked(definition);

    /// <summary>The workspace right before the call, or null when it cannot be measured - and then the call is not held.</summary>
    public Task<WorkspaceSnapshot?> BeforeAsync(CancellationToken ct)
        => frame.Changes?.TakeAsync(ct) ?? Task.FromResult<WorkspaceSnapshot?>(null);

    /// <param name="before">The snapshot before the call; null when it could not be taken.</param>
    public async Task<Outcome> AfterAsync(ToolCall call, WorkspaceSnapshot? before, string? saidByStep, CancellationToken ct)
    {
        if (frame.Changes is not { } changes || before is null || await changes.TakeAsync(ct) is not { } after
            || await changes.CompareAsync(before, after, ct) is not { } found)
            // Said, not passed over: a run's record that shows no question cannot otherwise tell "nothing to ask" from
            // "could not look" (review, 2026-10-28) - a git that timed out, a workspace past the scan's limit.
            return new(null, [Event(EventKind.ErrorObserved, $"A {call.Name} call's changes went unmeasured against the request's "
                + "limit on changes: the workspace could not be measured around it.", null)]);
        if (found.Count == 0) return Outcome.Nothing;

        // The run's start, read once for the call: asked path by path, each answer took the workspace again - a
        // formatter's 300 files, 300 snapshots (review, 2026-10-28). What changed since then, also once.
        var start = frame.Store.MeasuredStart;
        var was = start is null ? null : await changes.PathsAsync(start, ct);
        var sinceStart = start is null ? null : (await changes.ComparePathsAsync(start, after, ct))?
            .SelectMany(c => c.OldPath is { } o ? new[] { c.Path, o } : [c.Path]).Select(ShellLookup.Normal)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var asked = new List<FileChange>();
        foreach (var change in found)
        {
            if (change.Kind == FileChangeKind.Added) continue;                               // new: nothing the run found
            var path = ShellLookup.Normal(change.Kind == FileChangeKind.Renamed && change.OldPath is { } old ? old : change.Path);
            if (path.Length == 0 || path.Equals(WorkspaceGuard.ReservedFolder, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(WorkspaceGuard.ReservedFolder + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (was is not null)
            {
                if (!was.Contains(path)) continue;                                            // the run made it
                // As the run found it again - a `git checkout -- file` undoing an earlier step: no limit forbids that,
                // as restore_file is never asked about. Asked, it was refused and the earlier change written back.
                if (sinceStart is not null && !sinceStart.Contains(path)) continue;
            }
            else if ((await frame.Store.BeforeRunAsync(path, ct)).State == BeforeRunState.Absent) continue;   // the run made it
            asked.Add(change with { Path = path });
        }
        if (asked.Count == 0) return Outcome.Nothing;

        var decided = await guard.CheckMadeAsync(frame.StepNo, stepTitle, saidByStep, call, asked, ct);
        var events = new List<WorkEvent>();
        if (decided.Usage.Any) events.Add(guard.UsageEvent(frame.TaskId, frame.RunId, frame.StepNo, decided.Usage));
        if (decided.Note is { } note) events.Add(Event(EventKind.ErrorObserved, note, null));
        if (decided.Refused.Count == 0) return new(null, events);

        var changedWithIt = found.SelectMany(c => c.OldPath is { } o ? new[] { c.Path, o } : [c.Path]).Select(ShellLookup.Normal).ToArray();
        var putBack = new List<string>();
        var left = new List<(string Path, string Why)>();
        foreach (var path in decided.Refused)
        {
            var result = await changes.PutBackAsync(before, after, path, changedWithIt, ct);
            if (result.Done) putBack.Add(path);
            else left.Add((path, result.Why ?? PutBackReasons.NotExact));
        }
        // A rename's new copy was made by the command: not the run's to delete, so it is named.
        var copies = asked.Where(c => c.Kind == FileChangeKind.Renamed && decided.Refused.Contains(c.Path))
            .Select(c => found.First(f => f.OldPath is { } o && ShellLookup.Normal(o) == c.Path).Path)
            .Select(ShellLookup.Normal).ToArray();

        var said = $"REFUSED after the fact: this command changed {Names(decided.Refused)}, which the request limits "
                   + $"({string.Join("; ", guard.Limits.Select(l => $"\"{l}\""))}) - {decided.Reason}.";
        if (putBack.Count > 0)
            said += putBack.Count == decided.Refused.Count ? " It was put back as it was before the command."
                : $" {Names(putBack)} put back as it was before the command.";
        foreach (var (path, why) in left.Take(Named))
            said += $" '{path}' could not be put back ({why}). Put it back yourself, or hand on why the step cannot be done "
                    + "without changing it.";
        foreach (var copy in copies)
            said += $" '{copy}' is the command's copy of it, and is left.";

        var step = frame.StepNo is { } n ? $"[{n}] " : "";
        var line = (putBack.Count > 0 ? $"{step}Put back after a command: {string.Join(", ", putBack)}." : $"{step}Not put back after a command.")
                   + string.Concat(left.Select(l => $" Left as it is: {l.Path} - {l.Why}."))
                   + string.Concat(copies.Select(c => $" Left as it is: {c} - made by the command."))
                   // A change made in the window by someone else is taken for the command's and written over; the bytes
                   // the command left are still in git, and this is where to find them (review, 2026-10-28).
                   + (after.Tree is { } tree && putBack.Count > 0 ? $" Each file as the command left it: git tree {tree}." : "");
        events.Add(Event(EventKind.ArtifactReverted, line,
            WorkEventPayload.RevertPayload(frame.StepNo, putBack, [.. left.Select(l => l.Path), .. copies])));
        return new(said, events);
    }

    private static string Names(IReadOnlyList<string> paths)
        => string.Join(", ", paths.Take(Named).Select(p => $"'{p}'")) + (paths.Count > Named ? $", and {paths.Count - Named} more" : "");

    private WorkEvent Event(EventKind kind, string summary, string? payload)
        => new(Guid.NewGuid(), frame.TaskId, frame.RunId, DateTimeOffset.UtcNow, kind, summary, payload);
}

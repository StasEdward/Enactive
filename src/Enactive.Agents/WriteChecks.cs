namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// The run's <c>FireOn = Write</c> checks for one step's tool loop (Plan Phase 3.2): fast feedback to the worker on what
/// it has just written. <see cref="ToolResultAccounting.Record"/> tells it of each successful write; at the end of the
/// turn each check a write of the turn covered runs once, and what it said is added to the result of the LAST write of
/// the turn it covers - the tool result the worker reads next - and to the step's journal as the engine's own, so the
/// reviewer sees what the worker was told.
///
/// <para><b>Always advisory.</b> A write-time result never refuses the write, never undoes it and never ends the step:
/// the worker is mid-edit, and a check that fails between the first and the second of two writes that belong together
/// would be fighting the worker for the file. AI-DLC keeps its write-time hooks advisory for the same reason; a check
/// that must hold the work is a step or run check. A manifest asking a write check to block is read as advisory when it
/// is loaded (CheckStore), so nothing here has to decide it.</para>
///
/// <para><b>Once per check per turn, after the turn's last write.</b> A turn often writes several files that are one
/// change. Run after each, the check would judge the first file against the others' old versions - a finding about a
/// state that lasts until the next call - and would run as many times as there are writes. So the turn's writes are
/// gathered and each owed check runs once, when the turn's calls are all answered and before the worker is asked
/// again.</para>
///
/// <para>Per tool loop, which is per attempt of a step: a note that a check is not run (it needs permission) is said
/// once in it, not at every write. A new attempt is told again - it may be a conversation that never read it.</para>
/// </summary>
internal sealed class WriteChecks
{
    /// <summary>
    /// The longest a check may take to be run at write time, in seconds. A write-time check runs INSIDE the worker's
    /// turn: the worker waits for it, and it can run every turn of an edit loop. Thirty seconds is a linter, a formatter
    /// check or a schema validation over a few files with room to spare, and caps what a turn can be held up per check;
    /// a build or a test run takes longer and belongs at the step, where it runs once. It is under the command tool's own
    /// limit (run_command stops a command at 60s), so a check's own timeout is what stops it. A check declared with more
    /// is not run at write time at all - said once a run (<see cref="ForStep"/>) - rather than run and cut short: a
    /// check stopped half-way says nothing, every turn.
    /// </summary>
    internal const int TimeoutLimitSeconds = 30;

    /// <summary>
    /// How much of a check's output the WORKER is shown: its start and its end, about ten lines of a linter's or a
    /// compiler's output - the first finding and the summary line. It is added to the result of a write, every turn the
    /// check runs, in the conversation of a worker that is often a small local model with a small window; a check's
    /// whole output there at each write would fill the window with old findings. The reviewer's copy in the journal is
    /// cut at the evaluator's own, larger limit (SuccessEvaluator.Trim).
    /// </summary>
    internal const int WorkerOutputChars = 600;

    /// <summary>The engine's journal entry for a write-time check: what the worker was told, for the reviewer.</summary>
    internal const string JournalTool = "engine_write_check";

    /// <summary>How many of the paths a check was owed for are named; the rest are counted.</summary>
    private const int NamedPaths = 5;

    /// <summary>
    /// Said once a run that is staged and has write checks. Skipped, not deferred: a write check exists to inform the
    /// worker mid-step, and when the proposals are applied there is no worker left to inform. The checks that judge the
    /// applied files are the step and run checks, which the staged run accounts for itself.
    /// </summary>
    internal const string SkippedWhenStaged =
        "This run is staged: its writes are proposals, not files on disk, so its write-time checks are not run - a check "
        + "run now would judge the files as they were. They exist to tell the worker about its edits while it makes them; "
        + "when the proposals are applied there is no worker to tell, so they are skipped, not kept for later.";

    private readonly IReadOnlyList<ResolvedCheck> _checks;
    private readonly StepFrame _frame;

    /// <summary>What this turn's writes owe: each check, the last write of the turn it covers, and the paths it covered.</summary>
    private readonly Dictionary<string, (string CallId, List<string> Paths)> _owed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks this loop has already said are not run, so the worker is told once and not at every write.</summary>
    private readonly HashSet<string> _toldNotRun = new(StringComparer.OrdinalIgnoreCase);

    private WriteChecks(IReadOnlyList<ResolvedCheck> checks, StepFrame frame)
    {
        _checks = checks;
        _frame = frame;
    }

    /// <summary>Whether a write of this turn covered a check that has not run for it yet.</summary>
    public bool Owed => _owed.Count > 0;

    /// <summary>
    /// The write checks a step's loop runs, and what is to be said once a run about the ones it does not. Null - and
    /// nothing done at any write - when the run has no enabled write check it can run: then every tool result is exactly
    /// what it was before write checks existed.
    /// </summary>
    /// <param name="sayOnce">Sentences for the run's feed, each said once a run (the caller keeps count).</param>
    public static WriteChecks? ForStep(CheckSnapshot? snapshot, bool staged, StepFrame frame, out IReadOnlyList<string> sayOnce)
    {
        var declared = snapshot?.Checks.Where(c => c.Definition.Enabled && c.Definition.FireOn == CheckFire.Write).ToArray() ?? [];
        if (declared.Length == 0)
        {
            sayOnce = [];
            return null;
        }
        if (staged)
        {
            sayOnce = [SkippedWhenStaged];
            return null;
        }

        var said = new List<string>();
        var runnable = new List<ResolvedCheck>();
        foreach (var check in declared)
        {
            if (string.IsNullOrWhiteSpace(check.Definition.Command))
                // The ecosystem's own build or test raised to fire on write (CheckStore lets a manifest without a command
                // do that). Those run through the ecosystem over its targets - a build of a project, not a command over
                // the files written - and running them is the step's and the run's business; here there is no command.
                said.Add($"The check '{check.Id}' fires on write, but it has no command of its own: it is the workspace's "
                         + "build or test, run through its ecosystem, which is not run at write time. Give it a command, or fire it on step.");
            else if (check.Definition.TimeoutSeconds > TimeoutLimitSeconds)
                said.Add($"The check '{check.Id}' fires on write, but its timeout ({check.Definition.TimeoutSeconds}s) is over the "
                         + $"{TimeoutLimitSeconds}s a write-time check may take - it would run inside the worker's turn - so it is "
                         + $"not run at write time. Give it a timeoutSeconds of {TimeoutLimitSeconds} or less, or fire it on step.");
            else
                runnable.Add(check);
        }
        sayOnce = said;
        return runnable.Count == 0 ? null : new WriteChecks(runnable, frame);
    }

    /// <summary>
    /// A call's write succeeded: the checks whose globs cover a path it changed are owed, with this call as the turn's
    /// last write for them so far. The paths are the ones the call recorded as changed (ToolResult.ChangedPaths: the
    /// tool's own account, or the path arguments of its full call) - the same ones the step's FileWrite evidence is.
    /// </summary>
    public void Wrote(ToolCall call, ToolResult result)
    {
        foreach (var path in result.ChangedPaths ?? [])
        {
            if (Relative(path) is not { } relative)
                continue;
            foreach (var check in _checks)
            {
                if (!check.Definition.Matches.Any(glob => CheckGlob.IsMatch(glob, relative)))
                    continue;
                var paths = _owed.TryGetValue(check.Id, out var owed) ? owed.Paths : [];
                if (!paths.Contains(relative, StringComparer.OrdinalIgnoreCase)) paths.Add(relative);
                _owed[check.Id] = (call.Id, paths);
            }
        }
    }

    /// <summary>
    /// Runs what this turn's writes owe, each check once, and adds what each said to the result of the last write it
    /// covered. Returns a line per check for the run's feed. Never throws for a check: one that cannot be run says so.
    /// </summary>
    /// <param name="whyNotAllowed">Why the engine may not run a command of its own without asking, or null when it may
    /// (BuildRegression.WhyNotAllowed). A check that would need an approval is not run - the worker is mid-turn and the
    /// engine does not stop a person for a check nobody asked to run now - and the worker is told so once.</param>
    /// <param name="run">Runs one check as a criterion through the run's SuccessEvaluator: the same permission gate, the
    /// same "the exit code decides", the same reading of a test command that ran no tests.</param>
    public async Task<IReadOnlyList<string>> RunOwedAsync(string? whyNotAllowed,
        Func<SuccessCriterionDefinition, CancellationToken, Task<CriterionResult>> run, CancellationToken ct)
    {
        var said = new List<string>();
        foreach (var check in _checks)
        {
            if (!_owed.Remove(check.Id, out var owed))
                continue;
            var on = On(owed.Paths);

            if (whyNotAllowed is not null)
            {
                if (!_toldNotRun.Add(check.Id))
                    continue;
                Tell(owed.CallId, $"[Write-time check '{check.Id}': not run: needs permission - {whyNotAllowed}. It is not run "
                    + "at write time in this step; nothing about your write follows from that.]");
                said.Add(Record(check, on, $"not run: needs permission ({whyNotAllowed})", null));
                continue;
            }

            var criterion = new SuccessCriterionDefinition(check.Id, check.Definition.Command!, 0, Required: false);
            var seconds = check.Definition.TimeoutSeconds;
            CriterionResult? result;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // The check's own timeout, kept here: the evaluator has none, and a check that hangs would hold the
                // worker's turn until the command tool's own limit, with nothing said about why.
                limit.CancelAfter(TimeSpan.FromSeconds(seconds));
                try
                {
                    result = await run(criterion, limit.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new CriterionResult(check.Id, criterion.Command, false, CriterionOutcome.Unknown, null,
                        $"the check could not be run: {ex.Message}");
                }
            }

            var verdict = result is null ? $"did not finish within {seconds}s, so it was stopped and said nothing"
                : result.Outcome switch
                {
                    CriterionOutcome.Passed => $"PASS (exit {result.ExitCode})",
                    CriterionOutcome.Failed => $"FAIL (exit {result.ExitCode})",
                    _ => "NOT CHECKED - " + SuccessEvaluator.Trim(result.Detail)
                };
            var output = result?.Outcome == CriterionOutcome.Failed ? result.Output ?? result.Detail : null;
            Tell(owed.CallId, $"[Write-time check '{check.Id}': {verdict} - {on}"
                + (string.IsNullOrWhiteSpace(output) ? "" : "\n" + Shortening.HeadAndTail(output!.Trim(), WorkerOutputChars))
                + (result?.Outcome == CriterionOutcome.Failed
                    ? "\nAdvisory: it does not undo your write or end the step. If what it says is about your change, fix it.]"
                    : "]"));
            said.Add(Record(check, on, verdict, output is null ? null : SuccessEvaluator.Trim(output)));
        }
        _owed.Clear();
        return said;
    }

    /// <summary>
    /// Adds a check's line to the result the worker was given for <paramref name="callId"/> - after it, so the write's own
    /// result reads as before. The latest result of that call: a call id names one call of the conversation.
    /// </summary>
    private void Tell(string callId, string line)
    {
        var messages = _frame.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatRole.Tool && messages[i].ToolCallId == callId)
            {
                messages[i] = messages[i] with { Content = (messages[i].Content ?? "") + "\n\n" + line };
                return;
            }
    }

    /// <summary>The engine's journal entry for what the worker was told, and the feed's line for it.</summary>
    private string Record(ResolvedCheck check, string on, string verdict, string? output)
    {
        _frame.Journal.Record(_frame.StepNo, JournalTool, JsonSerializer.Serialize(new { check = check.Id }), ActionOutcome.Succeeded,
            $"Run by the engine after this step's write(s), and told to the worker with the write's result - a write-time "
            + $"check, advisory: it decides nothing on its own. '{check.Id}' ({check.Definition.Command}) {on}: {verdict}"
            + (string.IsNullOrWhiteSpace(output) ? "" : "\n" + output),
            WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        return $"Write-time check '{check.Id}': {verdict} - {on}.";
    }

    private static string On(IReadOnlyList<string> paths)
        => "on " + string.Join(", ", paths.Take(NamedPaths))
           + (paths.Count > NamedPaths ? $" and {paths.Count - NamedPaths} more" : "");

    /// <summary>
    /// A changed path as the checks' globs read it: workspace-relative with '/'. Null for a path outside the workspace,
    /// which no check can be about, and for the worker's own scratch area, which is not the workspace's content - a
    /// check on <c>**/*.md</c> would otherwise judge the worker's notes to itself.
    /// </summary>
    private string? Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var relative = path;
        if (Path.IsPathRooted(path))
        {
            try { relative = Path.GetRelativePath(_frame.WorkspaceRoot, path); }
            catch (ArgumentException) { return null; }
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
        }
        relative = relative.Replace('\\', '/');
        while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        return relative.Length == 0 || WorkspaceGuard.IsScratchRelative(_frame.WorkspaceRoot, relative) ? null : relative;
    }
}

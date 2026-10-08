namespace Enactive.Agents;

using System.Runtime.CompilerServices;
using static Enactive.Agents.ToolCallParsing;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// Whether a turn ends the step, and how: carry on with its calls, carry on after being told something once, or
/// stop - done, unfinished, blocked or failed.
///
/// <para><b>Why one module.</b> The rules are a list whose ORDER is the rule: a step that was to hand its result on
/// is reminded before any verdict on the calls it left open (run 4f1d97: three steps that had written their findings
/// never heard the reminder, because the verdict on open calls came first); a block the engine can see ends the step
/// before it is told about its open calls; each reminder is given once. The order and the once-only flags lived in
/// the 1800-line tool loop as its locals, where the only way to reach them was to script a whole run. Here they are
/// one ordered list, asked once per turn.</para>
///
/// <para><b>What it records.</b> What a step is told - the reminders, the request to re-send a call - is added to its
/// conversation here, and every event about the ending is emitted here, as it happens. The loop learns only what
/// to do next.</para>
///
/// <para><b>What it owns.</b> The step's open calls (<see cref="Open"/>), which only it reads: the admission and the
/// accounting of results write to them, and the loop does not touch them. The loop made them, wrote to them in
/// two places of its own and decided a reported block beside them, so a rule about what a step left open could
/// be anywhere in it.</para>
/// </summary>
/// <param name="readFile">A workspace file as the run sees it - a proposal staged for it, or what is on disk; null when there is neither.</param>
/// <param name="reviewed">Whether a reviewer judges this step when it ends.</param>
internal sealed class StepEnding(
    ExecutionJournal journal,
    IReadOnlyList<ToolDefinition> definitions,
    List<ChatMessage> messages,
    StepProgress progress,
    Guid taskId,
    Guid runId,
    int? stepNo,
    string workspaceRoot,
    Func<string, CancellationToken, Task<string?>> readFile,
    IReadOnlyList<SuccessCriterionDefinition>? stepCriteria = null,
    StepOutputSchema? outputSchema = null,
    StepOutputSlot? outputSlot = null,
    bool reviewed = false)
{
    /// <summary>One turn as the step ends it - or not.</summary>
    /// <param name="Calls">The tool calls the turn made, or null.</param>
    /// <param name="OnlyHandOn">The turn was kept for the hand-over alone.</param>
    /// <param name="Described">A call the reply described in text instead of making, when it did.</param>
    /// <param name="ActionsTaken">Calls this step has carried out so far.</param>
    /// <param name="ReasoningLength">Characters of reasoning the turn produced.</param>
    /// <param name="ContextWindow">The model's context window, when somebody has said - asked only when it is needed.</param>
    internal sealed record Turn(
        string? ReplyText,
        IReadOnlyList<ToolCall>? Calls,
        bool OnlyHandOn,
        ToolCall? Described,
        int ActionsTaken,
        int ReasoningLength,
        int? CompletionTokens,
        int? PromptTokens,
        Func<int?> ContextWindow);

    internal enum Next
    {
        /// <summary>The turn made calls: run them.</summary>
        Work,

        /// <summary>The step was told something and carries on with another turn.</summary>
        Continue,

        /// <summary>The step is over: see <see cref="Verdict.Kind"/>.</summary>
        End
    }

    /// <summary>What a turn came to. Filled by <see cref="EndAsync"/>, which cannot return it.</summary>
    internal sealed class Verdict
    {
        public Next Next { get; set; }
        public StepOutcomeKind Kind { get; set; }
        public string? Reason { get; set; }
        public OutcomeCause? Cause { get; set; }

        internal void Ends(StepOutcomeKind kind, string? reason, OutcomeCause? cause = null)
        {
            Next = Next.End;
            Kind = kind;
            Reason = reason;
            Cause = cause;
        }
    }

    /// <summary>The calls this step made that did not go through and nothing has made good - written by the admission and the accounting of results.</summary>
    public OpenFailures Open { get; } = new(definitions);

    // A reply that describes a call instead of making one earns exactly ONE re-ask per step; without
    // the cap a model that keeps explaining itself would burn every iteration on the same nudge.
    private bool _repairRequested;
    // Set when the engine asks for a call to be re-sent properly; the calls that arrive on the
    // NEXT turn are what that question bought, and are recorded as such.
    private bool _resendAsked;
    // Whether this step has been told, once, that a criterion the plan attached to it fails.
    private bool _criteriaNudged;
    // Amendment A: told once which calls are still open; what is still open after it, if a reviewer is to judge
    // the step, goes to it marked - the text of it, so a call that fails afterwards is not waved through.
    private bool _openCallsNudged;
    private string? _openCallsForReview;

    /// <summary>Whether the engine asked, last turn, for a call to be sent again properly - asked once, then forgotten.</summary>
    public bool TakeResendAsked()
    {
        var asked = _resendAsked;
        _resendAsked = false;
        return asked;
    }

    /// <summary>Decides whether this turn ends the step, and tells the step what it is told on the way.</summary>
    public async IAsyncEnumerable<WorkEvent> EndAsync(Turn turn, Verdict verdict, [EnumeratorCancellation] CancellationToken ct)
    {
        verdict.Next = Next.Continue;
        var calls = turn.Calls;

        // A step that has said it is done and repeats a call it has already made, with nothing changed since, is
        // done - not stuck. Run fba4d6, 2026-09-29: "S3 done - all 133 tests passed", and the same test run
        // attached, four turns running; its reasoning said "I need to stop repeating", and the step was stopped as
        // stuck, the report step after it skipped. The repeat is not run - it would say what it said - and the step
        // ends on its message by the ordinary road: the same end-of-step checks, and the review.
        if (!turn.OnlyHandOn && !string.IsNullOrWhiteSpace(turn.ReplyText) && calls is { Count: > 0 } && progress.OnlyRepeats(calls))
        {
            foreach (var call in calls)
                messages.Add(ChatMessage.Tool(call.Id, "NOT RUN: this exact call already ran in this step, and nothing has "
                    + "changed since - its result is above. The step ends with your message."));
            yield return Ev(EventKind.ContextAssembled, "The step said it was done and repeated "
                + string.Join("; ", calls.Select(c => $"{c.Name} {Compact(c.ArgumentsJson)}"))
                + ", which it had already made with nothing changed since: not run, and the step ends on its message.");
            calls = null;
        }

        if (calls is { Count: > 0 })
        {
            verdict.Next = Next.Work;
            yield break;
        }

        if (turn.OnlyHandOn)
        {
            messages.Add(ChatMessage.User($"Nothing was handed on. Carry on with the step, and hand its result on with "
                + $"{StepOutputContract.ToolName} when you have it."));
            yield return Ev(EventKind.ContextAssembled, $"The turn for {StepOutputContract.ToolName} was answered with text; the step carries on.");
            yield break;
        }

        if (turn.Described is { } described && !_repairRequested)
        {
            _repairRequested = true;
            _resendAsked = true;
            messages.Add(ChatMessage.User(
                $"Your reply described a '{described.Name}' call in plain text instead of invoking it. "
                + "Nothing was executed. If you meant to act, send it again as a real tool call. "
                + "If that JSON was only an example or an explanation, reply with your final answer."));
            yield return Ev(EventKind.ErrorObserved,
                $"The model described a '{described.Name}' call in plain text instead of invoking it — "
                + "nothing was executed; asked it to re-send the call properly.");
            yield break;
        }

        // NOTHING came back. Not an answer, not a call - and this used to fall through to
        // "genuine final answer" below and mark the step SUCCEEDED. The reviewer then failed
        // it for the only thing it could see ("no tools were run and no files were
        // changed"), the retry produced the same silence, and the run died with a message
        // about the reviewer while the cause - the model's output never arrived - appeared
        // nowhere. An absence is not an answer, which is the same rule as everywhere else
        // here; this was the last place still breaking it.
        if (turn.ReplyText is null && turn.ActionsTaken == 0)
        {
            var why = NothingCameBack(turn);
            yield return Ev(EventKind.ErrorObserved, why);
            verdict.Ends(StepOutcomeKind.Failed, why);
            yield break;
        }

        // An edit that did not apply is settled by its file holding what it wanted - read now, as
        // the run sees the file - and by nothing less: not by another write to the file, not by
        // the same stale old_string sent again (run 4f1d97, 2026-09-28).
        if (Open.EditPaths is { Count: > 0 } edited)
        {
            var contents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in edited)
                contents[path] = await readFile(path, ct);
            foreach (var path in Open.Settle(p => contents.GetValueOrDefault(p)))
                yield return Ev(EventKind.ContextAssembled,
                    $"An edit of {path} that did not apply is settled: the file holds the text it was to put there.");
        }

        // A criterion the plan attached to THIS step, decided by the engine as the step ends - not after the
        // run, when nothing can be done about it. Run 68f92f: the last step was to write the coverage report
        // the plan named, found the previous run's report under another name, said nothing needed doing, and
        // the run failed on the missing file five seconds later. Once, as an ordinary turn; nothing is taken
        // from prose, and what still fails after it is shown to the reviewer as the engine's own finding.
        if (!_criteriaNudged && stepCriteria is { Count: > 0 } && stepNo is { } planNo
            && TypedCriteria.OfStep(stepCriteria, planNo - 1, workspaceRoot)
                .Where(r => r.Outcome == CriterionOutcome.Failed).ToArray() is { Length: > 0 } failing)
        {
            _criteriaNudged = true;
            messages.Add(ChatMessage.User("Before this step ends: the plan checks this step's work, and the engine finds "
                + (failing.Length == 1 ? "this fails" : "these fail") + ":\n"
                + string.Join("\n", failing.Select(r => $"- {r.Name}: {r.Detail}"))
                + "\nMake the work meet " + (failing.Length == 1 ? "it" : "them") + " - the path and the text are the plan's, "
                + "not a suggestion - or say plainly why that cannot be done."));
            yield return Ev(EventKind.ContextAssembled, $"The plan's criteria for this step fail as it ends: "
                + string.Join("; ", failing.Select(r => $"{r.Name} ({r.Detail})")) + ". The step is told, once.");
            yield break;
        }

        // A step that was to hand its result on and has not is reminded ONCE, before any verdict -
        // including the one on calls still open, which used to end the step first: in run 4f1d97
        // three steps that had written their findings never heard the reminder. It is an ordinary
        // turn: limits, budget and cancellation apply to it as to any other, and it makes nothing
        // that did not finish into something that did.
        if (outputSchema is not null && outputSlot is { Values: null, Nudged: false })
        {
            outputSlot.Nudged = true;
            messages.Add(ChatMessage.User(
                $"This step is not finished until it hands its result on with {StepOutputContract.ToolName}. "
                + "Call it now with the step's result: "
                + string.Join(", ", outputSchema.Fields.Where(f => f.Required).Select(f => f.Name)) + "."
                + (Open.Count > 0
                    ? " These calls are still open and keep the step unfinished: " + Open.Describe()
                    : "")));
            yield break;
        }

        // A final answer only settles the step if the actions behind it actually worked. The
        // model saying "Done" over a failed read is the exact shape the follow-up review
        // caught reporting green.
        if (Open.Count > 0 && Open.Describe() != _openCallsForReview)
        {
            var unresolved = Open.Describe();

            // Blocked, not unfinished (Phase 7.1), where the engine can see why: a permission it was refused
            // and nothing made good, or nothing it looked for there at all. Both are for a person to put
            // right, and the run carries on from here once they have.
            if (Open.Block is { } block)
            {
                yield return Ev(EventKind.ErrorObserved, "Blocked: " + block.Reason);
                verdict.Ends(StepOutcomeKind.Blocked, block.Reason, block.Cause);
                yield break;
            }

            // Told once, before any verdict (amendment A). Run 0cf51c, 2026-09-29: a command written for bash
            // failed under cmd.exe, the step ran it again rightly spelled two seconds later and it passed, and
            // the step - never told the first was still open - ended INCOMPLETE on it, two steps skipped.
            if (!_openCallsNudged)
            {
                _openCallsNudged = true;
                messages.Add(ChatMessage.User("Before this step ends: "
                    + (Open.Count == 1 ? "this call" : $"these {Open.Count} calls")
                    + " did not go through, and nothing since has made "
                    + (Open.Count == 1 ? "it" : "them") + " good:\n" + unresolved
                    + "\nMake each good - run it again, corrected - or, if this step's result does not depend on it, "
                    + "say so and why in your closing message."));
                yield return Ev(EventKind.ContextAssembled, $"The step is told, once, of {Open.Count} call(s) still open: {unresolved}");
                yield break;
            }

            // Still open, and a reviewer is to judge the step: it goes to the review with them named, as the
            // engine's own finding - the reviewer decides whether the result stands without them. Without a
            // reviewer, nothing can, and the step is unfinished as before.
            if (reviewed)
            {
                _openCallsForReview = unresolved;
                journal.Record(stepNo, "engine_open_calls", "{}", ActionOutcome.Succeeded,
                    "Checked by the engine as this step ended: calls that did not go through and that nothing made good, "
                    + "after the step was told of them once:\n" + unresolved
                    + "\nJudge whether the step's result stands without them. A result that depends on one of them is not "
                    + "shown to be done; one that does not - a mistyped command made good by a different one - may stand.",
                    WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
                yield return Ev(EventKind.ErrorObserved, $"Finished with {Open.Count} call(s) not made good, after being "
                    + "told once: " + unresolved + " - the review decides whether the result stands without them.");
            }
            else
            {
                // Two different things end a step here, and saying which one is the difference
                // between a person fixing a broken command and a person checking a path.
                yield return Ev(EventKind.ErrorObserved, Open.NothingButMisses
                    ? $"Finished with nothing done: all {Open.Count} lookup(s) this step "
                      + "made found nothing, and nothing else was tried: " + unresolved
                    : $"Finished without resolving {Open.Count} tool call(s) that did not "
                      + "go through: " + unresolved);

                verdict.Ends(StepOutcomeKind.Incomplete, Open.NothingButMisses
                    ? "nothing found and nothing done: " + unresolved
                    : "unresolved tool call: " + unresolved);
                yield break;
            }
        }

        // A step that was to hand its result on as values and has not: told once, with the
        // fields, and then not called finished - the steps after it would have nothing.
        if (outputSchema is not null && outputSlot is { Values: null })
        {
            verdict.Ends(StepOutcomeKind.Incomplete,
                $"the step finished without handing on its declared output ({StepOutputContract.ToolName}), "
                + "so the steps after it would have nothing to work from");
            yield return Ev(EventKind.ErrorObserved, "The step finished without submitting its declared output.");
            yield break;
        }

        verdict.Ends(StepOutcomeKind.Succeeded, null); // genuine final answer - no tool calls
    }

    /// <summary>
    /// What to say when a turn brought back nothing at all - what was OBSERVED first, then the likely cause, and the
    /// window only when the prompt was actually near it.
    /// </summary>
    internal static string NothingCameBack(Turn turn)
    {
        var thought = turn.ReasoningLength;
        var spent = turn.CompletionTokens is { } t and > 0 ? $" while reporting {t} output token(s)" : "";

        // Only when the prompt is actually near the window. Suggesting num_ctx to somebody
        // whose prompt used 1786 of 131072 tokens sends them to tune a setting that has
        // nothing to do with it, which is how a diagnosis becomes a list of everything it
        // might be.
        var declaredWindow = turn.ContextWindow();
        var tight = declaredWindow is { } w && turn.PromptTokens is { } used && used > w * 4 / 5
            ? $" The prompt also used {used} of this model's {w} tokens, so raising num_ctx may help."
            : "";

        return thought > 0
            ? $"The model spent the whole turn reasoning ({thought:N0} characters of it) and "
              + "produced no answer and no tool call. Turn Thinking off in Settings, or use a "
              + "model that answers as well as reasons." + tight
            // What was OBSERVED first, then the causes - and reasoning is named as ruled
            // out rather than led with, because the provider reported none and saying
            // "a reasoning model does this" over evidence to the contrary is the habit
            // the rest of this engine exists to break.
            : $"The model returned nothing{spent} — no text, no tool call, and no reasoning "
              + "either — so its output never reached the engine. The usual cause is a model "
              + "that cannot emit tool calls in the format the provider expects: a small "
              + "local model often answers with something the provider then drops. Tick "
              + "\"Capture raw wire (Trace)\" in the log window and run it again to see "
              + "exactly what came back, or use a model known to call tools." + tight;
    }

    /// <summary>
    /// A step that said it cannot go on (report_blocked) ends blocked, once the calls of its turn are answered. The
    /// report is advisory: what the engine finds itself is recorded as the cause, with the step's word beside it -
    /// a permission refused and nothing made good, or nothing it looked for there (<see cref="OpenFailures.Block"/>);
    /// then a tool the run kept back that the report names, which is the engine's fact too, since it was the engine
    /// that kept it back. Only where the engine sees nothing is the step's word the cause, and recorded as its word.
    /// </summary>
    public IEnumerable<WorkEvent> ReportedBlocked(string reported, ToolOffer offer, Verdict verdict)
    {
        var (cause, why) = Open.Block is { } found
            ? (found.Cause, found.Reason + "; " + reported)
            : KeptBackAndNamed(offer, reported) is { } keptBack
                ? (OutcomeCause.BlockedPermission, $"needs a tool this run does not offer: {keptBack}; {reported}")
                : (OutcomeCause.BlockedReported, reported);
        yield return Ev(EventKind.ErrorObserved, "Blocked: " + why);
        verdict.Ends(StepOutcomeKind.Blocked, why, cause);
    }

    /// <summary>
    /// The tools the run kept back that the report names, with why each was kept back - or null when it names none.
    /// By whole word and whatever the case: "write_file" in a sentence, not "file" inside it. Only the tools the
    /// report names - a step in a run without git that waits on a person's answer is not blocked by git.
    /// </summary>
    private static string? KeptBackAndNamed(ToolOffer offer, string report)
    {
        var named = offer.Withheld.Where(held => System.Text.RegularExpressions.Regex.IsMatch(report,
            $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(held.Name)}(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToArray();
        return named.Length == 0 ? null : new ToolOffer([], named).Because;
    }

    private WorkEvent Ev(EventKind kind, string summary)
        => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary,
               stepNo is { } n ? $"{{\"step\":{n}}}" : null);
}

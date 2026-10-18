namespace Enactive.Agents;

using System.Runtime.CompilerServices;
using Enactive.Core;
using static Enactive.Agents.ToolCallParsing;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// Whether a turn ends the step, and how: carry on with its calls, carry on after being told what it still owes, or
/// stop - done, unfinished, blocked or failed.
///
/// <para><b>Why one module.</b> The rules are a list whose ORDER is the rule: a step that was to hand its result on
/// is reminded before any verdict on the calls it left open (run 4f1d97: three steps that had written their findings
/// never heard the reminder, because the verdict on open calls came first); a block the engine can see ends the step
/// before it is told about its open calls. The order and the once-only flags lived in the 1800-line tool loop as its
/// locals, where the only way to reach them was to script a whole run. Here they are one ordered list, asked once per
/// turn.</para>
///
/// <para><b>How often a step is told</b> (Plan Phase 2). The four reminders - re-send a described call, the step's
/// criteria, its result, its open calls - were once each, by a flag apiece, so a reminder that worked in part was
/// followed by the end of the step. They now ask one <see cref="ObligationLoop"/>, which tells again while what the
/// engine sees of the step changes, within <paramref name="nudges"/>; its default is once per kind, as before. When
/// the loop lets an obligation go, its rule ends the step as it always did. The first reminder of each kind is given as
/// before; nothing beyond it is told to a step the engine sees is blocked, or with a prompt too near the model's window
/// for another turn (see Ask). The loop lives as long as this object - one attempt at the step.</para>
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
/// <param name="nudges">How often the step may be told what it owes - the run's attended or unattended pair
/// (EngineOptions.ObligationNudges). Null: the shipped default, once per kind.</param>
internal sealed class StepEnding(
    StepFrame frame,
    Func<string, CancellationToken, Task<string?>> readFile,
    IReadOnlyList<SuccessCriterionDefinition>? stepCriteria = null,
    bool reviewed = false,
    ObligationNudgeLimits? nudges = null)
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
    public OpenFailures Open => frame.Open;

    // How many times this attempt has been told what it owes, of each kind and in all, and from where (ObligationLoop).
    // One per attempt: a step tried again after a review is told afresh, and a handover inside the attempt - which
    // keeps this object - does not buy it the reminders it has had. The output reminder's flag used to sit on the step's
    // output slot and outlive the attempt while the other three did not; a test found nothing that relied on it. That is
    // the one intended difference from the engine before the loop at the default limits: a step retried after a review
    // that has still handed nothing on is reminded again - one more turn per retry - where it used to end unfinished
    // untold.
    // Without a cap per kind a model that keeps explaining a call instead of making it would burn every iteration on
    // the same re-ask; the loop's per-kind limit is that cap.
    private readonly ObligationLoop _loop = new(nudges ?? ObligationNudges.Default.Attended);
    // Set when the engine asks for a call to be re-sent properly; the calls that arrive on the
    // NEXT turn are what that question bought, and are recorded as such.
    private bool _resendAsked;
    // Amendment A: what is still open after the step was told of it, if a reviewer is to judge the step, goes to it
    // marked - the text of it, so a call that fails afterwards is not waved through.
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
        // The step's own criteria, decided once per turn and read by every rule below that needs them - a rule that
        // tells the step, the position the loop compares, and the last look as the step ends.
        (CriterionResult[] Results, string[] Ids)? failingNow = null;
        CriterionResult[] Failing() => (failingNow ??= FailingCriteria()).Results;
        ObligationSignature Position() => Signature(turn, (failingNow ??= FailingCriteria()).Ids);

        // A step that has said it is done and repeats a call it has already made, with nothing changed since, is
        // done - not stuck. Run fba4d6, 2026-09-29: "S3 done - all 133 tests passed", and the same test run
        // attached, four turns running; its reasoning said "I need to stop repeating", and the step was stopped as
        // stuck, the report step after it skipped. The repeat is not run - it would say what it said - and the step
        // ends on its message by the ordinary road: the same end-of-step checks, and the review.
        if (!turn.OnlyHandOn && !string.IsNullOrWhiteSpace(turn.ReplyText) && calls is { Count: > 0 } && frame.Progress.OnlyRepeats(calls))
        {
            foreach (var call in calls)
                frame.Messages.Add(ChatMessage.Tool(call.Id, "NOT RUN: this exact call already ran in this step, and nothing has "
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
            frame.Messages.Add(ChatMessage.User($"Nothing was handed on. Carry on with the step, and hand its result on with "
                + $"{StepOutputContract.ToolName} when you have it."));
            yield return Ev(EventKind.ContextAssembled, $"The turn for {StepOutputContract.ToolName} was answered with text; the step carries on.");
            yield break;
        }

        if (turn.Described is { } described)
        {
            var asked = Ask(Obligation.DescribedCall, turn, Position);
            if (asked.Nudge)
            {
                _resendAsked = true;
                frame.Messages.Add(ChatMessage.User(
                    $"Your reply described a '{described.Name}' call in plain text instead of invoking it. "
                    + "Nothing was executed. If you meant to act, send it again as a real tool call. "
                    + "If that JSON was only an example or an explanation, reply with your final answer."));
                yield return Ev(EventKind.ErrorObserved,
                    $"The model described a '{described.Name}' call in plain text instead of invoking it — "
                    + "nothing was executed; asked it to re-send the call properly. " + ObligationLoop.Told(asked));
                yield break;
            }
            if (asked.Announce)
                yield return Ev(EventKind.ContextAssembled, ObligationLoop.NotToldAgain(Obligation.DescribedCall, asked));
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
        // the run failed on the missing file five seconds later. As an ordinary turn, while the loop allows; nothing is
        // taken from prose, and what still fails after it is shown to the reviewer as the engine's own finding.
        if (Failing() is { Length: > 0 } failing)
        {
            var asked = Ask(Obligation.Criteria, turn, Position);
            if (asked.Nudge)
            {
                frame.Messages.Add(ChatMessage.User("Before this step ends: the plan checks this step's work, and the engine finds "
                    + (failing.Length == 1 ? "this fails" : "these fail") + ":\n"
                    + string.Join("\n", failing.Select(r => $"- {r.Name}: {r.Detail}"))
                    + "\nMake the work meet " + (failing.Length == 1 ? "it" : "them") + " - the path and the text are the plan's, "
                    + "not a suggestion - or say plainly why that cannot be done."));
                yield return Ev(EventKind.ContextAssembled, $"The plan's criteria for this step fail as it ends: "
                    + string.Join("; ", failing.Select(r => $"{r.Name} ({r.Detail})")) + ". " + ObligationLoop.Told(asked));
                yield break;
            }
            if (asked.Announce)
                yield return Ev(EventKind.ContextAssembled, ObligationLoop.NotToldAgain(Obligation.Criteria, asked));
        }

        // A step that was to hand its result on and has not is reminded before any verdict -
        // including the one on calls still open, which used to end the step first: in run 4f1d97
        // three steps that had written their findings never heard the reminder. It is an ordinary
        // turn: limits, budget and cancellation apply to it as to any other, and it makes nothing
        // that did not finish into something that did.
        if (frame.OutputSchema is not null && frame.OutputSlot is { Values: null } slot)
        {
            var asked = Ask(Obligation.Output, turn, Position);
            if (asked.Nudge)
            {
                frame.Messages.Add(ChatMessage.User(
                    $"This step is not finished until it hands its result on with {StepOutputContract.ToolName}. "
                    + "Call it now with the step's result: "
                    + string.Join(", ", frame.OutputSchema.Fields.Where(f => f.Required).Select(f => f.Name)) + "."
                    + Refused(slot)
                    + (Open.Count > 0
                        ? " These calls are still open and keep the step unfinished: " + Open.Describe()
                        : "")));
                // The criteria nudge and the open-calls nudge say so on the timeline; this one changed what the model
                // was asked and left no trace, so a step that turned on it could not be read back from the events.
                yield return Ev(EventKind.ContextAssembled, $"The step has not handed its result on with {StepOutputContract.ToolName}. "
                    + ObligationLoop.Told(asked));
                yield break;
            }
            if (asked.Announce)
                yield return Ev(EventKind.ContextAssembled, ObligationLoop.NotToldAgain(Obligation.Output, asked));
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

            // Told before any verdict (amendment A). Run 0cf51c, 2026-09-29: a command written for bash
            // failed under cmd.exe, the step ran it again rightly spelled two seconds later and it passed, and
            // the step - never told the first was still open - ended INCOMPLETE on it, two steps skipped.
            var asked = Ask(Obligation.OpenCalls, turn, Position);
            if (asked.Nudge)
            {
                frame.Messages.Add(ChatMessage.User("Before this step ends: "
                    + (Open.Count == 1 ? "this call" : $"these {Open.Count} calls")
                    + " did not go through, and nothing since has made "
                    + (Open.Count == 1 ? "it" : "them") + " good:\n" + unresolved
                    + "\nMake each good - run it again, corrected - or, if this step's result does not depend on it, "
                    + "say so and why in your closing message."));
                yield return Ev(EventKind.ContextAssembled, $"{Open.Count} call(s) still open as the step ends: {unresolved}. "
                    + ObligationLoop.Told(asked));
                yield break;
            }
            if (asked.Announce)
                yield return Ev(EventKind.ContextAssembled, ObligationLoop.NotToldAgain(Obligation.OpenCalls, asked));

            // Still open, and a reviewer is to judge the step: it goes to the review with them named, as the
            // engine's own finding - the reviewer decides whether the result stands without them. Without a
            // reviewer, nothing can, and the step is unfinished as before.
            if (reviewed)
            {
                _openCallsForReview = unresolved;
                frame.Journal.Record(frame.StepNo, "engine_open_calls", "{}", ActionOutcome.Succeeded,
                    "Checked by the engine as this step ended: calls that did not go through and that nothing made good, "
                    + (_loop.TimesTold(Obligation.OpenCalls) == 0 ? "without the step being told of them"
                        : $"after the step was told of them {Times(Obligation.OpenCalls)}") + ":\n" + unresolved
                    + "\nJudge whether the step's result stands without them. A result that depends on one of them is not "
                    + "shown to be done; one that does not - a mistyped command made good by a different one - may stand.",
                    WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
                yield return Ev(EventKind.ErrorObserved, $"Finished with {Open.Count} call(s) not made good, "
                    + $"{AfterBeingTold(Obligation.OpenCalls)}: " + unresolved + " - the review decides whether the result stands without them.");
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

        // A step that was to hand its result on as values and has not: told, with the
        // fields, and then not called finished - the steps after it would have nothing.
        if (frame.OutputSchema is not null && frame.OutputSlot is { Values: null })
        {
            verdict.Ends(StepOutcomeKind.Incomplete,
                $"the step finished without handing on its declared output ({StepOutputContract.ToolName}), "
                + "so the steps after it would have nothing to work from");
            yield return Ev(EventKind.ErrorObserved, "The step finished without submitting its declared output.");
            yield break;
        }

        // The plan's criteria for this step, looked at once more now that the step was told of them and ended anyway.
        // Without this the step fell through to Succeeded with nothing said: a reviewer, when there is one, is shown
        // the failure by the review itself (Orchestrator.Review), but with none nothing ever saw it - a step whose
        // own check still failed counted as done. Last on purpose, after every rule that can still tell the step
        // something or end it for a reason of its own: the output nudge and the open calls come first, so this
        // pre-empts neither, and it asks for no further turn - the loop above let the criteria go before this is reached,
        // whether it told the step of them or a rule kept it from telling.
        if (Failing() is { Length: > 0 } stillFailing)
        {
            var named = string.Join("; ", stillFailing.Select(r => $"{r.Name} ({r.Detail})"));
            if (reviewed)
            {
                // No journal entry here: the review records the same check as the engine's own finding.
                yield return Ev(EventKind.ErrorObserved, "The plan's criteria for this step still fail "
                    + (_loop.TimesTold(Obligation.Criteria) == 0 ? "without the step being told" : $"after the step was told {Times(Obligation.Criteria)}")
                    + ": " + named + " - the review decides, with them shown as the engine's own finding.");
            }
            else
            {
                var reason = "the plan's criteria for this step still fail: " + named;
                yield return Ev(EventKind.ErrorObserved, "Finished with the plan's criteria for this step still failing, "
                    + $"{AfterBeingTold(Obligation.Criteria)}: " + named);
                verdict.Ends(StepOutcomeKind.Incomplete, reason);
                yield break;
            }
        }

        verdict.Ends(StepOutcomeKind.Succeeded, null); // genuine final answer - no tool calls
    }

    /// <summary>
    /// Whether to tell the step of <paramref name="kind"/> now. The first reminder of each kind is the loop's alone, as it
    /// always was; a further one that the limit per kind allows is first held back by what keeps any step from being told
    /// again - a block, which waits on a person as the work AI-DLC's Stop hook lets go of does, and the context guard - and
    /// then left to the loop.
    ///
    /// <para><b>Why only beyond the first.</b> The first reminder is what the engine has always given, and what it bought
    /// is measured: a step with a declared output and one refused call - a block, as the engine reads it - was reminded,
    /// handed on, had the refusal forgiven by the hand-over and ended done (run dd7ca94b); a step that only looked for a
    /// file it was to write, and found nothing - a block too - was told of the plan's criterion and wrote it (run 68f92f).
    /// Held back from them, both ended blocked at once. And the first reminder on a full window is the turn the trim and
    /// the handover exist for. What the carve-outs and the guard stop is the turns the loop adds.</para>
    ///
    /// <para>No carve-out for a spent turn budget: the tool loop ends the step on the budget before its next model call,
    /// and a reminder given now costs nothing but a line in a conversation that is about to end. Letting go here instead
    /// ended a step on what it owed - a described call fell through to done - where the budget was the reason.</para>
    /// </summary>
    private ObligationDecision Ask(Obligation kind, Turn turn, Func<ObligationSignature> position)
    {
        // After the limit per kind, not before it: a kind its limit lets go is let go by the limit, silently, and a
        // carve-out that happens to hold as well is not named as the reason (ObligationLoop.MayAdd).
        if (_loop.MayAdd(kind))
        {
            // No carve-out for a step waiting for a person's answer: no such step reaches its ending - a parked question
            // unwinds the step by exception, an attended one is answered inside the call that asked it.
            // Blocked (Phase 7.1): a permission refused, or nothing the step looked for there - for a person to put right.
            if (Open.Block is { } block)
                return _loop.Release(kind, "the step is blocked (" + block.Reason + "), which is for a person to put right");
            if (Crowded(turn) is { } full)
                return _loop.Release(kind, full);
        }
        return _loop.Nudge(kind, position());
    }

    /// <summary>
    /// The context guard: a reminder refused when the prompt is past four fifths of the window the provider stated. Every
    /// reminder is a whole turn on top of a prompt already that size - the reminder, the model's answer (an eighth of the
    /// window is the default reserve for it) and the calls it makes - and with a local model at 8k num_ctx that turn is
    /// the one that fills the window and ends the step on "window full" instead of on what it owed. Four fifths is the
    /// line the engine already reads as a window filling up: a turn cut at its limit above it is not tried again
    /// (Orchestrator), and NothingCameBack names num_ctx only above it. Not EvidenceBudget, which is what the reviewer is
    /// shown. No window stated, no guess: the loop's ceiling alone applies.
    /// </summary>
    private static string? Crowded(Turn turn)
        => turn.PromptTokens is { } used && turn.ContextWindow() is { } window and > 0 && used > window * 4 / 5
            ? $"the prompt used {used} of the model's {window} tokens, too near its window for another turn"
            : null;

    /// <summary>Where the step stands on what it owes, from the engine's own state alone (ObligationSignature).</summary>
    private ObligationSignature Signature(Turn turn, IReadOnlyList<string> failing)
        => new(frame.StepNo,
            failing,
            OutputOwed: frame.OutputSchema is not null && frame.OutputSlot is { Values: null },
            MissingFields: frame.OutputSlot?.MissingFields ?? [],
            InvalidFields: frame.OutputSlot?.InvalidFields ?? [],
            OpenCalls: Open.Keys,
            Described: turn.Described is { } described ? described.Name + " " + TaskProgress.Canonical(described.ArgumentsJson) : null);

    /// <summary>
    /// The criteria the plan attached to this step that fail now - none when it has none, or is no plan step - each with
    /// an id: its place among the step's own criteria, with its name. A name alone is not one: two criteria named alike
    /// would be one position, and the step that made one of them good would not have moved.
    /// </summary>
    private (CriterionResult[] Results, string[] Ids) FailingCriteria()
    {
        if (stepCriteria is not { Count: > 0 } || frame.StepNo is not { } planNo) return ([], []);
        var failing = TypedCriteria.OfStep(stepCriteria, planNo - 1, frame.WorkspaceRoot)
            .Select((r, at) => (Result: r, Id: $"{at}:{r.Name}"))
            .Where(c => c.Result.Outcome == CriterionOutcome.Failed).ToArray();
        return (failing.Select(c => c.Result).ToArray(), failing.Select(c => c.Id).ToArray());
    }

    /// <summary>What the last refused hand-over lacked, for the reminder - the refusal said it once, in a tool result further up.</summary>
    private static string Refused(StepOutputSlot slot)
        => (slot.MissingFields.Count, slot.InvalidFields.Count) switch
        {
            (0, 0) => "",
            (_, 0) => $" The last submission was not stored: it left out {string.Join(", ", slot.MissingFields)}.",
            (0, _) => $" The last submission was not stored: {string.Join(", ", slot.InvalidFields)} were not accepted.",
            _ => $" The last submission was not stored: it left out {string.Join(", ", slot.MissingFields)}, and "
                 + $"{string.Join(", ", slot.InvalidFields)} were not accepted."
        };

    /// <summary>How often the step was told of <paramref name="kind"/>, for the sentence that says it ended anyway.</summary>
    private string Times(Obligation kind) => _loop.TimesTold(kind) switch
    {
        1 => "once",
        2 => "twice",
        var n => $"{n} times"
    };

    /// <summary>"after being told once", or "without being told" for a kind a limit of 0 let go untold.</summary>
    private string AfterBeingTold(Obligation kind)
        => _loop.TimesTold(kind) == 0 ? "without being told" : "after being told " + Times(kind);

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
            ? $"The model spent the whole turn reasoning ({Invariant.N0(thought)} characters of it) and "
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
        => new(Guid.NewGuid(), frame.TaskId, frame.RunId, DateTimeOffset.UtcNow, kind, summary,
               frame.StepNo is { } n ? $"{{\"step\":{n}}}" : null);
}

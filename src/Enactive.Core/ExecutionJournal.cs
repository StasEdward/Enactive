namespace Enactive.Core.Execution;

using System.Text;

/// <summary>How a tool call ended. Never ran is not the same as ran and failed.</summary>
public enum ActionOutcome
{
    /// <summary>It ran and the tool reported success.</summary>
    Succeeded,

    /// <summary>It ran and the tool reported failure — a non-zero exit, an edit that did not apply.</summary>
    Failed,

    /// <summary>It never ran: the role does not offer it, the policy blocked it, the user said no.</summary>
    Refused,

    /// <summary>
    /// It ran, produced nothing, and that IS the result: a file that is not there, an offset past
    /// the end of one. See <see cref="ToolResult.IsAnswer"/>.
    ///
    /// <para>Recorded apart from <see cref="Failed"/> because the reviewer reads this and is told to
    /// fail work whose report does not account for an error. On 2026-09-07 21:43 it did exactly
    /// that — <i>"the evidence shows errors for offsets 800 and 400 being past the end of the file.
    /// The agent's report does not account for these errors"</i> — over two reads the engine had
    /// already, correctly, stopped counting against the step. Forgiving something in one half of
    /// the system and holding it against the work in the other is worse than either alone.</para>
    /// </summary>
    Answered
}

/// <summary>
/// One tool call, as it actually happened.
/// </summary>
/// <param name="Arguments">Compacted for reading — this is evidence, not a replay log.</param>
public sealed record ExecutedAction(
    DateTimeOffset At,
    int? Step,
    string Tool,
    string Arguments,
    ActionOutcome Outcome,
    string? Output);

/// <summary>
/// What a step actually did, written down when it did it.
///
/// <para>The reviewer is asked to judge a step against "the ACTUAL commands run and their real
/// stdout/stderr/exit codes", and that evidence was assembled by reading back the model's own
/// conversation. Two things are wrong with reading it there. The conversation is the model's
/// WORKING MEMORY, and once it has to be shortened to fit a context window the tool results are
/// replaced by a stub — so on exactly the runs under most pressure, the reviewer was handed less and
/// never told. And a transcript is a record of what was SAID; what was DONE is a different fact that
/// happens to have been written in the same place.</para>
///
/// <para>So it is recorded separately, at the moment each call returns, and nothing that shortens a
/// prompt can touch it. A refusal is recorded too: "the user did not permit this" is exactly the
/// sort of thing a reviewer must not have to infer from silence.</para>
/// </summary>
public sealed class ExecutionJournal
{
    private readonly List<ExecutedAction> _actions = new();
    private readonly object _gate = new();
    private readonly bool _spansSteps;

    /// <summary>
    /// One journal for one CONVERSATION.
    /// </summary>
    /// <param name="spansSteps">
    /// True when this journal covers a whole run's plan, because the plan shares one conversation
    /// (one step at a time). The evidence then says so and every call names its step, since a step
    /// may legitimately answer from what an earlier one did. False - the default - is a journal that
    /// covers a single unit of work: a quick action, or one step of a run whose steps were forked.
    ///
    /// <para>Passed in rather than inferred from the actions present. A step that made no calls of
    /// its own is exactly the case that matters, and there is nothing in the data to infer it from:
    /// the caller knows whether the window is the run's, and guessing produced a header that said
    /// "in this step" over another step's calls.</para>
    /// </param>
    public ExecutionJournal(bool spansSteps = false) => _spansSteps = spansSteps;

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<ExecutedAction> Actions
    {
        get { lock (_gate) return _actions.ToArray(); }
    }

    /// <summary>
    /// A position to describe from later. A review judges the CURRENT attempt, so a retry starts
    /// from here rather than re-reading what an earlier, rejected attempt did.
    /// </summary>
    public int Mark()
    {
        lock (_gate) return _actions.Count;
    }

    public void Record(int? step, string tool, string arguments, ActionOutcome outcome, string? output)
    {
        lock (_gate)
            _actions.Add(new ExecutedAction(DateTimeOffset.UtcNow, step, tool, arguments, outcome, output));
    }

    /// <summary>
    /// Forget everything from <paramref name="from"/> on, because the conversation just did.
    ///
    /// <para>A CONTENT review that rejects an attempt DISCARDS it: the draft comes out of the
    /// transcript and the retry starts from where the attempt began. The evidence has to lose the
    /// same calls, or the reviewer is judging an answer against work the model can no longer see.
    /// That is §9f's rule - the evidence window is the transcript window - made structural instead
    /// of maintained by hand: the two are truncated by the same event.</para>
    ///
    /// <para>Only safe where there is ONE conversation writing to this journal. A run above degree
    /// one gives every step its own fork and its own journal for exactly that reason.</para>
    /// </summary>
    public void Discard(int from)
    {
        lock (_gate)
        {
            var at = Math.Max(0, from);
            if (at < _actions.Count)
                _actions.RemoveRange(at, _actions.Count - at);
        }
    }

    /// <summary>
    /// Says, once and for the rest of the run, that the conversation this journal describes began
    /// before the journal did.
    ///
    /// <para>A resumed run restores the interrupted run's transcript and starts a fresh journal, so
    /// the agent can see work whose calls are not here. Everything else in this engine that shows
    /// less than the whole says so - a shortened result, an excerpted memory - and this is the same
    /// obligation: a reviewer that is not told will read the gap as a fabrication.</para>
    /// </summary>
    public void NotePriorTranscript() => _resumed = true;

    private volatile bool _resumed;

    /// <summary>Whether anything recorded from <paramref name="from"/> on used one of these tools.</summary>
    public bool UsedAny(IReadOnlyCollection<string> tools, int from = 0)
    {
        lock (_gate)
            for (var i = Math.Max(0, from); i < _actions.Count; i++)
                foreach (var tool in tools)
                    if (string.Equals(_actions[i].Tool, tool, StringComparison.OrdinalIgnoreCase))
                        return true;

        return false;
    }

    /// <summary>
    /// The default size of the whole evidence block, shared between every call's output.
    ///
    /// <para>A DEFAULT and not a rule: it is divided among the calls, so what it buys per call
    /// depends entirely on how many the step made. Three reads get a usable slice each; thirteen get
    /// about 320 characters each, which is less than a source file needs to settle anything quoted
    /// from it. The setting that overrides it exists because that number is a property of the work,
    /// not of the engine.</para>
    /// </summary>
    public const int DefaultBudget = 6000;

    /// <summary>
    /// Below this the block cannot hold its own header, so a smaller setting is raised to it rather
    /// than producing an evidence list with nothing in it.
    /// </summary>
    public const int MinimumBudget = 1500;

    /// <summary>
    /// The evidence, in the shape the reviewer prompt has always been written for: what was asked,
    /// then what came back.
    ///
    /// <para><b>Every call is listed. Only the OUTPUTS are shortened.</b> This used to build the
    /// whole thing and then cut the tail at 3000 characters, which meant one long result at the
    /// start ate the budget and every call after it vanished — from the evidence, not from the run.
    /// On 2026-09-07 a step listed a directory, read the README, and then read five source files;
    /// the reviewer was shown the first two and correctly concluded, by its own instructions, that
    /// "no source files in the src/ directory were ever read". It failed work that had been done,
    /// three times, and the run died with the second step skipped.</para>
    ///
    /// <para>That is §8i again in the one place built to prevent it. The journal fixed WHERE the
    /// evidence comes from and left the same hole in how it is rendered: a record shortened at the
    /// front, judged as if it were whole.</para>
    ///
    /// <para>So the budget is split. A call line is a dozen characters and is what proves the call
    /// happened; a result is bulky and is only supporting detail. Each action keeps its call line
    /// and gets an equal share of what is left for its output, and a shortened output says how much
    /// of it is shown. If even the call lines do not fit, the newest are kept and the number of
    /// older ones is stated — an evidence list that quietly stops is the whole defect.</para>
    /// </summary>
    public string Describe(int from = 0, int maxChars = DefaultBudget)
    {
        ExecutedAction[] slice;
        lock (_gate)
            slice = _actions.Skip(Math.Max(0, from)).ToArray();

        if (slice.Length == 0)
            return "(no tools were run in this step)";

        // The count first, as a fact rather than something to infer by counting arrows. A reviewer
        // that can compare "nine calls" against what it can see can tell a short list from a
        // shortened one.
        // Said ONCE. Per result it cost eighty-five characters times the number of calls, which is
        // the budget the calls were rescued from - a notice that crowds out what it is annotating.
        // At one step at a time the whole plan shares ONE conversation, so a step's answer may draw
        // on what an earlier step read - and the evidence has to span the same ground, or an honest
        // report reads as a fabrication. That is §9f between steps instead of between attempts.
        // Above degree one each step gets its own fork and its own journal, and the slice is one
        // step's again.
        //
        // Two different facts, and the first version of this conflated them into one flag.
        //
        // WHOSE window this is comes from the caller: a run-wide journal means the window is the
        // run's whether or not more than one step has run yet. HOW MANY steps are actually in it is
        // a property of the slice. Deriving the second from the first printed "They span more than
        // one step" over thirteen calls that were all step 1's - an overclaim in the one place built
        // to stop the engine overclaiming. Reported 2026-09-08 17:31.
        var runWide = _spansSteps && slice.Any(a => a.Step is not null);
        var manySteps = runWide && slice.Select(a => a.Step).Distinct().Count() > 1;

        var header = $"{slice.Length} tool call(s) in "
                   + (runWide ? "this run so far" : "this step")
                   + $", oldest first{Tally(slice)}, each numbered [n] so it can be referred to."
                   + (runWide
                       ? " These are the calls made so far in this RUN, not only in the step under "
                         + "review: at one step at a time the whole plan shares a conversation, so a "
                         + "report that draws on an earlier step's work is not inventing it."
                       : "")
                   + (manySteps ? " Each call says which step made it." : "")
                   + (_resumed
                       ? " This run RESUMED an interrupted one, whose transcript the agent can also "
                         + "see; the calls it made are not in this list. A claim about work done "
                         + "before the interruption is not evidence of fabrication."
                       : "")
                   + " A result too long to show keeps its START and its END, with the cut marked "
                   + "between them — so a command's closing summary is always here; the call it "
                   + "belongs to still happened.";

        // Numbered from 1 WITHIN THIS SLICE, not within the run. The number is a handle for a
        // reviewer that is asked to point at a call - see ProofAudit - and it can only point at what
        // it was shown. A number that meant a position in the whole journal would refer to calls
        // this evidence does not contain, and an audit checking it would be checking the wrong list.
        var calls = slice.Select((a, i) => Call(a, i + 1, manySteps)).ToArray();

        // How many can be shown AT ALL. Each costs its call line plus the floor under its output -
        // budgeting the call lines alone was the first version of this and it overran by a factor of
        // twelve, because the floor is paid per action whether or not there is room for it.
        var kept = slice.Length;
        var dropped = 0;
        while (kept > 1 && header.Length + Cost(calls, kept) > maxChars)
        {
            kept--;
            dropped++;
        }

        var note = dropped > 0
            ? $"… the {dropped} oldest call(s) of this step are not shown here.\n"
            : "";

        // What is left over, shared EQUALLY. Equal rather than first-come is the whole point: the
        // old behaviour was first-come, and one README took all of it.
        var spare = maxChars - header.Length - note.Length - Cost(calls, kept);
        var each = MinOutputChars + Math.Max(0, spare) / kept;

        var sb = new StringBuilder(header).AppendLine().Append(note);

        for (var i = slice.Length - kept; i < slice.Length; i++)
        {
            sb.AppendLine(calls[i]);
            sb.Append("<- ").AppendLine(Answer(slice[i], each));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// How the step's calls ended, as a count.
    ///
    /// <para>Reported 2026-09-07 21:54: a step that made four successful edits and one that did not
    /// match was rejected because <i>"the RunEdgeCaseTests method was never actually added … as
    /// indicated by the ERROR in the evidence"</i>. The method had been added — by the LAST of those
    /// edits, whose own output names the line it went in at. One ERROR was read as the state of the
    /// file, with four successes beside it.</para>
    ///
    /// <para>A tally is much harder to misread than a list: "17 worked, 1 failed" cannot be reached
    /// by noticing one line. Same reasoning as the call count itself, which is here because a
    /// shortened list was read as a short one.</para>
    /// </summary>
    private static string Tally(ExecutedAction[] slice)
    {
        var parts = new List<string>();
        Add(ActionOutcome.Succeeded, "worked");
        Add(ActionOutcome.Failed, "failed");
        Add(ActionOutcome.Answered, "found nothing");
        Add(ActionOutcome.Refused, "were refused");

        // Nothing to compare when they all ended the same way, and the count above already says it.
        return parts.Count > 1 ? " — " + string.Join(", ", parts) : "";

        void Add(ActionOutcome outcome, string label)
        {
            var count = slice.Count(a => a.Outcome == outcome);
            if (count > 0)
                parts.Add($"{count} {label}");
        }
    }

    /// <summary>The least the newest <paramref name="kept"/> actions can be shown in.</summary>
    private static int Cost(string[] calls, int kept)
    {
        var total = 0;
        for (var i = calls.Length - kept; i < calls.Length; i++)
            // The call, "<- ", the floor under its output, and the notice that says it was shortened -
            // which has to be paid for or the budget is a number the result does not obey.
            total += calls[i].Length + 1 + MinOutputChars + 4 + ShortenedNoticeChars;
        return total;
    }

    /// <summary>
    /// Enough of a result to recognise what it was, however many calls have to share the budget.
    /// A step with thirty calls gets a thin slice of each, which is the right trade: what the
    /// reviewer must not lose is the LIST.
    /// </summary>
    private const int MinOutputChars = 120;

    /// <summary>
    /// Room reserved for the notice that marks a shortened result.
    ///
    /// <para>Reserved because it has to be PAID for: it is printed inside the space the output was
    /// given, so a reserve smaller than the notice makes the budget a number the block does not
    /// obey. It was 24 against a notice of about 65, and three tests measuring the block against its
    /// budget only passed on the slack left over — until a longer notice used the slack up and they
    /// failed, which is what they are for.</para>
    /// </summary>
    private const int ShortenedNoticeChars = 72;

    /// <summary>
    /// The call itself. Arguments are clipped because write_file carries a whole file.
    /// </summary>
    /// <param name="withStep">
    /// Whether to say which step made it. Only when the slice spans several - one step's evidence
    /// would be repeating the same number on every line, and the reviewer was told the step already.
    /// </param>
    private static string Call(ExecutedAction action, int number, bool withStep = false)
    {
        const int maxArguments = 300;
        var arguments = action.Arguments ?? "";
        if (arguments.Length > maxArguments)
            arguments = arguments[..maxArguments] + $"… ({arguments.Length:N0} characters of arguments)";

        var whose = withStep && action.Step is { } step ? $" (step {step})" : "";
        return $"[{number}]{whose} -> {action.Tool} {arguments}";
    }

    /// <summary>
    /// The action a call number in this slice refers to, or null when the number is not one of them.
    ///
    /// <para>The counterpart of the numbering above, and the reason it exists: a reviewer asked to
    /// point at the call that proves something answers with a number, and the ENGINE resolves it
    /// against what actually happened. A number nothing answers to is a citation of a call that was
    /// never made - which is worth knowing about a proof.</para>
    /// </summary>
    public ExecutedAction? Cited(int number, int from = 0)
    {
        lock (_gate)
        {
            var index = Math.Max(0, from) + number - 1;
            return number >= 1 && index < _actions.Count ? _actions[index] : null;
        }
    }

    /// <summary>How many calls a slice has - the range a citation may name.</summary>
    public int CountFrom(int from = 0)
    {
        lock (_gate)
            return Math.Max(0, _actions.Count - Math.Max(0, from));
    }

    private static string Answer(ExecutedAction action, int budget)
    {
        var text = action.Outcome switch
        {
            ActionOutcome.Refused => "REFUSED: " + (action.Output ?? "not permitted"),
            ActionOutcome.Failed => "ERROR: " + (action.Output ?? "failed"),
            // Not "ERROR". The call worked and the answer is that there is nothing there — which is
            // information, and reads as a defect only if it is labelled as one.
            ActionOutcome.Answered => "NOTHING THERE: " + (action.Output ?? "nothing to return"),
            _ => action.Output ?? "OK"
        };

        return text.Length <= budget ? text : HeadAndTail(text, budget);
    }

    /// <summary>
    /// A shortened result: the START and the END of it, with the cut marked between them.
    ///
    /// <para>Keeping the first N characters is the obvious implementation and it is wrong for the
    /// results that matter most. A command puts its restore and build noise first and its VERDICT
    /// last, so head-only shortening reliably hands over the part with no answer in it. Reported
    /// 2026-09-07 22:22, twice in one run, and the reviewer named the cause itself: <i>"the provided
    /// tool output for 'dotnet test' is truncated and does not contain these specific numbers. The
    /// agent fabricated the test summary."</i> The numbers were real — <c>Passed! Failed: 0, Passed:
    /// 22</c> — and they were in the line after the cut.</para>
    ///
    /// <para>Weighted to the end for the same reason the log analyst weights its excerpt that way:
    /// the head says what was attempted, the tail says how it went, and only one of those is what a
    /// reviewer is being asked about.</para>
    /// </summary>
    private static string HeadAndTail(string text, int budget)
    {
        // Enough head to recognise WHAT ran; the rest to the end, which is where the answer is.
        var head = Math.Max(1, budget * 2 / 5);
        var tail = budget - head;

        // Below this there is no room for two pieces and a marker between them, and half a marker
        // is worse than a clean cut.
        if (tail < 40)
            return text[..budget] + $"… ({budget:N0} of {text.Length:N0})";

        // "NOT SHOWN", not "cut". On 2026-09-08 17:31 a reviewer read "1,645 characters cut from the
        // middle" as evidence that the file did not contain what the agent had quoted from it - and
        // failed the step twice for a value that was in those 1,645 characters. The rule that
        // follows from it belongs in the reviewer's instructions, where it is said ONCE; per result
        // it would cost its own length times the number of calls, which is the budget the calls were
        // rescued from. What the notice itself can do is stop reading as an absence.
        return text[..head]
             + $"\n… ({text.Length - budget:N0} characters not shown here; the end follows) …\n"
             + text[^tail..];
    }
}

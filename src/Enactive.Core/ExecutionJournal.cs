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
    public string Describe(int from = 0, int maxChars = 6000)
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
        var header = $"{slice.Length} tool call(s) in this step, oldest first. A result ending "
                   + "\"… (N of M)\" was shortened to fit; the call it belongs to still happened.";
        var calls = slice.Select(Call).ToArray();

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

    /// <summary>Room reserved for the "… (N of M)" that marks a shortened result.</summary>
    private const int ShortenedNoticeChars = 24;

    /// <summary>The call itself. Arguments are clipped because write_file carries a whole file.</summary>
    private static string Call(ExecutedAction action)
    {
        const int maxArguments = 300;
        var arguments = action.Arguments ?? "";
        if (arguments.Length > maxArguments)
            arguments = arguments[..maxArguments] + $"… ({arguments.Length:N0} characters of arguments)";

        return $"-> {action.Tool} {arguments}";
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

        return text.Length <= budget
            ? text
            : text[..budget] + $"… ({budget:N0} of {text.Length:N0})";
    }
}

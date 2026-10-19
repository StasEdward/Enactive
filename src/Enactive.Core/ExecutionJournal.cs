namespace Enactive.Core.Execution;

using Enactive.Core.Tools;

using System.Text;
using System.Text.Json;
using System.Collections.Frozen;

/// <summary>
/// One immutable rendering of journal evidence. Citation IDs are one-based positions in this
/// snapshot's slice, preserved when older actions are omitted. The full slice is retained only
/// for engine checks, such as whether the workspace changed; it does not make hidden calls citable.
/// </summary>
public sealed class EvidenceView
{
    internal EvidenceView(string text, ExecutedAction[] actions, IEnumerable<int> visibleActionIds,
        bool outputsTruncated = false, bool argumentsTruncated = false, bool hasPriorTranscript = false)
    {
        Text = text;
        Actions = Array.AsReadOnly(actions);
        VisibleActionIds = visibleActionIds.ToFrozenSet();
        OutputsTruncated = outputsTruncated;
        ArgumentsTruncated = argumentsTruncated;
        HasPriorTranscript = hasPriorTranscript;
    }

    public string Text { get; }
    public IReadOnlySet<int> VisibleActionIds { get; }
    public bool ActionsOmitted => VisibleActionIds.Count < Actions.Count;
    public bool OutputsTruncated { get; }
    public bool ArgumentsTruncated { get; }
    public bool HasPriorTranscript { get; }
    internal IReadOnlyList<ExecutedAction> Actions { get; }

    /// <summary>Resolve only a call whose numbered entry was included in Text.</summary>
    public ExecutedAction? Cited(int number)
        => VisibleActionIds.Contains(number) ? Actions[number - 1] : null;

    /// <summary>Engine-owned command facts, with IDs from this evidence view, never inferred from prose.
    /// Missing exits remain null. Bounded omissions are explicit and do not grant citation access.</summary>
    /// <param name="after">Only the commands after this call number - for a history that is continued, not rewritten.</param>
    public string CommandHistory(int after = 0)
    {
        var commands = Commands().Where(x => x.id > after).ToArray();
        var visible = commands.Where(x => VisibleActionIds.Contains(x.id)).TakeLast(64).ToArray();
        return JsonSerializer.Serialize(new {
            priorHistoryUnavailable = HasPriorTranscript,
            omittedCommands = commands.Length - visible.Length,
            calls = visible.Select(x => new { call = x.id, step = x.a.Step, tool = x.a.Tool,
                arguments = x.a.Arguments.Length <= 1024 ? x.a.Arguments : x.a.Arguments[..1024],
                argumentsTruncated = x.a.Arguments.Length > 1024,
                outcome = x.a.Outcome.ToString(), exitCode = x.a.ExitCode })
        });
    }

    private IEnumerable<(ExecutedAction a, int id)> Commands()
        => Actions.Select((a, i) => (a, id: i + 1))
            .Where(x => x.a.ExitCode is not null || x.a.Tool is "run_command" or "run_powershell");

    /// <summary>The number of the last command in this view, or 0 when there is none.</summary>
    public int LastCommand() => Commands().Select(x => x.id).DefaultIfEmpty(0).Max();

}

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
/// How a tool call reached the engine. Recorded so the choice between repairing a malformed
/// call and refusing it stops being a matter of opinion.
///
/// <para>The engine currently refuses: a turn with one unparseable call executes nothing, and raw
/// fragments are deliberately not repaired or replayed. That is a deliberate decision, and the
/// argument for changing it needs a number rather than a preference - how many calls a given model
/// emits natively, how many only arrive after the engine recovers or re-asks for them, and how
/// many never arrive at all. Nothing here changes what runs; it records how it got here.</para>
/// </summary>
public enum ToolCallOrigin
{
    /// <summary>The provider returned it as a structured tool call. The only origin that costs nothing.</summary>
    Native,

    /// <summary>Recovered from the reply TEXT by the engine, without asking the model again.</summary>
    Healed,

    /// <summary>It arrived only after the engine asked the model to send the call properly. Costs one turn.</summary>
    Nudged,

    /// <summary>It came in a repeat attempt of a step whose review was rejected.</summary>
    Retry,

    /// <summary>
    /// Made by the engine itself when the step was reviewed - a place the step cited, opened as the workspace
    /// stands (CitedPlaces). Not a call of the model's.
    /// </summary>
    Engine
}

/// <summary>
/// One tool call, as it actually happened.
/// </summary>
/// <param name="Arguments">Compacted for reading — this is evidence, not a replay log.</param>
/// <param name="Named">
/// The short strings the call's FULL arguments named, taken before they were compacted - what coverage reads a mention
/// from. <see cref="Arguments"/> is cut at 120 characters, which leaves JSON that does not parse: a write_file call named
/// nothing, and a call listing twelve paths named none of them. Null for a call recorded without it (older records).
/// </param>
/// <param name="Change">
/// What the call changed in a file, as its tool reported it (<see cref="WrittenChange"/>) - what the evidence shows of a
/// successful write in place of its arguments, whose first 120 characters are an edit's anchor and not its change. Null
/// for a call that changed no file it could compare, and for one that did not succeed: nothing was changed.
/// </param>
/// <param name="FilesRead">
/// The files a read call read and where each one's text is in its result, as its tool reported it (<see cref="FileRead"/>) -
/// what the evidence names a successful read by, every file in full, in place of its arguments cut at 120 characters, and
/// what it says under a shortened result: which lines of each file are shown. Null for a call that read no file it could
/// say this of, and for one that did not succeed.
/// </param>
public sealed record ExecutedAction(
    DateTimeOffset At,
    int? Step,
    string Tool,
    string Arguments,
    ActionOutcome Outcome,
    string? Output,
    WorkspaceEffect WorkspaceEffect = WorkspaceEffect.Unknown,
    IReadOnlyList<string>? ChangedPaths = null,
    int? ExitCode = null,
    bool FileDeletion = false,
    ToolCallOrigin Origin = ToolCallOrigin.Native,
    IReadOnlyList<string>? Named = null,
    WrittenChange? Change = null,
    IReadOnlyList<FileRead>? FilesRead = null);

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
    /// How many calls arrived each way, oldest journal to newest. The number behind the choice
    /// between repairing a malformed call and refusing it: a model whose calls are all
    /// <see cref="ToolCallOrigin.Native"/> is not the one that argument is about.
    ///
    /// <para>Counts every recorded call, including refused ones, because a call the engine had to
    /// ask for twice and then refused anyway is exactly what this is here to make visible.</para>
    /// </summary>
    public IReadOnlyDictionary<ToolCallOrigin, int> OriginTally()
    {
        var tally = new Dictionary<ToolCallOrigin, int>();
        lock (_gate)
            foreach (var action in _actions)
                tally[action.Origin] = tally.GetValueOrDefault(action.Origin) + 1;
        return tally;
    }

    /// <summary>
    /// A position to describe from later. A review judges the CURRENT attempt, so a retry starts
    /// from here rather than re-reading what an earlier, rejected attempt did.
    /// </summary>
    public int Mark()
    {
        lock (_gate) return _actions.Count;
    }

    public void Record(int? step, string tool, string arguments, ActionOutcome outcome, string? output,
        WorkspaceEffect workspaceEffect = WorkspaceEffect.Unknown, IReadOnlyList<string>? changedPaths = null,
        int? exitCode = null, bool fileDeletion = false,
        ToolCallOrigin origin = ToolCallOrigin.Native, IReadOnlyList<string>? named = null, WrittenChange? change = null,
        IReadOnlyList<FileRead>? filesRead = null)
    {
        lock (_gate)
            _actions.Add(new ExecutedAction(DateTimeOffset.UtcNow, step, tool, arguments, outcome, output,
                workspaceEffect, changedPaths is null ? null : Array.AsReadOnly(changedPaths.ToArray()), exitCode, fileDeletion,
                origin, named is null ? null : Array.AsReadOnly(named.ToArray()), change,
                filesRead is null ? null : Array.AsReadOnly(filesRead.ToArray())));
    }

    /// <summary>
    /// An observation of the engine's that supersedes one it recorded earlier: written IN PLACE of
    /// <paramref name="earlier"/> while this journal still holds it, else added at the end. Returns the entry, to be
    /// superseded in its turn.
    ///
    /// <para>For an observation repeated as the step goes on - a write-time check, run every turn of an edit loop - of
    /// which only the latest says anything about the work. The engine's entries are the ones kept longest when the
    /// evidence is shortened (<see cref="KeptLongest"/>), so twenty of them crowded the step's own calls out of the
    /// reviewer's evidence to say nineteen times what the twentieth corrects. In place, and not removed and added:
    /// positions in this journal are marks the loop and the review hold (<see cref="Mark"/>), and the calls after it
    /// keep their numbers.</para>
    /// </summary>
    public ExecutedAction Supersede(ExecutedAction? earlier, int? step, string tool, string arguments, ActionOutcome outcome,
        string? output, WorkspaceEffect workspaceEffect = WorkspaceEffect.None, ToolCallOrigin origin = ToolCallOrigin.Engine)
    {
        var action = new ExecutedAction(DateTimeOffset.UtcNow, step, tool, arguments, outcome, output, workspaceEffect, Origin: origin);
        lock (_gate)
        {
            var at = earlier is null ? -1 : _actions.FindIndex(a => ReferenceEquals(a, earlier));
            if (at >= 0) _actions[at] = action;
            else _actions.Add(action);
        }
        return action;
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
    /// Marks what the engine observed for an earlier review as history, from <paramref name="from"/> on: its
    /// output becomes <paramref name="note"/>. The call stays listed and citable; what it said, about a report
    /// that has since been replaced, no longer reads as a finding about the current one.
    /// </summary>
    /// <returns>How many were marked.</returns>
    public int MarkHistorical(int from, string tool, string note)
    {
        var marked = 0;
        lock (_gate)
            for (var i = Math.Max(0, from); i < _actions.Count; i++)
                if (_actions[i].Tool == tool && _actions[i].Origin == ToolCallOrigin.Engine && _actions[i].Output != note)
                {
                    _actions[i] = _actions[i] with { Output = note };
                    marked++;
                }
        return marked;
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
    /// <para>Call lines are retained where the budget allows. This used to build the
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
    public EvidenceView Describe(int from = 0, int maxChars = DefaultBudget)
    {
        ExecutedAction[] slice;
        lock (_gate)
            slice = _actions.Skip(Math.Max(0, from)).ToArray();
        var resumed = _resumed;

        if (slice.Length == 0)
            return new EvidenceView("(no tools were run in this step)", slice, Array.Empty<int>(),
                hasPriorTranscript: resumed);

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
                   + (resumed
                       ? " This run RESUMED an interrupted one, whose transcript the agent can also "
                         + "see; the calls it made are not in this list. A claim about work done "
                         + "before the interruption is not evidence of fabrication."
                       : "")
                   + " A result too long to show keeps its START and its END, with the cut marked "
                   + "between them — so a command's closing summary is always here; the call it "
                   + "belongs to still happened."
                   // Said once, and only when there is such a call: a diff read as arguments is the misreading this
                   // presentation is here to end (WrittenChange).
                   + (slice.Any(Changed)
                       ? " A call that changed a file is shown by WHAT IT CHANGED, not by its arguments: under its result, "
                         + "lines starting '+ ' were added, '- ' removed, and '  ' are unchanged lines around them; each @@ "
                         + "gives the lines it covers, in the file before and after. A diff too long to show is not cut "
                         + "in the middle like a result: it keeps its first changed lines and says how many it leaves out."
                       : "");
                   // A read cut to fit explains itself beside each file it names ("none of its 91 lines shown here"), and is
                   // not explained here: a sentence of 150 characters cost a step of single-file reads one of them
                   // (code review of task 21).

        // Numbered from 1 WITHIN THIS SLICE, not within the run. The number is a handle for a
        // reviewer that is asked to point at a call - see StepVerdictReview - and it can only point at what
        // it was shown. A number that meant a position in the whole journal would refer to calls
        // this evidence does not contain, and an audit checking it would be checking the wrong list.
        var calls = slice.Select((a, i) => Call(a, i + 1, manySteps)).ToArray();
        var floors = slice.Select(a => ChangeFloor(a) + ReadFloor(a)).ToArray();

        // How many can be shown AT ALL. Each costs its call line plus the floor under its output -
        // budgeting the call lines alone was the first version of this and it overran by a factor of
        // twelve, because the floor is paid per action whether or not there is room for it.
        //
        // The oldest go first - but not a command that ran, and not what the engine observed itself: those
        // go last, oldest first, and only when nothing else is left to give up. Run 148e77, 2026-09-29: a step
        // read ten files, ran the test suite (129 of 129 passed), then searched sixteen times; the budget kept
        // the searches, dropped the one test run as "the 19 oldest calls", and the reviewer failed the step
        // twice for claiming a test run "not among the displayed calls". A process that ran and exited is the
        // strongest evidence a step has, and a read or a search is the easiest to give up.
        var shown = new SortedSet<int>(Enumerable.Range(0, slice.Length));
        foreach (var i in Enumerable.Range(0, slice.Length).OrderBy(i => KeptLongest(slice[i]) ? 1 : 0).ThenBy(i => i))
        {
            if (shown.Count <= 1 || header.Length + Cost(calls, floors, shown) <= maxChars) break;
            shown.Remove(i);
        }
        var kept = shown.Count;
        var dropped = slice.Length - kept;
        var hidden = Enumerable.Range(0, slice.Length).Where(i => !shown.Contains(i)).ToArray();

        var note = dropped == 0 ? ""
            : hidden.Length == hidden[^1] + 1 && !hidden.Any(i => KeptLongest(slice[i]))
                ? $"… the {dropped} oldest call(s) of this step are not shown here.\n"
                : $"… {dropped} call(s) of this step are not shown here ({Ranges(hidden)}): the oldest reads and lookups go "
                  + "first; a command that ran, and what the engine observed itself, are kept longest.\n";

        // What is left over, shared FAIRLY. Not first-come - the old behaviour, and one README took all
        // of it - and not in equal slices either: a result shorter than its slice is shown whole, and
        // what it did not use goes to the longer ones. Run 596a05, 2026-09-29: nine calls in equal slices
        // of about 400 characters cut three disk tables of 500-700 each in the middle, while the one long
        // result - a report shown in full elsewhere - kept its slice, and room in the budget went unused.
        var spare = maxChars - header.Length - note.Length - Cost(calls, floors, shown);
        var room = MinOutputChars * kept + Math.Max(0, spare);
        var whole = shown.ToDictionary(i => i, i => Changed(slice[i])
            ? AnswerText(slice[i]) + "\n" + slice[i].Change!.Body() : AnswerText(slice[i]));
        var (texts, allowance) = Unrepeated(whole, room, (i, text, budget) => Render(i, text, budget, out _),
            i => floors[i]);

        var sb = new StringBuilder(header).AppendLine().Append(note);
        var outputsTruncated = false;
        var argumentsTruncated = false;

        foreach (var i in shown)
        {
            var output = Render(i, texts[i], allowance[i], out var shortened);
            // A read cut to fit says, beside each file it names, which of the file's lines its result still shows.
            sb.AppendLine(shortened && ShownAsRead(slice[i]) && texts[i] == whole[i]
                ? Call(slice[i], i + 1, manySteps, ReadKept(slice[i], texts[i], allowance[i]))
                : calls[i]);
            sb.Append("<- ").AppendLine(output);
            outputsTruncated |= shortened;
            argumentsTruncated |= !Changed(slice[i]) && !ShownAsRead(slice[i]) && slice[i].Arguments?.Length > MaxArguments;
        }

        return new EvidenceView(sb.ToString().TrimEnd(), slice,
            shown.Select(i => i + 1), outputsTruncated, argumentsTruncated, resumed);

        // A write's result is its tool's line and then its diff. Cut as one text, the start and the end were kept and the
        // middle of the diff went - where an insertion's new lines are, the very thing it is shown for. So the line keeps a
        // share of its own and the diff is shortened as a diff (WrittenChange.Body): the unchanged lines go first, then
        // changed lines from the end, and how many are not shown is said.
        string Render(int i, string text, int budget, out bool truncated)
        {
            if (ShownAsRead(slice[i]) && text == whole[i]) return ReadCut(slice[i], text, budget, out truncated, out _, out _);
            if (!Changed(slice[i]) || text != whole[i]) return Answer(text, budget, out truncated);
            truncated = text.Length > budget;
            if (!truncated) return text;
            var change = slice[i].Change!;
            var said = Shortening.ToFit(AnswerText(slice[i]), Math.Max(MinOutputChars, budget / 3));
            // Room for the note at least, taken from the result's line when it has to be: a diff cut to nothing must still
            // say it was there (WrittenChange.Body).
            if (budget - said.Length - 1 < change.NoteLength)
                said = Shortening.ToFit(AnswerText(slice[i]), Math.Max(1, budget - change.NoteLength - 1));
            var diff = change.Body(Math.Max(change.NoteLength, budget - said.Length - 1));
            return diff.Length == 0 ? said : said + "\n" + diff;
        }
    }

    /// <summary>
    /// A call shown by what it changed (<see cref="ExecutedAction.Change"/>) rather than by its arguments. Only one that
    /// succeeded: a write that failed or was refused changed nothing, and keeps the presentation it always had.
    /// </summary>
    private static bool Changed(ExecutedAction action)
        => action.Change is not null && action.Outcome == ActionOutcome.Succeeded;

    /// <summary>
    /// The least of a diff each write is given, on top of the floor under its result - paid for in <see cref="Cost"/>, so a
    /// step of many writes keeps enough of each to see what it added (an insertion's first added lines: the new test's
    /// name) and gives up its oldest reads instead.
    /// </summary>
    private const int MinChangeChars = 240;

    /// <summary>
    /// The room a call's diff has of its own: <see cref="MinChangeChars"/>, or the whole diff when it is shorter. Charged
    /// in full, a one-line replacement of fifty characters held 240, and a step of many small edits gave up reads it had
    /// room to show (code review of task 20).
    /// </summary>
    private static int ChangeFloor(ExecutedAction action)
        => Changed(action) ? Math.Min(MinChangeChars, action.Change!.Body().Length + 1) : 0;

    /// <summary>
    /// A call shown by the files it read (<see cref="ExecutedAction.FilesRead"/>) rather than by its arguments. Only one that
    /// succeeded: a read that failed or found nothing read nothing, and keeps the presentation it always had.
    /// </summary>
    private static bool ShownAsRead(ExecutedAction action)
        => action.FilesRead is { Count: > 0 } files && FileRead.AnyRead(files) && action.Outcome == ActionOutcome.Succeeded
           && !Changed(action);

    /// <summary>
    /// The least of a cut read's result that is still its text, once what is said beside its files is paid for out of the
    /// floor every result has (<see cref="MinOutputChars"/>): the start and the end of it, a line or two.
    /// </summary>
    private const int MinReadChars = MinOutputChars / 2;

    /// <summary>
    /// The room a read has of its own for what it says beside each file (<see cref="FileRead.ShownLength"/>), paid for in
    /// <see cref="Cost"/> like a diff's floor so the budget is one the evidence obeys - but only what the floor every result
    /// already has cannot hold with <see cref="MinReadChars"/> of text left. Charged on top in full, with the paths said a
    /// second time in a list of their own, the least a read cost went from about 340 characters to about 850 in run 2eedd5's
    /// step, and a step of many reads lost more of them whole, names and all (code review of task 21). And only what a
    /// result can be cut by: one no longer than the least a result is shown in is never cut, and is charged nothing.
    /// </summary>
    private static int ReadFloor(ExecutedAction action)
        => ShownAsRead(action)
            ? Math.Max(0, Math.Min(FileRead.ShownLength(action.FilesRead!) + MinReadChars - MinOutputChars,
                AnswerText(action).Length - MinOutputChars))
            : 0;

    /// <summary>
    /// A read's result in <paramref name="budget"/>: whole when it fits, else its start and its end as any result - cut
    /// as ONE text, since a file has no error stream to keep apart - in what is left once what its call line will say
    /// beside each file is kept room for; <paramref name="kept"/> is what of it is shown, for that. Run 2eedd5: a read of
    /// five files reached the reviewer as the first one's opening lines and a note appended after the last, and two files
    /// shown in neither were judged never read.
    /// </summary>
    private static string ReadCut(ExecutedAction action, string text, int budget, out bool truncated,
        out int head, out int tail)
    {
        truncated = text.Length > budget;
        (head, tail) = (text.Length, 0);
        if (!truncated) return text;
        return Shortening.HeadAndTail(text, Math.Max(1, budget - FileRead.ShownLength(action.FilesRead!)),
            Shortening.CommandHead, out head, out tail);
    }

    /// <summary>The files a cut read read, each with which of its lines the cut result shows (<see cref="ReadCut"/>).</summary>
    private static string ReadKept(ExecutedAction action, string text, int budget)
    {
        ReadCut(action, text, budget, out _, out var head, out var tail);
        return FileRead.Summary(action.FilesRead!, text, [(0, head), (text.Length - tail, text.Length)]);
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

    /// <summary>
    /// Kept in the evidence longest: a process that ran and exited, and what the engine observed itself. Neither
    /// is a tool name - an exit code is a fact about any command, and an observation's origin is the engine's.
    /// </summary>
    private static bool KeptLongest(ExecutedAction action)
        => action.ExitCode is not null || action.Origin == ToolCallOrigin.Engine;

    /// <summary>1-based call numbers as ranges: "[1]-[18], [21]".</summary>
    private static string Ranges(IReadOnlyList<int> zeroBased)
    {
        var parts = new List<string>();
        for (var i = 0; i < zeroBased.Count;)
        {
            var j = i;
            while (j + 1 < zeroBased.Count && zeroBased[j + 1] == zeroBased[j] + 1) j++;
            parts.Add(i == j ? $"[{zeroBased[i] + 1}]" : $"[{zeroBased[i] + 1}]-[{zeroBased[j] + 1}]");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>The least the actions at <paramref name="shown"/> can be shown in.</summary>
    private static int Cost(string[] calls, int[] floors, IEnumerable<int> shown)
    {
        var total = 0;
        foreach (var i in shown)
            // The call, "<- ", the floor under its output, and the notice that says it was shortened -
            // which has to be paid for or the budget is a number the result does not obey. A write shown
            // by its diff pays for the least of that too.
            total += calls[i].Length + 1 + MinOutputChars + 4 + ShortenedNoticeChars
                   + floors[i];
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

    private const int MaxArguments = 300;

    /// <summary>The call itself. Arguments are clipped because write_file carries a whole file.</summary>
    /// <param name="withStep">Whether to name the step, when the slice spans several steps.</param>
    /// <param name="read">A read's files as its cut result shows them (<see cref="ReadKept"/>), in place of their names alone.</param>
    private static string Call(ExecutedAction action, int number, bool withStep = false, string? read = null)
    {
        var arguments = action.Arguments ?? "";
        if (Changed(action))
            // Not the arguments: compacted, they are the first 120 characters of the call, and for an edit that inserts
            // after an anchor that is the anchor - what the edit kept, read as what it changed (WrittenChange).
            arguments = "changed " + action.Change!.Summary;
        else if (ShownAsRead(action))
            // Not the arguments either: cut at 120 characters, a call reading five files named two and a half of them,
            // and the reviewer failed a step for files "never read" that were (FileRead).
            arguments = "read " + (read ?? FileRead.Summary(action.FilesRead!));
        else if (arguments.Length > MaxArguments)
            arguments = arguments[..MaxArguments] + $"… ({Invariant.N0(arguments.Length)} characters of arguments)";

        var whose = withStep && action.Step is { } step ? $" (step {step})" : "";
        var facts = (action.ExitCode is { } code ? $" [process exit={code}]" : "")
            + (action.FileDeletion ? " [file-deletion operation]" : "");
        return $"[{number}]{whose}{facts} -> {action.Tool} {arguments}";
    }

    /// <summary>How many calls a slice has, including those omitted from the evidence view.</summary>
    public int CountFrom(int from = 0)
    {
        lock (_gate)
            return Math.Max(0, _actions.Count - Math.Max(0, from));
    }

    /// <summary>
    /// Room shared out so that every result shorter than a fair share is shown whole, and what those
    /// leave goes, in equal parts, to the ones longer than it - never less than an equal part each.
    /// </summary>
    private static Dictionary<int, int> Fair(IReadOnlyDictionary<int, int> lengths, int room)
    {
        var given = new Dictionary<int, int>();
        var left = lengths.Count;
        foreach (var (i, length) in lengths.OrderBy(p => p.Value).ThenBy(p => p.Key))
        {
            var share = room / left;
            given[i] = Math.Min(length, share);
            room -= given[i];
            left--;
        }
        return given;
    }

    /// <summary>A result this long or longer is not printed twice.</summary>
    private const int RepeatedChars = 400;

    /// <summary>
    /// A result whose whole text is inside another result shown - the same file read twice, a file handed on
    /// and then read - is printed once. The other says where it is, so the call is still listed and can still
    /// be cited. Run 3d2446, 2026-09-29: a report of 3,000 characters was shown twice in one review, once as the
    /// file the step handed on and once as the next step's read of it, and the two copies split its share of
    /// the budget so that the middle was cut out of both. Only the same text: a file that changed between two
    /// reads is shown both times, since what changed is the point.
    /// </summary>
    /// <param name="render">How a result is printed in its allowance - checked against, so "shown there" holds for a result
    /// printed in a shape of its own (a write's diff) too.</param>
    /// <param name="floor">Room a result has of its own, on top of its share of <paramref name="room"/> - a write's diff
    /// (<see cref="MinChangeChars"/>). Added after the sharing, not pooled into it: pooled, a step of twelve long reads and
    /// one edit gave the edit's floor out in thirteen parts, and the edit kept too little to show the name of the test it
    /// added.</param>
    private static (Dictionary<int, string> Texts, Dictionary<int, int> Allowance) Unrepeated(Dictionary<int, string> texts, int room,
        Func<int, string, int, string> render, Func<int, int> floor)
    {
        static string Flat(string text) => text.Replace("\r\n", "\n").Trim();
        var flat = texts.ToDictionary(p => p.Key, p => Flat(p.Value));
        var holders = new Dictionary<int, int>();
        var printed = new HashSet<int>();
        // The longest first: a result that contains another is the one kept whole. Of two the same, the earlier.
        foreach (var i in texts.Keys.OrderByDescending(i => flat[i].Length).ThenBy(i => i))
        {
            var holder = flat[i].Length < RepeatedChars ? null
                : printed.Where(j => flat[j].Contains(flat[i], StringComparison.Ordinal)).Cast<int?>().FirstOrDefault();
            if (holder is { } j) holders[i] = j;
            else printed.Add(i);
        }

        // Only while the text is still there to be seen where it is said to be. The room is shared after, and a holder
        // that does not fit is cut in the middle: a result found in the middle of a long one would be cut out of it, and
        // "shown there" would promise what is not (code review of engeen_v4, P2). Such a one is printed after all, and
        // the room shared again, until every "shown there" is true.
        while (true)
        {
            var result = new Dictionary<int, string>(texts);
            foreach (var (i, j) in holders)
                result[i] = $"(the same {Invariant.N0(flat[i].Length)} characters as in the result of [{j + 1}] - shown there, not repeated here)";
            var allowance = Fair(result.ToDictionary(p => p.Key, p => Math.Max(0, p.Value.Length - floor(p.Key))), room);
            foreach (var i in allowance.Keys.ToArray()) allowance[i] += floor(i);
            var lost = holders.Where(h => !Flat(render(h.Value, result[h.Value], allowance[h.Value])).Contains(flat[h.Key], StringComparison.Ordinal))
                .Select(h => h.Key).ToArray();
            if (lost.Length == 0) return (result, allowance);
            foreach (var i in lost) holders.Remove(i);
        }
    }

    private static string Answer(string text, int budget, out bool truncated)
    {
        truncated = text.Length > budget;
        return truncated ? HeadAndTail(text, budget) : text;
    }

    private static string AnswerText(ExecutedAction action)
        => action.Outcome switch
        {
            ActionOutcome.Refused => "REFUSED: " + (action.Output ?? "not permitted"),
            ActionOutcome.Failed => "ERROR: " + (action.Output ?? "failed"),
            // Not "ERROR". The call worked and the answer is that there is nothing there — which is
            // information, and reads as a defect only if it is labelled as one.
            ActionOutcome.Answered => "NOTHING THERE: " + (action.Output ?? "nothing to return"),
            _ => action.Output ?? "OK"
        };

    /// <summary>
    /// A shortened result: the START and the END of it, with the cut marked between them.
    ///
    /// <para>Reported 2026-09-07 22:22, twice in one run, and the reviewer named the cause itself:
    /// <i>"the provided tool output for 'dotnet test' is truncated and does not contain these
    /// specific numbers. The agent fabricated the test summary."</i> The numbers were real —
    /// <c>Passed! Failed: 0, Passed: 22</c> — and they were in the line after the cut.</para>
    ///
    /// <para>The rule itself moved to <see cref="Shortening"/>, because it was written here and
    /// only here: the REVIEWER was shown the end of a command's output and the MODEL that ran the
    /// command was not — <c>ProcessExec</c> kept its first N characters. Same failure, two
    /// audiences, fixed for one of them.</para>
    /// </summary>
    private static string HeadAndTail(string text, int budget)
        => Shortening.HeadAndTail(text, budget);
}

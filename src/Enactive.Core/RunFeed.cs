namespace Enactive.Core.History;

using System.Text;
using Enactive.Core.Events;

/// <summary>Where a step card stands.</summary>
public enum FeedCardStatus { Pending, Running, Done, Failed, Skipped, Unverified, Blocked }

/// <summary>What a line on a step card is.</summary>
public enum FeedEntryKind { Command, File, Tool, Note, Refusal }

/// <summary>One line on a step card: a tool call with its one-line result, a note, a refused call.</summary>
public sealed class FeedEntry(FeedEntryKind kind, string label)
{
    public FeedEntryKind Kind { get; } = kind;
    public string Label { get; } = label;

    /// <summary>The call's result, in one line, once it has one.</summary>
    public string Detail { get; internal set; } = string.Empty;
}

/// <summary>
/// One step card as the events have made it: what the step is, where it stands, what it is doing now and
/// what it has done. A view draws it; nothing here knows how.
/// </summary>
public sealed class FeedCard
{
    private readonly StringBuilder _noteBuffer = new();
    private readonly List<FeedEntry> _entries = new();
    private int _tools, _commands, _files, _notes, _refused;

    internal FeedCard(string title, FeedCard? parent = null)
    {
        Title = title;
        Parent = parent;
    }

    public string Title { get; }

    /// <summary>The step this card was made from, for a step done for each item - shown under it.</summary>
    public FeedCard? Parent { get; }

    public FeedCardStatus Status { get; private set; } = FeedCardStatus.Pending;

    /// <summary>A question is waiting for a person, and nothing moves until it is answered. Cleared by any status.</summary>
    public bool WaitingForYou { get; private set; }

    public string Activity { get; private set; } = "Waiting…";

    public IReadOnlyList<FeedEntry> Entries => _entries;

    /// <summary>What the card holds, in words - the line a person reads to decide whether to open it.</summary>
    public string Tally => new StepTally(_tools, _commands, _files, _refused, _notes).Words();

    /// <summary>Bumped on every change, so a view can tell which cards to redraw.</summary>
    public int Version { get; private set; }

    /// <summary>Whether the step reached an end of its own.</summary>
    public bool Ended => Status is not (FeedCardStatus.Pending or FeedCardStatus.Running);

    internal void SetRunning() => SetStatus(FeedCardStatus.Running);

    internal void SetStatus(FeedCardStatus status)
    {
        if (status != FeedCardStatus.Running)
            FlushPendingNote();
        Status = status;
        WaitingForYou = false;
        Version++;
    }

    internal void SetWaitingForYou()
    {
        WaitingForYou = true;
        Version++;
    }

    internal void SetActivity(string text)
    {
        Activity = text;
        Version++;
    }

    /// <summary>
    /// Buffers a chunk of the assistant's streamed reply. It is never shown verbatim - it is folded into a
    /// single short note the next time a tool runs or the step ends. The card shows a 220-character preview;
    /// the complete text lives in the run log.
    /// </summary>
    internal void AppendAssistantText(string delta)
    {
        foreach (var character in delta)
        {
            if (_noteBuffer.Length > 220) break;
            if (_noteBuffer.Length == 0 && char.IsWhiteSpace(character)) continue;
            _noteBuffer.Append(character is '\r' or '\n' ? ' ' : character);
        }
    }

    internal void AddCommand(string commandText)
    {
        FlushPendingNote();
        _tools++;
        _commands++;
        Add(FeedEntryKind.Command, "Ran: " + Truncate(commandText, 90));
    }

    /// <summary>A file operation - verb is a short past-tense word like "Wrote", "Read", "Listed".</summary>
    internal void AddFileOp(string verb, string path)
    {
        FlushPendingNote();
        _tools++;
        if (string.Equals(verb, "Wrote", StringComparison.Ordinal))
            _files++;
        Add(FeedEntryKind.File, $"{verb} {path}");
    }

    internal void AddGenericTool(string label)
    {
        FlushPendingNote();
        _tools++;
        Add(FeedEntryKind.Tool, label);
    }

    /// <summary>A short one-line result under the most recent entry.</summary>
    internal void AppendEntryDetail(string text)
    {
        if (_entries.Count == 0)
            return;
        var flat = Truncate(text.Replace('\n', ' ').Replace('\r', ' ').Trim(), 160);
        if (flat.Length == 0)
            return;
        _entries[^1].Detail = flat;
        Version++;
    }

    /// <summary>An explicit short note - a warning, a review or decision remark, an artifact - after any buffered text.</summary>
    internal void AddNote(string text)
    {
        FlushPendingNote();
        AddNoteEntry(text);
    }

    /// <summary>
    /// A call the policy - or whoever answers for it - did not let through. Counted apart from tools, and not
    /// as a tool: it never ran. Until 2026-09-11 it was a note, so a step stopped six times reported "14 notes".
    /// </summary>
    internal void AddRefusal(string text)
    {
        FlushPendingNote();
        _refused++;
        Add(FeedEntryKind.Refusal, text);
    }

    private void FlushPendingNote()
    {
        if (_noteBuffer.Length == 0)
            return;
        var text = _noteBuffer.ToString();
        _noteBuffer.Clear();
        AddNoteEntry(text);
    }

    private void AddNoteEntry(string text)
    {
        var flat = Truncate(text.Replace('\n', ' ').Replace('\r', ' ').Trim(), 220);
        if (flat.Length == 0)
            return;
        _notes++;
        Add(FeedEntryKind.Note, flat);
    }

    private void Add(FeedEntryKind kind, string label)
    {
        _entries.Add(new FeedEntry(kind, label));
        Version++;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// A run's step cards, its title, its phase and its step count, folded from its events one at a time - the
/// same fold whether the events arrive live or are read back from the run's record.
///
/// <para><b>Why one fold.</b> The live window and a run reopened from the history each built the cards with an
/// interpreter of their own, kept in agreement by comments, and they had drifted: a reopened run showed the steps
/// a plan grew at the end of the list instead of under their step, wrote "Blocked — Blocked — …" and enum names
/// ("ReviewRejected") where the live card had words, and a run that stopped while steps ran in parallel left
/// their live cards "running…" for good while the reopened run said they never finished. One fold cannot
/// disagree with itself.</para>
/// </summary>
public sealed class RunFeed
{
    private const string QuickActionMarker = "Quick action: ";
    private const string StepsMarker = " steps: ";
    private const string NeverFinished = "Never finished — the run ended here";

    // Numbering order: a plan's step N is _cards[_planOffset + N - 1]; cards made before the plan come first.
    private readonly List<FeedCard> _cards = new();
    // Display order: a step done for each item is shown under the step it was made from.
    private readonly List<FeedCard> _shown = new();
    private readonly Dictionary<FeedCard, int> _shownUnder = new();
    private readonly List<FeedCard> _running = new();
    private FeedCard? _current;
    private int _stepIndex;
    private int _planOffset;

    /// <param name="title">The run's title until the planner gives a better one - the request, shortened.</param>
    public RunFeed(string? title = null) => Title = title ?? string.Empty;

    public string Title { get; private set; }

    /// <summary>Where the run is: Understanding, Planning, Executing, then its outcome. Null before any of them.</summary>
    public string? Phase { get; private set; }

    public int StepsDone { get; private set; }

    public int StepsTotal { get; private set; }

    /// <summary>The cards in the order they are shown.</summary>
    public IReadOnlyList<FeedCard> Cards => _shown;

    /// <summary>
    /// Folds one event in. Returns the cards it added, each with the position it was shown at - in the order a
    /// view must insert them to show what this feed shows.
    /// </summary>
    /// <param name="step">The event's step number; the payload's when not given.</param>
    public IReadOnlyList<(FeedCard Card, int At)> Apply(WorkEvent ev, int? step = null)
    {
        var added = new List<(FeedCard, int)>();
        step ??= ev.StepNo();

        switch (ev.Kind)
        {
            case EventKind.IntentReceived:
                Phase = "Understanding";
                break;

            case EventKind.Routed:
                // Planning starts once the worker is routed - read from the route's value, not from "-> model"
                // in the sentence.
                if (ev.Route() == "worker")
                    Phase = "Planning";
                // A quick action's title, as a value; the sentence only for a run recorded before it was one.
                if (ev.QuickActionTitle() is { Length: > 0 } quick)
                    Title = quick;
                else if (ev.Summary.StartsWith(QuickActionMarker, StringComparison.Ordinal)
                         && ev.Summary[QuickActionMarker.Length..].Trim() is { Length: > 0 } said)
                    Title = said;
                break;

            case EventKind.PlanCreated:
                Phase = "Executing";
                // The planner's title is a better header than the raw request, which is often a paragraph. From the
                // payload; the sentence only for a run produced by a build that predates it, where a title
                // containing " — " lost its tail.
                if (ev.PlanTitle() is { Length: > 0 } plannedTitle)
                    Title = plannedTitle;
                else if (ev.Summary.IndexOf(" — ", StringComparison.Ordinal) is var dash and > 0)
                    Title = ev.Summary[..dash];
                CreateStepCards(ev, added);
                break;

            case EventKind.PlanExpanded:
                // Steps the plan grew while it ran: numbered after every step it had, SHOWN under the step they
                // were made from - a step after the items ran last and read first otherwise.
                var grownFrom = CardFor(step);
                var grown = ev.PlanSteps()?.ToArray() ?? [];
                AddStepCards(grown, grownFrom, added);
                grownFrom?.SetActivity(grown.Length == 0
                    ? "No steps for its items"
                    : $"{grown.Length} item step(s); joins their results when they have all ended");
                break;

            case EventKind.StepStarted:
                BeginStep(step, ev.Summary, added);
                (CardFor(step) ?? EnsureCurrent(added)).SetActivity("Thinking…");
                break;

            case EventKind.StepCompleted:
                CompleteStep(CardFor(step) ?? _current, ev);
                break;

            // A long generation still arriving - shown on the activity line, replaced in place, so minutes of
            // writing a big tool call do not look like a hang.
            case EventKind.GenerationProgress:
                (CardFor(step) ?? EnsureCurrent(added)).SetActivity(ev.Summary);
                break;

            case EventKind.AssistantDelta:
                var streaming = CardFor(step) ?? EnsureCurrent(added);
                // Buffered, not shown live - folded into one short note the next time a tool runs or the step ends.
                streaming.AppendAssistantText(ev.Summary);
                streaming.SetActivity("Thinking…");
                break;

            case EventKind.ToolInvoked:
                var tool = CardFor(step) ?? EnsureCurrent(added);
                var call = CallOf(ev);
                LogInvocation(tool, call);
                tool.SetActivity(DescribeActivity(call));
                break;

            case EventKind.ToolResult:
                (CardFor(step) ?? EnsureCurrent(added)).AppendEntryDetail(ev.Summary);
                break;

            case EventKind.ErrorObserved:
                // On the activity line too, without changing where the step stands: a warning is worth reading,
                // and is not the step being stuck.
                var warned = CardFor(step) ?? EnsureCurrent(added);
                warned.AddNote("⚠ " + ev.Summary);
                warned.SetActivity("⚠ " + ev.Summary);
                break;

            case EventKind.ReviewRequested:
            case EventKind.ReviewPassed:
            case EventKind.ReviewFailed:
                var reviewed = CardFor(step) ?? EnsureCurrent(added);
                reviewed.AddNote(ev.Summary);
                reviewed.SetActivity(ev.Kind == EventKind.ReviewRequested ? "Reviewing…"
                    : ev.Kind == EventKind.ReviewPassed ? "Review passed" : "Review flagged an issue…");
                break;

            case EventKind.DecisionRequested:
            case EventKind.DecisionResolved:
                var decided = CardFor(step) ?? EnsureCurrent(added);
                // A refused call gets its own line, told apart by the event's VALUE - three wordings say "denied".
                if (ev.WasRefused() == true)
                    decided.AddRefusal(ev.Summary);
                else
                    decided.AddNote(ev.Summary);
                if (ev.Kind == EventKind.DecisionRequested)
                {
                    decided.SetActivity("Waiting for your approval…");
                    decided.SetWaitingForYou();
                }
                else
                    // Answered: the step is moving again. Its own ending overwrites this either way.
                    decided.SetRunning();
                break;

            case EventKind.ArtifactProduced:
                (CardFor(step) ?? EnsureCurrent(added)).AddNote("Artifact: " + ev.Summary);
                break;

            // The conversation was pruned to fit the model's window, or handed over to a fresh one; and a
            // rejected step's work was put back. Both explain what the step does next.
            case EventKind.ContextTrimmed:
            case EventKind.ArtifactReverted:
                (CardFor(step) ?? EnsureCurrent(added)).AddNote(ev.Summary);
                break;

            // What the engine DECIDED, from the typed outcome rather than from which of the two kinds arrived.
            case EventKind.TaskCompleted:
            case EventKind.TaskFailed:
                var outcome = ev.Outcome() ?? (ev.Kind == EventKind.TaskCompleted ? RunOutcomeKind.Completed : RunOutcomeKind.Failed);
                Phase = outcome.ToString();
                End(outcome, ev.OutcomeReason());
                break;
        }

        return added;
    }

    /// <summary>
    /// The run stopped without saying how it ended - cancelled, broken, or recorded no further. Every card still
    /// short of an end never reached one, and says so rather than "running…" for good.
    /// </summary>
    public void Stopped()
    {
        foreach (var card in _cards.Where(c => !c.Ended))
        {
            card.SetStatus(FeedCardStatus.Failed);
            card.SetActivity(NeverFinished);
        }
        _running.Clear();
        _current = null;
    }

    /// <summary>
    /// A finished run's cards, folded from its record exactly as they were folded live - or none, for a record
    /// that cannot honestly produce any.
    /// </summary>
    public static RunFeed Replay(RunRecord record)
    {
        var feed = new RunFeed(string.IsNullOrWhiteSpace(record.Title) ? null : record.Title);

        // A plan whose events carry no step numbers is a record from before they existed: there is no honest
        // way to say which card a tool call belonged to, so it builds none and the timeline is the whole story.
        if (record.Events.Any(e => e.Kind == nameof(EventKind.PlanCreated)) && record.Events.All(e => e.Step is null))
            return feed;

        var ended = false;
        foreach (var e in record.Events)
        {
            if (!Enum.TryParse<EventKind>(e.Kind, out var kind))
                continue;
            feed.Apply(new WorkEvent(Guid.Empty, Guid.Empty, record.RunId, e.At, kind, e.Summary, e.Payload), e.Step);
            ended |= kind is EventKind.TaskCompleted or EventKind.TaskFailed;
        }

        // Cut off before it could finish: no outcome at all, which is not the same as success.
        if (!ended)
            feed.Stopped();
        return feed;
    }

    /// <summary>
    /// The run's own end: a card not made from a plan step - a quick action's, or the work before a plan - ends as
    /// the run did, and a plan's step that never ended never finished.
    /// </summary>
    private void End(RunOutcomeKind outcome, string? reason)
    {
        var step = outcome switch
        {
            RunOutcomeKind.Completed => StepOutcomeKind.Succeeded,
            RunOutcomeKind.Incomplete or RunOutcomeKind.NeedsUser => StepOutcomeKind.Incomplete,
            RunOutcomeKind.Blocked => StepOutcomeKind.Blocked,
            _ => StepOutcomeKind.Failed
        };

        foreach (var card in _cards.Where(c => !c.Ended).ToArray())
        {
            if (!IsPlanStep(card))
                // A completed run's reason is what must be added to "done" - the checks that overruled the steps,
                // or one of the engine's own that failed (RunOutcomeDecision.Settle) - and the card says it.
                Finish(card, step, reason);
            else
            {
                card.SetStatus(FeedCardStatus.Failed);
                card.SetActivity(NeverFinished);
            }
        }
        _running.Clear();
        _current = null;
    }

    // The cards made for a plan's steps - as against the card of a quick action, or one made for an event with no
    // step of its own. A step that never ended never finished; any other card ends as the run did.
    private readonly HashSet<FeedCard> _stepCards = new();

    private bool IsPlanStep(FeedCard card) => _stepCards.Contains(card);

    private void CompleteStep(FeedCard? card, WorkEvent ev)
    {
        // A failed step and a dependency-skipped step arrive as StepCompleted too. The outcome is a value in the
        // payload; the wording is read only for a run recorded by an earlier build - exactly the fragility it
        // replaced: rewording a summary used to turn a red card green.
        var outcome = ev.StepOutcome()
                      ?? (ev.Summary.Contains("skipped (dependency", StringComparison.Ordinal) ? StepOutcomeKind.Skipped
                          : ev.Summary.Contains("FAILED:", StringComparison.Ordinal) ? StepOutcomeKind.Failed
                          : StepOutcomeKind.Succeeded);
        // The step's own reason, as a value - or, for a record from before it was one, the tail of the sentence.
        // None for a step that worked or was skipped: "why did this work" is not a question, and a skipped step's
        // cause is another step's failure, which belongs on that card.
        var reason = outcome is StepOutcomeKind.Succeeded or StepOutcomeKind.Skipped
            ? null
            : ev.OutcomeReason() ?? TailOf(ev.Summary);

        if (card is not null)
            Finish(card, outcome, reason);
        if (card is not null)
            _running.Remove(card);
        _current = _running.Count == 1 ? _running[0] : null;
        StepsDone++;
    }

    /// <summary>
    /// A card's end, in the words a person reads (RunOutcomeWords): the step's REASON when it recorded one, and the
    /// outcome word alone otherwise. Never an enum's name, and never the word twice.
    /// </summary>
    private static void Finish(FeedCard card, StepOutcomeKind outcome, string? reason)
    {
        card.SetStatus(outcome switch
        {
            StepOutcomeKind.Succeeded => FeedCardStatus.Done,
            // Skipped is not failed: nothing went wrong in THIS step, and painting it red sends you looking for a
            // fault that is in another card.
            StepOutcomeKind.Skipped => FeedCardStatus.Skipped,
            // A blocked step has not failed; it waits for its cause.
            StepOutcomeKind.Blocked => FeedCardStatus.Blocked,
            // Done, and nobody confirmed it: not green, which would claim a confirmation, nor red, which would say
            // the work is missing when it is on disk.
            StepOutcomeKind.DoneUnverified => FeedCardStatus.Unverified,
            _ => FeedCardStatus.Failed
        });
        card.SetActivity(outcome == StepOutcomeKind.Succeeded
            ? string.IsNullOrWhiteSpace(reason) ? "Done" : reason
            : RunOutcomeWords.StepActivity(outcome, reason));
    }

    /// <summary>The tail of "[1/2] Title — INCOMPLETE: &lt;reason&gt;", for records written before the reason was a value.</summary>
    private static string? TailOf(string summary)
    {
        var colon = summary.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon + 2 < summary.Length && summary[(colon + 2)..].Trim() is { Length: > 0 } tail ? tail : null;
    }

    private void CreateStepCards(WorkEvent ev, List<(FeedCard, int)> added)
    {
        // Values first. Splitting the sentence on " | " turned a step whose own title contains one into two cards,
        // and every event afterwards was attributed to the wrong card.
        var titles = ev.PlanSteps()?.ToArray() ?? FromSummary(ev.Summary);
        if (titles.Length == 0)
            return;

        // A card made before the plan arrived is the planning's, not step 1's: it is closed, and the plan's cards
        // are numbered after it.
        foreach (var early in _cards)
        {
            early.SetStatus(FeedCardStatus.Done);
            early.SetActivity("Planned");
        }
        _planOffset = _cards.Count;
        _current = null;
        _running.Clear();
        StepsTotal = 0;   // the plan's own count; steps it grows are added to it
        AddStepCards(titles, null, added);

        static string[] FromSummary(string summary)
        {
            var index = summary.IndexOf(StepsMarker, StringComparison.Ordinal);
            return index < 0
                ? []
                : summary[(index + StepsMarker.Length)..].Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        }
    }

    /// <param name="under">The card the new ones are shown under (the step they were made from), or null for the end.</param>
    private void AddStepCards(IReadOnlyList<string> titles, FeedCard? under, List<(FeedCard, int)> added)
    {
        StepsTotal += titles.Count;
        foreach (var title in titles)
        {
            var card = new FeedCard(title.Trim(), under);
            _cards.Add(card);   // numbering: after every step the plan had
            _stepCards.Add(card);
            var parentAt = under is null ? -1 : _shown.IndexOf(under);
            var at = _shown.Count;
            if (under is not null && parentAt >= 0)
            {
                var below = _shownUnder.GetValueOrDefault(under);
                _shownUnder[under] = below + 1;
                at = Math.Min(parentAt + 1 + below, _shown.Count);
            }
            _shown.Insert(at, card);
            added.Add((card, at));
        }
    }

    private void BeginStep(int? step, string summary, List<(FeedCard, int)> added)
    {
        // The step number the orchestrator stamped on the event; steps start out of order, and several at once,
        // once more than one may run, so a running counter is not enough.
        var index = step ?? ++_stepIndex;
        _stepIndex = Math.Max(_stepIndex, index);

        FeedCard card;
        if (_planOffset + index - 1 < _cards.Count)
            card = _cards[_planOffset + index - 1];
        else
        {
            card = new FeedCard(summary);
            _cards.Add(card);
            _stepCards.Add(card);
            _shown.Add(card);
            added.Add((card, _shown.Count - 1));
            StepsTotal = _cards.Count;
        }
        card.SetRunning();
        _running.Add(card);
        // With one step in flight this is that step; with several, events without a step number have no single
        // owner, so nothing claims to be "current".
        _current = _running.Count == 1 ? card : null;
    }

    /// <summary>The card a step number belongs to, or null when there is none.</summary>
    private FeedCard? CardFor(int? step)
        => step is { } i && i >= 1 && _planOffset + i - 1 < _cards.Count ? _cards[_planOffset + i - 1] : null;

    /// <summary>The card an event with no step of its own belongs to - made, named after the run, when there is none.</summary>
    private FeedCard EnsureCurrent(List<(FeedCard, int)> added)
    {
        if (_current is not null)
            return _current;
        // The quick action's own title when the planner has given one.
        var card = new FeedCard(string.IsNullOrWhiteSpace(Title) ? "Working" : Title);
        card.SetRunning();
        _cards.Add(card);
        _shown.Add(card);
        added.Add((card, _shown.Count - 1));
        _current = card;
        if (StepsTotal == 0)
            StepsTotal = 1;
        return card;
    }

    // ── what a tool call looks like on a card ────────────────────────────────────

/// <summary>What a card shows of a tool call: which tool, the path it names, the command it runs.</summary>
    private sealed record Call(string Name, string? Path, string? Command);

    /// <summary>
    /// The call an event is about, from its VALUES - the tool, its path and its command, read from its whole arguments
    /// (WorkEventPayload.ToolPayload). The sentence "<c>name {arguments}</c>" is read only for a record from before the
    /// tool was a value: rewording it would have turned "Wrote disks.md" into "Ran write_file", and the arguments in it
    /// are cut short, so a long write was no JSON and said "Wrote a file".
    /// </summary>
    private static Call CallOf(WorkEvent ev)
    {
        if (ev.ToolName() is { Length: > 0 } name)
            return new Call(name, ev.ToolPath(), ev.ToolCommand());

        var (said, args) = Split(ev.Summary);
        return new Call(said, WorkEventPayload.ArgumentOf(args, "path"), WorkEventPayload.ArgumentOf(args, "command") ?? args);
    }

    /// <summary>
    /// The tools a card names by what they do to a path: the line a call leaves, and the header while it runs. One table
    /// for both, where each had a switch over the same names. Any other tool is "Ran" and "Running" by its name.
    /// </summary>
    private static readonly Dictionary<string, (string Did, string Doing, string Unnamed, string UnnamedDoing)> PathTools = new()
    {
        ["write_file"] = ("Wrote", "Writing", "a file", "a file"),
        ["read_file"] = ("Read", "Reading", "a file", "a file"),
        ["list_dir"] = ("Listed", "Listing", "a directory", "files"),
    };

    /// <summary>The tool a card names by the command it runs.</summary>
    private const string CommandTool = "run_command";

    private static void LogInvocation(FeedCard card, Call call)
    {
        if (PathTools.TryGetValue(call.Name, out var tool)) card.AddFileOp(tool.Did, call.Path ?? tool.Unnamed);
        else if (call.Name == CommandTool) card.AddCommand(call.Command ?? "a command");
        else card.AddGenericTool($"Ran {call.Name}");
    }

    /// <summary>The one-line "what it is doing right now" for the card's header.</summary>
    private static string DescribeActivity(Call call)
        => PathTools.TryGetValue(call.Name, out var tool) ? $"{tool.Doing} {call.Path ?? tool.UnnamedDoing}…"
            : call.Name == CommandTool
                ? call.Command is { } c ? $"Running: {(c.Length <= 60 ? c : c[..60] + "…")}" : "Running a command…"
                : $"Running {call.Name}…";

    private static (string Name, string Args) Split(string summary)
    {
        var space = summary.IndexOf(' ');
        return space > 0 ? (summary[..space], summary[(space + 1)..]) : (summary, string.Empty);
    }
}

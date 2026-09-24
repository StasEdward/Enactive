namespace Enactive.Agents;

using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Templates;
using Enactive.Core.Memory;

/// <summary>
/// Wraps an orchestrator event stream, re-yielding every event unchanged while accumulating a
/// <see cref="RunRecord"/> that is saved when the run ends (even on failure or cancellation).
/// Keeps the orchestrator pure — persistence is a consumer concern.
/// </summary>
public sealed class RunRecorder
{
    private readonly IRunStore _store;
    private readonly IMemoryStore? _memory;
    private readonly Guid _workspaceId;
    private readonly RunSettings? _settings;
    private readonly string? _spec;

    /// <param name="spec">
    /// The resolved specification this run was started from, as its canonical JSON - null for a run
    /// that was typed rather than started from a template.
    ///
    /// <para>Handed in for the same reason <paramref name="settings"/> is: the template is editable
    /// and its version moves on, so reading a finished run against the template as it stands later
    /// answers "what would this do now" rather than "what did it do".</para>
    /// </param>
    public RunRecorder(
        IRunStore store, IMemoryStore? memory = null, Guid workspaceId = default,
        RunSettings? settings = null, string? spec = null)
    {
        _store = store;
        _memory = memory;
        _workspaceId = workspaceId;
        // Handed in rather than read back later: what the run was allowed to do is a fact about the
        // moment it started, and the slider will have moved by the time anyone asks.
        _settings = settings;
        _spec = spec;
    }

    public async IAsyncEnumerable<WorkEvent> RecordAsync(
        IAsyncEnumerable<WorkEvent> stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var events = new List<WorkEvent>();

        // Enumerated by hand rather than with `await foreach`, so the MoveNext can sit in a try with
        // a CATCH while the yield sits outside it - an iterator may not yield inside a try that has
        // one. The shape is only there to let the exception be RECORDED before it is rethrown.
        var source = stream.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                WorkEvent current;
                try
                {
                    if (!await source.MoveNextAsync())
                        break;
                    current = source.Current;
                }
                catch (Exception ex)
                {
                    // THE POINT OF ALL THIS. An exception that ends a run used to leave the record
                    // with whatever events had arrived and no terminal event at all - so the run
                    // read as "Incomplete", the report had no reason, and the Inbox item said
                    // "Incomplete · 0 artifact(s)". The cause existed only in a log file nobody was
                    // told about.
                    //
                    // Found on 2026-09-10 by the first real scheduled runs: both died 40ms in on
                    // "model 'qwen2.5-coder' not found", and neither the timeline, the report nor
                    // the Inbox said so. A run that cannot say why it stopped is the failure the
                    // whole unattended design exists to prevent.
                    Note(events, ex);
                    throw;
                }

                events.Add(current);
                yield return current;
            }
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);

            if (events.Count > 0)
            {
                var record = Build(events, _settings, _spec);
                await _store.SaveAsync(record, CancellationToken.None);

                // Fold each resolved decision into the project's durable memory (PLAN_v2 §2.6) -
                // but only what this project actually learned. See ProjectFacts: memory is read
                // into the PROMPT of every step of every later run, so an entry that is not a fact
                // about the project is a permanent tax on the window and on the bill.
                if (_memory is not null && ProjectFacts.WorthRemembering(ToolsUsed(events), record.Artifacts))
                {
                    // Read once, for the duplicate check below. Best-effort like everything else
                    // here: if memory cannot be read, write rather than lose the entry - a
                    // duplicate costs one line, a silent drop costs the fact.
                    IReadOnlyList<MemoryEntry> known = Array.Empty<MemoryEntry>();
                    try { known = await _memory.LoadAllAsync(CancellationToken.None); }
                    catch { /* keep going: writing is what matters */ }

                    foreach (var decision in record.Decisions)
                    {
                        // A standing answer re-recorded by every run that asks is not a second
                        // decision. "git: denied" had six of the twenty places a run is given.
                        if (ProjectFacts.AlreadyDecided(known, decision))
                            continue;

                        await _memory.AppendAsync(
                            new MemoryEntry(Guid.NewGuid(), _workspaceId, MemoryKind.Decision, decision,
                                            null, record.FinishedAt),
                            CancellationToken.None);
                    }

                    // And how the run ENDED. Until 2026-09-08 memory held decisions only, which meant
                    // that in a workspace where nothing ever needed approving it held nothing at all -
                    // and the thing a later run most wants to know, what the last one concluded, was
                    // the one thing never written down. PLAN_v2 §11 carried this as "written, never
                    // read back"; it was also barely written.
                    if (Conclusion(record) is { Length: > 0 } conclusion)
                        await _memory.AppendAsync(
                            new MemoryEntry(Guid.NewGuid(), _workspaceId, MemoryKind.Outcome, conclusion,
                                            null, record.FinishedAt),
                            CancellationToken.None);
                }
            }
        }
    }

    /// <summary>
    /// Writes what killed the run INTO the run: an error to look at, and a terminal outcome that
    /// carries the reason as a value.
    ///
    /// <para>Two events rather than one, because they answer different questions and are read by
    /// different things. <c>ErrorObserved</c> puts the message on the timeline, where somebody
    /// scrolls to the bottom for it. <c>TaskFailed</c> carries the typed outcome, which is what the
    /// report, the history's status column and the Inbox line are all built from - none of them
    /// read prose.</para>
    ///
    /// <para>Nothing is invented. The events are stamped with the ids and the moment of the run
    /// that was actually happening, and if no event ever arrived there is no run to attach them to
    /// and none are made: a record fabricated out of an exception would be a run that never
    /// started.</para>
    /// </summary>
    private static void Note(List<WorkEvent> events, Exception ex)
    {
        if (events.Count == 0)
            return;

        var last = events[^1];
        var at = DateTimeOffset.Now;

        // Cancelled is not failed. A person pressing Stop, or a scheduled run cut off by the task's
        // time limit, has not produced a wrong answer - and telling them it failed would send them
        // looking for a defect that is not there.
        var cancelled = ex is OperationCanceledException;
        var reason = cancelled
            ? "the run was stopped before it finished"
            : $"{ex.GetType().Name}: {ex.Message}";

        if (!cancelled)
            events.Add(new WorkEvent(
                Guid.NewGuid(), last.TaskId, last.RunId, at,
                EventKind.ErrorObserved, reason, null));

        // TaskFailed for both: it is the terminal event kind for a run that did not complete, and
        // there is no separate "cancelled" kind. Which of the two it was rides in the PAYLOAD, as a
        // value - which is the whole reason the outcome stopped being inferred from the kind.
        events.Add(new WorkEvent(
            Guid.NewGuid(), last.TaskId, last.RunId, at,
            EventKind.TaskFailed,
            cancelled ? "Cancelled" : "Failed",
            WorkEventPayload.OutcomePayload(
                cancelled ? RunOutcomeKind.Cancelled : RunOutcomeKind.Failed, reason)));
    }

    /// <summary>
    /// Every tool this run invoked, by name, read from the events as VALUES.
    ///
    /// <para>Taken from the live events rather than from the saved record on purpose: the name is
    /// in the event's payload, and adding it to <see cref="RunRecord"/> would mean a new column in
    /// both SQL stores and a migration, for a question only this method asks and only once.</para>
    /// </summary>
    private static IEnumerable<string?> ToolsUsed(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.ToolInvoked).Select(e => e.ToolName());

    /// <summary>
    /// One line about how a run ended, for the project's memory: what was asked, how it finished,
    /// and what it left behind.
    ///
    /// <para>Deliberately one line and deliberately factual. It is read back into the NEXT run's
    /// prompt, so it competes for the same context window as the work itself - and a paragraph per
    /// past run would crowd out the decisions, which are the entries that age best. What a later run
    /// needs is "this was tried, it ended like this"; if it wants more there is a whole run record
    /// under the same title.</para>
    /// </summary>
    private static string? Conclusion(RunRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Title))
            return null;

        var terminal = record.Events.LastOrDefault(
            e => string.Equals(e.Kind, nameof(EventKind.TaskCompleted), StringComparison.Ordinal)
              || string.Equals(e.Kind, nameof(EventKind.TaskFailed), StringComparison.Ordinal));

        // A run with no terminal event never finished - it was killed, or the process went away.
        // Saying nothing is better than recording a conclusion it never reached.
        if (terminal is null)
            return null;

        // A CANCELLATION is that same absence one step later. The run reached a terminal event but
        // never a conclusion: somebody pressed stop. "the run was stopped before it finished" is a
        // fact about the person, not about the project - and memory is read into the PROMPT of every
        // step of every later run, so it is a permanent tax on the window and on the bill.
        //
        // Measured 2026-09-24 10:29, run 1942b0. The "What this project has already decided and
        // done" block in front of every step carried eighteen entries for one prompt, thirteen of
        // them that sentence, each indistinguishable from the next because the titles truncate to
        // the same words. Whatever a stopped run changed is on disk and in its own record; what it
        // DECIDED is nothing.
        if (string.Equals(record.Status, nameof(RunOutcomeKind.Cancelled), StringComparison.Ordinal))
            return null;

        var reason = WorkEventPayload.OutcomeReasonIn(terminal.Payload);
        var files = record.Artifacts.Count > 0
            ? " Changed: " + string.Join(", ", record.Artifacts.Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
              + (record.Artifacts.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 6 ? ", …" : "")
            : "";

        return $"\"{record.Title.Trim()}\" — {record.Status}"
             + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason.Trim())
             + files;
    }

    private static RunRecord Build(List<WorkEvent> events, RunSettings? settings, string? spec)
    {
        var first = events[0];
        var last = events[^1];

        var title = "(intent)";
        string? model = null;
        var artifacts = new List<string>();
        var decisions = new List<string>();
        var eventRecords = new List<RunEventRecord>();
        RunOutcomeKind? outcome = null;
        var promptTokens = 0;
        var completionTokens = 0;
        var sawUsage = false;

        // Nullable and starting at null, unlike the two above: zero here would be a claim that
        // nothing was served from a cache, and for a run against a local model the truth is that
        // nobody counted. See ModelSpend.CachedPromptTokens.
        int? cachedTokens = null;

        // What each model was asked to do, and what it cost. Keyed by phase AND model because one
        // model can serve two phases and two models can serve one - a run with ExecuteLight bound
        // spends on two models under "execute", and folding them together loses the distinction the
        // binding exists to make. Insertion-ordered, so the list reads in the order the run spent.
        var spend = new Dictionary<(string Purpose, string Provider, string Model), ModelSpend>();

        foreach (var ev in events)
        {
            // The step number is stamped HERE, while the event still carries it. Nothing downstream
            // can work it out again: replaying by order attributes a tool call to whichever step
            // started last, which is the wrong one as soon as two run at once.
            eventRecords.Add(new RunEventRecord(
                ev.At, ev.Kind.ToString(), ev.Summary, ev.StepNo(), ev.PayloadJson));
            switch (ev.Kind)
            {
                case EventKind.IntentReceived:
                    // One line, always. The request itself used to be the title, line breaks and
                    // all, which was fine while a request was a sentence somebody typed and became
                    // four identical-looking paragraphs per card the moment templates arrived.
                    title = RunTitle.OneLine(StripPrefix(ev.Summary, "Intent: "));
                    break;
                // The worker's model, from the routing event's VALUES.
                //
                // It was scraped out of the summary with a search for "-> model ", which picked the
                // right event only because the worker route is the one sentence that happens to
                // contain that phrase - the planner's reads "Planner -> Antropic/...". So the field
                // a run list shows was decided by five characters of wording in a message written
                // for a person to read, and rewording it would have quietly emptied the column.
                // The route now says which route it is.
                //
                // What this still cannot say is the OTHER models the run used. That is not a fault
                // of the parsing and is not fixed here - see Usage.ByModel.
                case EventKind.Routed when model is null && ev.Route() == "worker":
                    model = ev.ProviderId() is { Length: > 0 } p && ev.ModelName() is { Length: > 0 } m
                        ? $"{p}/{m}"
                        : ExtractAfter(ev.Summary, "-> model ");
                    break;

                // A record from before routing carried values: the sentence is all there is.
                case EventKind.Routed when model is null
                                           && ev.Route() is null
                                           && ev.Summary.Contains("-> model "):
                    model = ExtractAfter(ev.Summary, "-> model ");
                    break;
                case EventKind.ArtifactProduced:
                    // The path, when the event carries it as a value. Older records kept the whole
                    // sentence ("FileSet: path") and are still read that way where they are opened.
                    artifacts.Add(ev.ArtifactPath() ?? ev.Summary);
                    break;
                case EventKind.DecisionResolved:
                    decisions.Add(ev.Summary);
                    break;
                case EventKind.UsageReported when ev.Usage() is { } used:
                    promptTokens += used.In;
                    completionTokens += used.Out;
                    sawUsage = true;
                    // Through TokenCounts, never with ?? 0. A run where only the reviewer is on a
                    // provider that reports cache reads must come out as the reviewer's number, and
                    // a run where nothing reports one must come out as null - not as a zero saying
                    // the caching achieved nothing.
                    cachedTokens = TokenCounts.Add(cachedTokens, ev.CachedTokens());
                    Spent(spend, ev, used.In, used.Out, ev.CachedTokens());
                    break;
                // The terminal event carries a typed outcome now, so the history stores what the
                // engine DECIDED instead of a status inferred from which event happened to arrive
                // last. The event kind is the fallback for a record written by an older build.
                case EventKind.TaskFailed:
                case EventKind.TaskCompleted:
                    outcome = ev.Outcome()
                        ?? (ev.Kind == EventKind.TaskCompleted
                            ? RunOutcomeKind.Completed
                            : RunOutcomeKind.Failed);
                    break;
            }
        }

        // No terminal event at all means the stream stopped early — a crash or a cancellation. That
        // is not success, and it is not a failure the engine reported either.
        var status = (outcome ?? RunOutcomeKind.Incomplete).ToString();
        // A templated run is named by its TEMPLATE - "Code Review" is what somebody scanning the
        // list is looking for, and it is the truth about where the run came from.
        if (ResolvedTaskSpec.Parse(spec) is { TemplateName: { Length: > 0 } named })
            title = RunTitle.OneLine(named);

        return new RunRecord(
            first.RunId, first.TaskId, title, model,
            first.At, last.At, status, eventRecords, artifacts, decisions, settings,
            // Null rather than zero when nothing reported: "this provider does not tell us" and
            // "this run used no tokens" are different facts and are shown differently.
            sawUsage
                ? new RunUsage(promptTokens, completionTokens)
                {
                    // Null rather than an empty list when no call named its model. Records written
                    // before the usage payload carried one are exactly that case, and an empty list
                    // would read as "this run spent nothing anywhere".
                    ByModel = spend.Count > 0 ? spend.Values.ToList() : null,
                    CachedPromptTokens = cachedTokens
                }
                : null,
            spec);
    }

    /// <summary>
    /// Adds one call's tokens to the model that spent them.
    ///
    /// <para>From the event's VALUES — provider, model, purpose — not from its sentence.</para>
    ///
    /// <para>A call that names no provider is not counted here. It is still in the totals, so the
    /// two can disagree — deliberately: a breakdown that silently filed unattributed tokens under
    /// some model would be worse than one that is visibly short.</para>
    /// </summary>
    private static void Spent(
        Dictionary<(string, string, string), ModelSpend> spend, WorkEvent ev,
        int prompt, int completion, int? cached)
    {
        if (ev.ProviderId() is not { Length: > 0 } provider || ev.ModelName() is not { Length: > 0 } name)
            return;

        var purpose = ev.Purpose() is { Length: > 0 } p ? p : WorkEventPayload.WorkPurpose.Execute;
        var key = (purpose, provider, name);

        spend[key] = spend.TryGetValue(key, out var running)
            ? running with
            {
                PromptTokens = running.PromptTokens + prompt,
                CompletionTokens = running.CompletionTokens + completion,
                Calls = running.Calls + 1,
                // One turn in a step reporting a cache read and the next not reporting one is a
                // provider that answered once - the row says what it was told, and stays null only
                // while nothing has told it anything.
                CachedPromptTokens = TokenCounts.Add(running.CachedPromptTokens, cached)
            }
            : new ModelSpend(purpose, provider, name, prompt, completion, Calls: 1)
            {
                CachedPromptTokens = cached
            };
    }

    private static string StripPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string? ExtractAfter(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? value[(index + marker.Length)..].Trim() : null;
    }
}

namespace Enactive.Agents;

using System.Runtime.CompilerServices;
using System.Text.Json;
using static Enactive.Agents.ToolCallParsing;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

/// <summary>
/// What happens to a call the model made before the tool runs, if it runs at all: answered by the
/// engine, refused by one of its rules, asked about, or let through.
///
/// <para><b>Why one module.</b> The checks are many and their ORDER is the rule: a turn kept for the
/// hand-over refuses everything else first; a call a read-only step may not make is refused before
/// anybody is asked about it; a command that writes outside the workspace is asked about only after
/// the shell itself was allowed. The checks had been pulled out one by one for their own tests, but
/// the order and the short cuts between them stayed inline in an 1800-line tool loop, where the only
/// way to reach them was to script a whole run. Here they are one ordered list, asked one call at a
/// time, and the loop keeps only running what is let through.</para>
///
/// <para><b>What it records.</b> Everything a refusal or an answer leaves behind - the events, the
/// journal entry, how the open failures count it, the reply the model is given - is written here, by
/// kind, so no path can be the one that forgets a line. The event shapes are load-bearing: a call the
/// engine answers or refuses by rule is shown as invoked, a call refused at the gates is shown only as
/// decided, and ProjectFacts reads "invoked" as the run reaching into the workspace.</para>
/// </summary>
internal sealed class CallAdmission(
    StepFrame frame,
    IToolRegistry tools,
    IPermissionEngine permissions,
    Worker worker,
    PermissionPolicy policy,
    ToolOffer offer,
    TaskProgress taskProgress,
    IDecisionHandler decisions,
    SemaphoreSlim decisionGate,
    GrantedRoots granted,
    WritableRoots writableRoots,
    // The step's catalog of tools listed only on request (ToolBudget), when it has one.
    ToolBudget? catalog = null,
    // Whether the step may say it cannot go on (Phase 7.2).
    bool mayReportBlocked = false,
    // Whether a path a hand-over names exists - for the step as it sees the workspace.
    Func<string, bool>? outputPathExists = null,
    // What the request says may be changed, held against the first change to each file the run found - and the step's
    // title, which the question names (ChangeLimitGuard).
    ChangeLimitGuard? changeLimits = null,
    string? stepTitle = null)
{
    // Who may call what, and what a policy allows - the admission's own, not the loop's: the loop asks RunsFreely.
    private readonly ToolAccess _access = new(tools, permissions);
    /// <summary>What one call was admitted to. Filled by <see cref="AdmitAsync"/>, which cannot return it.</summary>
    internal sealed class Verdict
    {
        /// <summary>The tool is to run. False when the call was answered or refused here.</summary>
        public bool Run { get; set; }

        /// <summary>Tools the call loaded from the catalog: listed from the next turn, at the END of the list.</summary>
        public IReadOnlyList<ToolDefinition> Loaded { get; set; } = [];
    }

    /// <summary>
    /// What admitting one call ahead of its turn left behind, kept until the loop reaches that call: its events, its
    /// journal entry, how the open failures count it, its answer to the model - and the verdict, which is decided at
    /// once. Released by <see cref="Release"/>, in the calls' order.
    ///
    /// <para>A read admitted ahead of the batch it runs in used to leave its events and its journal entry at once, so
    /// a read the admission answered (the same read twice in a turn, a tool still in the catalog) stood in the run's
    /// feed and in the journal the review reads BEFORE the reads the model had made ahead of it. Only its answer to
    /// the model was kept back, so the conversation alone had the calls' order.</para>
    /// </summary>
    internal sealed class Held
    {
        public Verdict Verdict { get; } = new();
        internal List<WorkEvent> Events { get; } = [];
        internal List<ChatMessage> Replies { get; } = [];
        internal List<Action> Records { get; } = [];
        internal bool Released { get; set; }
    }

    private readonly HashSet<string> _readsThisTurn = new(StringComparer.Ordinal);

    /// <summary>
    /// Where the admission writes what it leaves for one call - its journal entry, how the open failures count it, its
    /// answer to the model: at once, or into the <see cref="Held"/> of a call admitted ahead. What the admission DECIDES
    /// (a read made once this turn, the step's word that it is blocked) is never kept back: the next call of the batch
    /// is decided on it.
    ///
    /// <para>Handed down with the call. It was a field, set for as long as an admission ran ahead - through an async
    /// iterator, read by every method of the class and passed to none of them.</para>
    /// </summary>
    private sealed class Trail(List<ChatMessage> conversation, Held? held)
    {
        public void Leave(Action record)
        {
            if (held is null) record();
            else held.Records.Add(record);
        }

        public void Reply(ChatMessage reply)
        {
            if (held is null) conversation.Add(reply);
            else held.Replies.Add(reply);
        }
    }

    /// <summary>The trail of a call admitted in its turn: everything written at once.</summary>
    private Trail AtOnce => new(frame.Messages, null);
    private bool _onlyHandOn;

    /// <summary>The step's own word that it cannot go on, once it has given it; the step ends blocked when the turn does.</summary>
    public string? ReportedBlocked { get; private set; }

    /// <summary>A new turn: whether it is kept for the hand-over alone, and no read made yet in it.</summary>
    public void BeginTurn(bool onlyHandOn)
    {
        _onlyHandOn = onlyHandOn;
        _readsThisTurn.Clear();
    }

    /// <summary>
    /// Decides one call, in order, and records whatever it does not let through.
    /// </summary>
    /// <param name="park">Writes the step's place down before it stops at a question nobody is here to answer.</param>
    public IAsyncEnumerable<WorkEvent> AdmitAsync(ToolCall call, Verdict verdict, Action park, CancellationToken ct)
        => AdmitInOrderAsync(call, verdict, park, AtOnce, ct);

    /// <summary>
    /// Decides one call ahead of its turn - a read the loop would run together with the reads before it - and keeps what
    /// it leaves until the loop reaches the call (<see cref="Release"/>), so the feed, the journal and the conversation
    /// all have the calls in the order the model made them. Only a call nobody is asked about can be decided ahead: a
    /// question kept back is one nobody sees, so reaching one is a fault in the caller, thrown before it is asked.
    /// </summary>
    public async Task<Held> AdmitAheadAsync(ToolCall call, CancellationToken ct)
    {
        var held = new Held();
        await foreach (var ev in AdmitInOrderAsync(call, held.Verdict,
                           () => throw new InvalidOperationException("A call admitted ahead cannot stop at a question."),
                           new Trail(frame.Messages, held), ct))
        {
            if (ev.Kind == EventKind.DecisionRequested)
                throw new InvalidOperationException(
                    $"'{call.Name}' would ask somebody, and was admitted ahead of its turn; only a call that runs freely can be (RunsFreely).");
            held.Events.Add(ev);
        }
        return held;
    }

    /// <summary>
    /// What a call admitted ahead left, written now that the loop has reached it: its journal entry and open failure, its
    /// answer in the conversation, and its events, returned to be put in the feed here.
    /// </summary>
    public IReadOnlyList<WorkEvent> Release(Held held)
    {
        if (held.Released)
            throw new InvalidOperationException("Already released: what a call left is written once.");
        held.Released = true;
        foreach (var record in held.Records) record();
        frame.Messages.AddRange(held.Replies);
        return held.Events;
    }

    private async IAsyncEnumerable<WorkEvent> AdmitInOrderAsync(ToolCall call, Verdict verdict, Action park, Trail trail,
        [EnumeratorCancellation] CancellationToken ct)
    {
        verdict.Run = false;
        verdict.Loaded = [];

        // ── The turn's own rules ──────────────────────────────────────────

        // A turn that offered only the hand-over: anything else is answered, not run.
        if (_onlyHandOn && call.Name != StepOutputContract.ToolName)
        {
            var onlyHandOn = $"this turn is for {StepOutputContract.ToolName} only. Hand on what you have "
                + "established first; every tool is back on the next turn.";
            foreach (var ev in NotRunByRule(call, onlyHandOn, trail))
                yield return ev;
            yield break;
        }

        // A step that has said it cannot go on does nothing more in this turn.
        if (ReportedBlocked is not null)
        {
            const string afterReport = "the step has reported it is blocked; nothing after that report is carried out.";
            foreach (var ev in NotRunByRule(call, afterReport, trail))
                yield return ev;
            yield break;
        }

        // ── Calls the engine answers itself ───────────────────────────────

        // The step's word that it cannot go on (Phase 7.2): recorded as its word. The step ends when the turn does.
        if (mayReportBlocked && call.Name == AgentBlocked.ToolName)
        {
            yield return Invoked(call);
            var (blockReason, blockNeeds, notAReport) = AgentBlocked.Read(call.ArgumentsJson);
            string recorded;
            if (notAReport is not null)
            {
                recorded = "Not recorded: " + notAReport;
                trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, recorded, WorkspaceEffect.None));
                yield return Ev(EventKind.ToolResult, $"{call.Name} -> failed: {recorded}");
            }
            else
            {
                ReportedBlocked = AgentBlocked.Line(blockReason!, blockNeeds);
                recorded = "Recorded: this step ends here as BLOCKED - not done - and the run stops for a person to remove "
                    + "the cause; then this step is done again.";
                trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Succeeded, recorded, WorkspaceEffect.None));
                yield return Ev(EventKind.ToolResult, $"{call.Name} -> ok: {ReportedBlocked}");
            }
            trail.Reply(ChatMessage.Tool(call.Id, recorded));
            yield break;
        }

        // The step's catalog: a tool loaded by name, or found by a search, is listed from the next turn -
        // at the END of the list, so nothing a provider has cached before it moves.
        if (catalog is not null && call.Name is ToolBudget.LoadToolName or ToolBudget.FindToolName)
        {
            yield return Invoked(call);
            var (found, said) = call.Name == ToolBudget.LoadToolName
                ? catalog.Load(call.ArgumentsJson)
                : catalog.Find(call.ArgumentsJson);
            verdict.Loaded = found;
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Succeeded, said, WorkspaceEffect.None));
            trail.Reply(ChatMessage.Tool(call.Id, said));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> ok: {said}");
            yield break;
        }

        // A tool still waiting in the catalog was never shown to the model: not its description, not its
        // arguments. The gate below asks only whether a tool exists and whether the role may use it, so
        // such a call ran - on arguments the model had made up from the name. It is answered instead, and
        // counts as a call that failed: the step is told how to get the tool, and may not end as if it had.
        if (catalog?.IsWaiting(call.Name) == true)
        {
            yield return Invoked(call);
            var notLoaded = $"'{call.Name}' is not loaded, so it did not run. Call {ToolBudget.LoadToolName} with its name first; "
                + "it is callable from the turn after, with its full description.";
            trail.Leave(() => frame.Open.Failed(call, notLoaded, true, null));
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, notLoaded));
            trail.Reply(ChatMessage.Tool(call.Id, "NOT RUN: " + notLoaded));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> not run: {notLoaded}");
            yield break;
        }

        // The step's hand-over: checked here, against the one contract, and nowhere else (see HandOn).
        if (TakesHandOn && call.Name == StepOutputContract.ToolName)
        {
            foreach (var ev in HandOn(call, answered: true, trail))
                yield return ev;
            yield break;
        }

        // The run offers the hand-over to every step; one with nothing to hand on is told so, and nothing is stored.
        if (frame.OutputSchema is null && frame.SubmitTool is not null && call.Name == StepOutputContract.ToolName)
        {
            yield return Invoked(call);
            const string nothing = "Nothing was stored: this step hands nothing on as values. Finish it now with a short "
                + "closing message - no further tool calls.";
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Succeeded, nothing, WorkspaceEffect.None));
            trail.Reply(ChatMessage.Tool(call.Id, nothing));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> ok: {nothing}");
            yield break;
        }

        // A document the engine assembles, looked at by a step for one item: answered by the engine,
        // not run. It may not exist yet, and whether it does is not the item's business - run
        // d91b6a45: a page step read it, got "File not found", and ended INCOMPLETE on that.
        if (frame.Boundary?.AssembledByTheEngine(call, tools.DefinitionOf(call.Name)) is { } assembled)
        {
            foreach (var ev in NotRunByRule(call, assembled, trail))
                yield return ev;
            yield break;
        }

        // ── The step's own limits ─────────────────────────────────────────

        // Outside what this step may change: refused before it runs, and said why. Not an open
        // failure - it is the engine's rule, not a call that went wrong - and the way on is named.
        // What a read-only step made itself, it may delete (WriteBoundary.Refuse): measured here, against the
        // workspace as the step found it, and only when the call is one that deletes.
        IReadOnlySet<string>? madeByThisStep = null;
        if (frame.Boundary?.AsksWhatTheStepMade(tools.DefinitionOf(call.Name)) == true && frame.Changes is not null && frame.StepStart is not null
            && await frame.Changes.TakeAsync(ct) is { } nowThere && await frame.Changes.CompareAsync(frame.StepStart, nowThere, ct) is { } sinceStart)
            madeByThisStep = sinceStart.Where(c => c.Kind == FileChangeKind.Added)
                .Select(c => ShellLookup.Normal(c.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (frame.Boundary?.Refuse(call, tools.DefinitionOf(call.Name),
                path => frame.Store.PendingPaths.Any(p => string.Equals(ShellLookup.Normal(p), path, StringComparison.OrdinalIgnoreCase)),
                madeByThisStep)
            is { } notItsToChange)
        {
            yield return Invoked(call);
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, notItsToChange, WorkspaceEffect.None));
            trail.Leave(() => frame.Open.RefusedByRule(call));
            trail.Reply(ChatMessage.Tool(call.Id, "REFUSED: " + notItsToChange));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> refused: {notItsToChange}");
            yield break;
        }

        // What the request says may be changed: the first change a step makes to a file the run found is put to the
        // planning model against the request's own words (ChangeLimitGuard). After the step's own limits, so a call they
        // refuse costs no question.
        if (changeLimits is not null)
        {
            var said = frame.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content;
            var limit = await changeLimits.CheckAsync(frame.StepNo, stepTitle, said, call, tools.DefinitionOf(call.Name),
                frame.Store, frame.WorkspaceRoot, ct);
            if (limit.Usage.Any)
                yield return changeLimits.UsageEvent(frame.TaskId, frame.RunId, frame.StepNo, limit.Usage);
            if (limit.Note is { } unchecked_)
                yield return Ev(EventKind.ErrorObserved, unchecked_);
            if (limit.Refusal is { } againstTheRequest)
            {
                yield return Invoked(call);
                trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, againstTheRequest, WorkspaceEffect.None));
                trail.Leave(() => frame.Open.RefusedByRule(call));
                trail.Reply(ChatMessage.Tool(call.Id, "REFUSED: " + againstTheRequest));
                yield return Ev(EventKind.ToolResult, $"{call.Name} -> refused: {againstTheRequest}");
                yield break;
            }
        }

        // ── A read that needs nobody's leave ──────────────────────────────

        var freeRead = _access.CanRunRead(call, worker, policy, offer);
        // The reads made in this turn, by tool and arguments: the same read twice in one turn is
        // run once. Measured 2026-09-28 13:28, run 80c951: one turn asked for four missing files,
        // each twice; every duplicate is a second copy of the same answer in a full window.
        if (freeRead && !_readsThisTurn.Add(call.Name + "\0" + TaskProgress.Canonical(call.ArgumentsJson)))
        {
            yield return Invoked(call);
            const string same = "Not run: this is the same call, with the same arguments, as one made earlier in this "
                + "turn, and its result above stands.";
            trail.Reply(ChatMessage.Tool(call.Id, same));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> {same}");
            yield break;
        }

        // Admitted here, before the gates below: such a read exists, its role may make it, it changes
        // nothing and asks nobody, and the policy allows it outright (ToolAccess.CanRunRead) - so every
        // gate below would let it through, and the loop may run it in parallel with the reads beside it.
        // Said as a rule because it used to be a side effect: a read run ahead in a batch skipped the
        // gates, a read run alone went through them, and only their agreeing kept the two the same.
        if (freeRead)
        {
            verdict.Run = true;
            yield break;
        }

        // ── The gates ─────────────────────────────────────────────────────

        // Existence, role, an exact repeat of a command, a whole-file write over a file seen in part.
        if (await PreflightAsync(call, ct) is { } admissionRefusal)
        {
            if (admissionRefusal.Answered) trail.Leave(() => frame.Open.FoundNothing(call, admissionRefusal.Reason));
            else trail.Leave(() => frame.Open.Failed(call, admissionRefusal.Reason, admissionRefusal.DidNotRun, admissionRefusal.AsTool));
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, admissionRefusal.Reason));
            yield return Decided(call.Name, allowed: false, admissionRefusal.Summary);
            trail.Reply(ChatMessage.Tool(call.Id, admissionRefusal.Reply));
            yield break;
        }

        // An action that cannot be taken back, which this task has already taken with exactly
        // these arguments - in an attempt that stopped, or a process that died - is not taken
        // again, and not asked about again. The model is given what it answered the first time.
        if (tools.DefinitionOf(call.Name)?.OnceOnly == true && taskProgress.DoneBefore(frame.TaskId, call) is { } already)
        {
            const string notAgain = "already done earlier in this task, with the same arguments; not repeated";
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, notAgain));
            yield return Decided(call.Name, allowed: false, $"{call.Name}: {notAgain}");
            trail.Reply(ChatMessage.Tool(call.Id,
                $"ALREADY DONE: this exact {call.Name} was carried out earlier in this task, at "
                + $"{already.At.ToLocalTime():yyyy-MM-dd HH:mm}, and cannot be taken back - it was NOT done again. "
                + $"What it answered then: {already.Output ?? "(nothing)"}"));
            yield break;
        }

        // ── Permission gate: allow / ask / deny ──────────────────────
        var gate = _access.Evaluate(policy, call.Name, offer);

        if (gate != PermissionDecision.Allow)
        {
            var approved = false;
            if (gate == PermissionDecision.Ask)
            {
                // What a removal takes away, said where the person decides. The question was the path and nothing more;
                // on 2026-10-09 (run 7f3435) it was allowed for a test file the run had found - 24,733 bytes of older
                // tests - which the step then rewrote as 954.
                var takesAway = await WhatItTakesAwayAsync(call, ct);
                yield return Ev(EventKind.DecisionRequested,
                    $"Approve tool '{call.Name}'? {Compact(call.ArgumentsJson)}" + (takesAway is null ? "" : " " + takesAway));

                var decisionRequest = _access.Approval(call, frame.TaskId, frame.RunId, frame.WorkspaceRoot);
                if (takesAway is not null)
                    decisionRequest = decisionRequest with
                    {
                        Detail = takesAway + "\n" + decisionRequest.Detail,
                        FullDetail = takesAway + "\n\n" + decisionRequest.FullText
                    };
                DecisionOutcome outcome;
                try { outcome = await ToolAccess.AskAsync(decisions, decisionGate, decisionRequest, ct); }
                catch (DecisionPendingException) { park(); throw; }
                approved = string.Equals(outcome.OptionId, "allow", StringComparison.OrdinalIgnoreCase);
                // Says WHO answered. A standing approval and a person clicking Allow used to
                // produce the same line, separated only by how long it took.
                var by = string.IsNullOrEmpty(outcome.Because) ? "" : $" ({outcome.Because})";
                yield return Decided(call.Name, approved,
                                $"{call.Name}: {(approved ? "allowed" : "denied")}{by}");
            }
            else
            {
                // The REASON, when there is a specific one. "Blocked by policy" was true of
                // a withheld tool and told the reader nothing they could act on; a run whose
                // shell was kept back because nobody was awake to approve it should say so.
                yield return Decided(call.Name, allowed: false,
                    $"{call.Name}: {offer.Reason(call.Name) ?? "blocked by policy"}");
            }

            if (!approved)
            {
                // Same reason as the role gate above: a denial that only appears in the
                // transcript lets the step finish green over an action that never happened.
                var why = gate == PermissionDecision.Ask
                    ? "the user did not permit this action"
                    : offer.Reason(call.Name) ?? "blocked by the permission policy";
                // A refusal is a call that NEVER HAPPENED: nobody typed it wrong and
                // nothing broke - it was asked about and answered. The person's "no" IS
                // the resolution, and the policy's "no" is a door that will not open, which
                // the model is told below to walk around. Either way there is no residue,
                // so it stops counting once the step has changed something. See Forgiven.
                trail.Leave(() => frame.Open.Refused(call, why));
                trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson),
                               ActionOutcome.Refused, why));

                // The model is told WHICH refusal this was. Both used to arrive as "the user
                // did not permit this action" - the distinction was computed, recorded in the
                // journal, and then thrown away on the one path where it changes behaviour.
                // A model told a person refused it stops and apologises, which is right; a
                // model told a tool is off for this run should stop asking for that tool and
                // do the job another way, and it could not tell the two apart.
                trail.Reply(ChatMessage.Tool(call.Id, gate == PermissionDecision.Ask
                    ? "ERROR: the user did not permit this action."
                    : $"ERROR: the tool '{call.Name}' is not permitted for this run and will "
                      + "not become permitted. Do not call it again. If another permitted tool "
                      + "can do the same job, use that instead; if none can, say what you "
                      + "could not do and why."));
                yield break;
            }
        }

        // ── Geography gate: does this command write somewhere else? ──
        //
        // AFTER the permission gate, because "may this run use a shell at all" is a bigger
        // question than "may this one write land there", and asking the second of somebody
        // who is about to refuse the first is a question wasted.
        //
        // A guess, and it says so: see ShellGeography. It never refuses on its own - the
        // whole reason it may exist at all is that its answer becomes a QUESTION. A check
        // this rough deciding by itself would be the guard the sandbox plan warns about, and
        // the first false positive would stop work the model was right to do.
        var outside = ShellGeography.WritesOutsideFor(
            call.Name, call.ArgumentsJson, frame.WorkspaceRoot, granted.Roots);

        if (outside.Count > 0)
        {
            var where = string.Join(", ", outside.Select(w => w.Known ? w.Path : w.Token));

            // The same rule as the tool offer above, at the other place this engine puts a
            // question to a handler: one that cannot say yes is not asked.
            //
            // This is the case the tool offer does NOT cover, and it is reachable in the
            // configuration §9an recommends for schedules. At Execute the shells sit in
            // AskBefore and are withheld, so nothing gets this far; at Autonomous the shell
            // is allowed outright and rightly offered - and then every write it aims outside
            // the workspace becomes a question nobody is awake to answer. The answer is not
            // in doubt, so it is given here instead of fetched.
            //
            // Only the REQUEST is skipped. Everything below - the decision line, the
            // journal entry, the failure, the sentence the model is told - runs exactly as
            // it does when a person says no, because the outcome genuinely is the same.
            DecisionOutcome geography;

            if (!decisions.CanApprove)
            {
                // The place, on the line that survives, since no request is emitted to
                // carry it. A refusal that does not say WHERE sends the reader looking.
                yield return Decided(call.Name, allowed: false,
                    $"{call.Name}: kept to the workspace — {where}, and nobody is there to "
                    + "allow it");

                var unattendedWhy = ShellGeography.Explain(outside, frame.WorkspaceRoot);
                trail.Leave(() => frame.Open.Failed(call, "this run may not write outside the workspace"));
                trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson),
                               ActionOutcome.Refused, "writes outside the workspace"));
                trail.Reply(ChatMessage.Tool(call.Id, "ERROR: " + unattendedWhy));
                yield break;
            }

            yield return Ev(EventKind.DecisionRequested,
                $"{call.Name} appears to write outside the workspace: {where}");

            var geographyRequest = new DecisionRequest(
                frame.TaskId,
                "Let this command write outside the workspace?",
                $"Writes to {where}",
                KeepOptions(outside),
                // Nothing is recommended. Every other approval in this engine can lean on
                // "this is the tool you configured"; this one is a guess about a path, and
                // a highlighted button is an answer given on the reader's behalf.
                RecommendedOptionId: null,
                Subject: null,
                FullDetail: ShellGeography.Explain(outside, frame.WorkspaceRoot)
                          + "\n\nThe command in full:\n" + DescribeCall(call),
                // Still true, and it is about the TOOL: no "Allow run_command in this
                // workspace" button appears here or anywhere, whatever is answered below.
                // The "keep" option remembers a PLACE, which the boundary is made of, and
                // leaves every question about the shell itself exactly where it was.
                SessionOnly: true,
                Action: new BoundAction(
                    frame.RunId, call.Id, call.Name, call.ArgumentsJson, frame.WorkspaceRoot));

            try { geography = await ToolAccess.AskAsync(decisions, decisionGate, geographyRequest, ct); }
            catch (DecisionPendingException) { park(); throw; }

            var keepOut = string.Equals(geography.OptionId, "deny", StringComparison.OrdinalIgnoreCase);
            var forRun = string.Equals(geography.OptionId, "run", StringComparison.OrdinalIgnoreCase);
            var keepIt = string.Equals(geography.OptionId, "keep", StringComparison.OrdinalIgnoreCase);

            // Kept to the workspace IS a refusal: this command does not run. The other three
            // answers let it run, and the difference between them is how long the permission
            // lasts, not whether the call happened.
            yield return Decided(call.Name, allowed: !keepOut,
                $"{call.Name}: {(keepOut ? "kept to the workspace"
                               : keepIt ? "allowed outside, kept for this workspace"
                               : forRun ? "allowed outside, for this run"
                               : "allowed outside, once")}");

            if (keepOut)
            {
                // Refused like any other refusal - counted, journalled, and told to the
                // model in words it can act on. A step that quietly skipped the call would
                // report success over work that never happened.
                var why = ShellGeography.Explain(outside, frame.WorkspaceRoot);
                trail.Leave(() => frame.Open.Failed(call, "the user kept this command inside the workspace"));
                trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson),
                               ActionOutcome.Refused, "writes outside the workspace"));
                trail.Reply(ChatMessage.Tool(call.Id, "ERROR: " + why));
                yield break;
            }

            if (forRun || keepIt)
                foreach (var write in outside)
                    granted.Grant(write);

            // Kept: the same folders, written down where they outlive the process. Through
            // the store's own rules, so a refusal there (a drive root, a system folder,
            // Enactive's own settings) leaves the run-scoped grant standing and nothing on
            // the disk - the command still runs, and the person is simply asked again next
            // time rather than silently given something the store would not grant.
            if (keepIt)
                foreach (var root in granted.Roots)
                    if (writableRoots.Add(frame.WorkspaceRoot, root) is { } refused)
                        yield return Ev(EventKind.DecisionResolved,
                            $"Not kept for this workspace: {refused}");
        }

        verdict.Run = true;
    }

    /// <summary>
    /// The answers offered when a command appears to write outside the workspace.
    ///
    /// <para>Three always, and a fourth — <i>keep for this workspace</i> — only when every place
    /// named could actually be kept. The store refuses a drive root, a system folder and the folder
    /// holding Enactive's own settings, and a button that is offered and then refuses is worse than
    /// one that was never there: the person has already decided by the time they are told no. So the
    /// store is asked first, with the same method it will judge by.</para>
    ///
    /// <para>A write whose place is a variable is not keepable either — there is no folder to name,
    /// and this is the same reason <see cref="GrantedRoots.Grant"/> ignores one.</para>
    /// </summary>
    private static IReadOnlyList<DecisionOption> KeepOptions(IReadOnlyList<OutsideWrite> outside)
    {
        var options = new List<DecisionOption>(4)
        {
            new("once", "Allow once"),
            new("run", "Allow for this run"),
        };

        var folders = outside
            .Where(w => w.Known)
            .Select(w => Directory.Exists(w.Path) ? w.Path : Path.GetDirectoryName(w.Path))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (folders.Count > 0 && folders.TrueForAll(f => WritableRoots.Refuses(f!) is null))
            options.Add(new DecisionOption("keep", "Keep for this workspace"));

        // "Decline", said plainly. It was "Keep to the workspace", which sat beside "Keep for this
        // workspace" - the same two words meaning the opposite, one refusing the write and one
        // allowing it for good. Asked 2026-09-24: "why is there no Decline button?" There was;
        // nobody could tell which one it was.
        options.Add(new DecisionOption("deny", "Decline"));
        return options;
    }

    /// <summary>
    /// The checks that need nobody: the tool exists, the role may use it, a command is not an exact
    /// repeat, and a whole-file write does not replace what the step has seen only in part.
    /// </summary>
    private async Task<PreflightRefusal?> PreflightAsync(ToolCall call, CancellationToken ct)
    {
        if (!tools.Definitions.Any(d => string.Equals(d.Name, call.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var nearest = _access.NearestTool(call.Name, worker);
            var reason = $"there is no tool called '{call.Name}'. Nothing ran."
                + (nearest is null
                    ? " Use one of the tools listed for this conversation, spelled exactly as it appears there."
                    : $" The tool is spelled '{nearest}'. Call it again with that name.");
            return new PreflightRefusal(reason, "ERROR: " + reason, $"{call.Name}: no such tool", true, nearest);
        }
        if (!ToolAccess.Allows(worker, call.Name))
            return new PreflightRefusal($"not available to the {worker.Role} role",
                $"ERROR: tool '{call.Name}' is not available to the {worker.Role} role.",
                $"{call.Name}: not available to role '{worker.Role}'");
        if (tools.DefinitionOf(call.Name)?.Kind == ToolKind.Command && !HasForce(call)
            && frame.Progress.AlreadyRanExactly(call, tools.WorkspaceVersion(frame.WorkspaceId)))
        {
            var reason = $"'{call.Name}' already ran with these exact arguments earlier in this step, "
                + "with no reported workspace effects or intervening tracked frame.Changes. "
                + "Its earlier result is available. If you have a reason to check again "
                + "(state outside the workspace, flakiness you are checking for), send it "
                + "again with \"force\": true.";
            return new PreflightRefusal(reason, "ERROR: " + reason, $"{call.Name}: refused — an exact repeat", Answered: true);
        }
        var named = ReadLedger.FileNamedBy(call);
        if (frame.Reads.Refuse(call, named, tools.DefinitionOf(call.Name),
            named is null || await FileIsThereAsync(frame.WorkspaceRoot, named, frame.Store, ct)) is { } unread)
            return new PreflightRefusal(unread, "ERROR: " + unread, $"{call.Name}: refused — the file has only been read in part");
        return null;
    }

    /// <summary>Whether a call asks, itself, to be run despite being an exact repeat - see <see cref="ToolArguments.Force"/>.</summary>
    private static bool HasForce(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(ToolArguments.Force, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether there is a file at this workspace path now - on disk, or as a proposal a staging
    /// store holds. When it cannot be said (a path that does not resolve), true: the guards that ask
    /// then stay as strict as they were, and the tool itself refuses a bad path.
    /// </summary>
    private static async Task<bool> FileIsThereAsync(string root, string path, IArtifactStore store, CancellationToken ct)
    {
        try
        {
            if (File.Exists(Path.GetFullPath(Path.Combine(root, path))))
                return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }

        return await store.TryReadPendingAsync(path, ct) is not null;
    }

    /// <summary>A call the checks that need nobody refuse, and how it is to be told and counted.</summary>
    private sealed record PreflightRefusal(string Reason, string Reply, string Summary,
        bool DidNotRun = false, string? AsTool = null, bool Answered = false);

    /// <summary>
    /// Whether this call is a read that needs nobody's leave - one this admission lets through without asking, and so one
    /// the loop may run together with the reads beside it. The admission's own test (its free-read path asks the same),
    /// where the loop used to hold the gates and ask them itself.
    /// </summary>
    public bool RunsFreely(ToolCall call) => _access.CanRunRead(call, worker, policy, offer);

    /// <summary>Whether this step hands a result on as values, so a hand-over is checked rather than turned away.</summary>
    public bool TakesHandOn => frame.OutputSchema is not null && frame.OutputSlot is not null;

    /// <summary>
    /// The step's hand-over (submit_step_output), checked against the one contract and kept or refused - for a call
    /// made in a turn, and for one sent with a handover note. The second is not answered in the conversation, which
    /// the note is about to replace: <paramref name="answered"/> false.
    ///
    /// <para>It was checked twice. The handover note's copy, in the tool loop, had drifted from this one: it said
    /// nothing of items handed on with no evidence behind them, left out the contract's notes, and did not say a
    /// submission sent again unchanged was one - all of which the review reads in the journal.</para>
    /// </summary>
    public IEnumerable<WorkEvent> HandOn(ToolCall call, bool answered) => HandOn(call, answered, AtOnce);

    private IEnumerable<WorkEvent> HandOn(ToolCall call, bool answered, Trail trail)
    {
        if (frame.OutputSchema is null || frame.OutputSlot is null)
            throw new InvalidOperationException("This step hands nothing on as values; see TakesHandOn.");

        yield return Invoked(call);
        var sameAgain = frame.OutputSlot.LastRefused == TaskProgress.Canonical(call.ArgumentsJson);
        var checkedOutput = StepOutputContract.Check(frame.OutputSchema, call.ArgumentsJson,
            path => outputPathExists?.Invoke(path) == true, id => id >= 1 && id <= frame.Journal.Actions.Count,
            frame.Boundary is { ForItem: true } ? frame.Boundary.Items : null, offered: frame.SubmitTool);
        string handed;
        if (checkedOutput.Accepted)
        {
            frame.OutputSlot.Accept(checkedOutput);
            frame.OutputSlot.LastRefused = null;
            trail.Leave(() => frame.Open.HandedOn());
            // What the step had shown for each item it hands a result on for, recorded now
            // and by the engine (Phase 5.1): later, only this counts as coverage.
            frame.OutputSlot.Items = EvidenceCoverage.Gather(frame.OutputSchema, checkedOutput.Values!, frame.Reads, frame.Journal.Actions, tools.Definitions,
                frame.Boundary is { ForItem: true } ? frame.Boundary.Items : null);
            var unbacked = EvidenceCoverage.Unbacked(frame.OutputSlot.Items);
            handed = $"Accepted as this step's output (revision {frame.OutputSlot.Revision}); the steps after it receive "
                + "these values." + (checkedOutput.Notes.Count > 0 ? " " + string.Join(" ", checkedOutput.Notes) : "")
                + (unbacked is null ? "" : " " + unbacked)
                // Said to the step when it is answered; with a note, the note is what carries on.
                + (answered ? " Finish the step with a short closing message." : " Handed on with the handover note.");
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Succeeded, handed, WorkspaceEffect.None));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> ok: {handed}");
        }
        else
        {
            // A submission sent again unchanged is said to be one (C.5): a refusal that
            // does not say so changes nothing about what the model does next.
            handed = (sameAgain ? "This is the same submission as the last one, unchanged - the problems below still stand. " : "")
                + string.Join(" ", checkedOutput.Errors) + " Nothing was stored; send the corrected submission.";
            frame.OutputSlot.LastRefused = TaskProgress.Canonical(call.ArgumentsJson);
            trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, handed, WorkspaceEffect.None));
            yield return Ev(EventKind.ToolResult, $"{call.Name} -> failed: {handed}");
        }
        if (answered)
            trail.Reply(ChatMessage.Tool(call.Id, handed));
    }

    /// <summary>
    /// The calls of a turn that came back incomplete - cut at the output limit, or with arguments that do not parse:
    /// none ran, and each is recorded so - in the journal, and as a call the step has not made good, so it cannot
    /// end as though it had made them.
    /// </summary>
    public void NotRun(IEnumerable<ToolCall> calls, string reason)
    {
        foreach (var call in calls)
        {
            frame.Open.Failed(call, reason + "; nothing executed", didNotRun: true);
            frame.Journal.Record(frame.StepNo, call.Name, call.ArgumentsJson, ActionOutcome.Refused,
                reason + "; nothing executed", WorkspaceEffect.None);
        }
    }

    /// <summary>
    /// A call the engine's own rule turns away before it runs - a turn kept for the hand-over, a step that has said it is
    /// blocked, a look at a document the engine assembles: shown as invoked, journalled and counted as refused by rule,
    /// and answered NOT RUN with the reason. Written three times, word for word, until it was one.
    /// </summary>
    private IEnumerable<WorkEvent> NotRunByRule(ToolCall call, string why, Trail trail)
    {
        yield return Invoked(call);
        trail.Leave(() => frame.Journal.Record(frame.StepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused, why, WorkspaceEffect.None));
        trail.Leave(() => frame.Open.RefusedByRule(call));
        trail.Reply(ChatMessage.Tool(call.Id, "NOT RUN: " + why));
        yield return Ev(EventKind.ToolResult, $"{call.Name} -> not run: {why}");
    }

    /// <summary>
    /// What a call that takes files away would take, for the person asked to allow it: for each file, whether it was
    /// there before the run, whether the run has changed it since, and how big it is. Null for a call that takes nothing.
    /// </summary>
    internal async Task<string?> WhatItTakesAwayAsync(ToolCall call, CancellationToken ct)
    {
        var said = new List<string>();
        foreach (var path in ChangeLimitGuard.RemovedPaths(call, tools.DefinitionOf(call.Name)))
        {
            var rel = ShellLookup.Normal(path);
            string full;
            try { full = WorkspaceGuard.ResolveInside(frame.WorkspaceRoot, rel); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { continue; }
            if (!File.Exists(full)) continue;

            long bytes; int lines;
            try
            {
                bytes = new FileInfo(full).Length;
                lines = 0;
                foreach (var b in await File.ReadAllBytesAsync(full, ct)) if (b == (byte)'\n') lines++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var size = $"{bytes:N0} bytes, {lines:N0} lines";
            var before = await frame.Store.BeforeRunAsync(rel, ct);
            said.Add(before.State switch
            {
                BeforeRunState.Absent => $"'{rel}' was made by this run ({size}).",
                BeforeRunState.Untouched => $"'{rel}' existed before this run ({size}); this run has not changed it.",
                BeforeRunState.Kept => $"'{rel}' existed before this run ({before.Content!.Length:N0} bytes then); this run has changed it since - now {size}.",
                BeforeRunState.Lost => $"'{rel}' existed before this run; this run has changed it since - now {size}.",
                _ => $"'{rel}' ({size}): whether it existed before this run is not known."
            });
        }
        return said.Count == 0 ? null : "It takes away: " + string.Join(" ", said);
    }

    private WorkEvent Ev(EventKind kind, string summary)
        => new(Guid.NewGuid(), frame.TaskId, frame.RunId, DateTimeOffset.UtcNow, kind, summary,
               frame.StepNo is { } n ? $"{{\"step\":{n}}}" : null);

    /// <summary>A resolved decision, with WHETHER THE CALL WENT THROUGH as a value beside the sentence.</summary>
    private WorkEvent Decided(string tool, bool allowed, string summary)
        => new(Guid.NewGuid(), frame.TaskId, frame.RunId, DateTimeOffset.UtcNow,
               EventKind.DecisionResolved, summary,
               WorkEventPayload.DecisionPayload(frame.StepNo, tool, allowed));

    /// <summary>
    /// The tool's name as a VALUE beside the sentence, not only at the front of it. ProjectFacts decides
    /// from these whether the run reached into the workspace at all.
    /// </summary>
    private WorkEvent Invoked(ToolCall call)
        => new(Guid.NewGuid(), frame.TaskId, frame.RunId, DateTimeOffset.UtcNow,
               EventKind.ToolInvoked, $"{call.Name} {Compact(call.ArgumentsJson)}",
               WorkEventPayload.ToolPayload(call, frame.StepNo));
}

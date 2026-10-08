namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Core.Tasks;
using Enactive.Core.Workers;
using Enactive.Tools;
using Xunit;

/// <summary>
/// What happens to a call before its tool runs - asked of the admission itself, one call at a time.
///
/// <para>Until 2026-10-08 these checks sat inline in the tool loop, and their ORDER - which is the rule
/// - could be reached only by scripting a whole run in the right sequence. Each test here is about one
/// place where the order decides the answer: what is refused before anybody is asked, what is never
/// asked at all, what is asked twice.</para>
/// </summary>
public sealed class CallAdmissionTests : IDisposable
{
    private readonly EngineFixture _fx = new();
    private readonly ToolRegistry _tools = new(EngineFixture.ShippedTools());
    private readonly List<ChatMessage> _messages = [];
    private readonly GrantedRoots _granted = new();
    private readonly TaskProgress _tasks;
    private readonly Guid _taskId = Guid.NewGuid();
    private readonly ExecutionJournal _journal = new();
    private StepFrame _frame = null!;

    public CallAdmissionTests()
    {
        _tasks = new TaskProgress(_fx.Root);
    }

    public void Dispose() => _fx.Dispose();

    private CallAdmission Admission(int autonomy, IDecisionHandler decisions, Worker? worker = null,
        WriteBoundary? boundary = null, ToolBudget? catalog = null, bool mayReportBlocked = false,
        StepOutputSchema? schema = null, StepOutputSlot? slot = null)
    {
        _frame = new StepFrame(_taskId, Guid.NewGuid(), 1, _fx.Workspace, _tools.Definitions, _messages, _journal,
            new ReadLedger(), _fx.Artifacts.BeginStep(), schema, slot, boundary: boundary);
        worker ??= EngineFixture.WorkerWith("read_file", "write_file", "run_command", "send_email");
        var policy = AutonomyTiers.PolicyFor(autonomy);
        var permissions = new PermissionEngine();
        var offer = ToolOffers.For(
            _tools.Definitions.Where(d => ToolAccess.Allows(worker, d.Name)).Select(d => d.Name),
            tool => permissions.Evaluate(policy, tool, _tools.RequiredLevelOf(tool)) is var d
                    && d == PermissionDecision.Allow && _tools.RequiresApprovalOf(tool) ? PermissionDecision.Ask : d,
            decisions.CanApproveTool);
        return new CallAdmission(_frame, _tools, permissions, worker, policy, offer, _tasks,
            decisions, new SemaphoreSlim(1, 1), _granted,
            new WritableRoots(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-roots.json")),
            catalog, mayReportBlocked);
    }

    private static async Task<(bool Run, CallAdmission.Verdict Verdict)> Admit(CallAdmission admission, ToolCall call)
    {
        var verdict = new CallAdmission.Verdict();
        await foreach (var _ in admission.AdmitAsync(call, verdict, () => { }, CancellationToken.None)) { }
        return (verdict.Run, verdict);
    }

    private string LastReply => _messages[^1].Content ?? "";

    private static ToolCall Call(string name, string arguments) => new("c" + Guid.NewGuid().ToString("N")[..6], name, arguments);

    private static WriteBoundary ReadOnlyStep(string root)
        => new(root, [], null, () => new HashSet<string>(), _ => { }, readOnly: true);

    // ── the turn's own rules come first ─────────────────────────────────────

    [Fact]
    public async Task A_turn_kept_for_the_hand_over_refuses_any_other_call_before_anything_else_is_asked()
    {
        var decisions = new ScriptedDecisionHandler("allow");
        var admission = Admission(0, decisions, boundary: ReadOnlyStep(_fx.Root));
        admission.BeginTurn(onlyHandOn: true);

        var (run, _) = await Admit(admission, Call("write_file", """{"path":"notes.md","content":"x"}"""));

        Assert.False(run);
        // Not the read-only step's refusal, and not a question: the turn's own rule answered first.
        Assert.StartsWith("NOT RUN: this turn is for submit_step_output only", LastReply);
        Assert.Empty(decisions.Requests);
    }

    [Fact]
    public async Task After_the_step_reports_it_is_blocked_nothing_more_in_the_turn_runs()
    {
        var admission = Admission(2, new ScriptedDecisionHandler("allow"), mayReportBlocked: true);
        admission.BeginTurn(onlyHandOn: false);

        await Admit(admission, Call(AgentBlocked.ToolName,
            """{"reason":"the mailbox password is refused","needs":"a working mailbox password"}"""));
        var (run, _) = await Admit(admission, Call("read_file", """{"path":"inbox.md"}"""));

        Assert.NotNull(admission.ReportedBlocked);
        Assert.False(run);
        Assert.Contains("reported it is blocked", LastReply);
    }

    [Fact]
    public async Task A_tool_still_in_the_catalog_is_not_run_until_it_is_loaded()
    {
        var mail = new ToolDefinition("mcp__mail__send", "Sends a message.", "{}");
        var (_, catalog) = ToolBudget.Split([.. _tools.Definitions, mail], maxLoaded: 8);
        var admission = Admission(3, new ScriptedDecisionHandler("allow"), catalog: catalog);
        admission.BeginTurn(onlyHandOn: false);

        var (beforeLoad, _) = await Admit(admission, Call(mail.Name, "{}"));
        Assert.False(beforeLoad);
        Assert.StartsWith($"NOT RUN: '{mail.Name}' is not loaded", LastReply);

        var (_, loaded) = await Admit(admission, Call(ToolBudget.LoadToolName, """{"names":["mcp__mail__send"]}"""));
        Assert.Equal([mail.Name], loaded.Loaded.Select(d => d.Name));
    }

    // ── refused before anybody is asked ─────────────────────────────────────

    /// <summary>
    /// A step planned read-only may not write, and that is the engine's rule - asking a person whether to
    /// allow a write the step may not make anyway is a question wasted, and a "yes" would mean nothing.
    /// </summary>
    [Fact]
    public async Task A_read_only_step_is_refused_a_write_before_anybody_is_asked_about_it()
    {
        var decisions = new ScriptedDecisionHandler("allow");
        var admission = Admission(0, decisions, boundary: ReadOnlyStep(_fx.Root));
        admission.BeginTurn(onlyHandOn: false);

        var (run, _) = await Admit(admission, Call("write_file", """{"path":"notes.md","content":"x"}"""));

        Assert.False(run);
        Assert.StartsWith("REFUSED: ", LastReply);
        Assert.Empty(decisions.Requests);
    }

    [Fact]
    public async Task An_exact_repeat_of_a_command_is_refused_and_one_sent_with_force_goes_on()
    {
        var admission = Admission(3, new ScriptedDecisionHandler("allow"));
        var status = Call("run_command", """{"command":"git status"}""");
        var version = _tools.WorkspaceVersion(_fx.Workspace.Id);
        _frame.Progress.Resulted(status, new ToolResult(true, "clean", null, [], new Dictionary<string, object?>(),
            WorkspaceEffect: WorkspaceEffect.None), version, version);
        _frame.Progress.BeginTurn();
        admission.BeginTurn(onlyHandOn: false);

        var (repeat, _) = await Admit(admission, status);
        var (forced, _) = await Admit(admission, Call("run_command", """{"command":"git status","force":true}"""));

        Assert.False(repeat);
        Assert.Contains("already ran with these exact arguments", _messages[0].Content);
        Assert.True(forced);
    }

    /// <summary>
    /// An action that cannot be taken back, already taken in this task with the same arguments, is answered
    /// with what it gave then - and nobody is asked whether to take it again.
    /// </summary>
    [Fact]
    public async Task An_action_already_taken_in_this_task_is_answered_from_then_and_not_asked_about()
    {
        var decisions = new ScriptedDecisionHandler("allow");
        var admission = Admission(2, decisions);
        var send = Call("send_email", """{"to":"ops@example.com","subject":"Disk report","body":"C: is at 91%"}""");
        _tasks.RecordDone(_taskId, send, "sent to ops@example.com");
        admission.BeginTurn(onlyHandOn: false);

        var (run, _) = await Admit(admission, send);

        Assert.False(run);
        Assert.StartsWith("ALREADY DONE: ", LastReply);
        Assert.Contains("sent to ops@example.com", LastReply);
        Assert.Empty(decisions.Requests);
    }

    // ── asked, and asked again ──────────────────────────────────────────────

    /// <summary>
    /// The shell is asked about first; only once it is allowed is the place it writes asked about - and an
    /// answer that keeps that place leaves it granted for the run.
    /// </summary>
    [Fact]
    public async Task A_command_writing_outside_is_asked_about_after_the_shell_itself_was_allowed()
    {
        var decisions = new ScriptedDecisionHandler("deny");
        decisions.Script.Enqueue("allow");
        decisions.Script.Enqueue("keep");
        var admission = Admission(2, decisions);
        admission.BeginTurn(onlyHandOn: false);
        var elsewhere = Path.Combine(Path.GetTempPath(), "enactive-tests", "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);

        var (run, _) = await Admit(admission, Call("run_command",
            System.Text.Json.JsonSerializer.Serialize(new { command = $"echo done > \"{Path.Combine(elsewhere, "report.txt")}\"" })));

        Assert.True(run);
        Assert.Equal(2, decisions.Requests.Count);
        Assert.Equal("run_command", decisions.Requests[0].Subject);
        Assert.Equal("Let this command write outside the workspace?", decisions.Requests[1].Topic);
        Assert.NotEmpty(_granted.Roots);
    }

    /// <summary>With nobody there to say yes, a write outside the workspace is refused without asking anybody.</summary>
    [Fact]
    public async Task A_command_writing_outside_is_refused_without_a_question_when_nobody_can_say_yes()
    {
        var nobody = new NobodyThere();
        var admission = Admission(3, nobody);
        admission.BeginTurn(onlyHandOn: false);
        var elsewhere = Path.Combine(Path.GetTempPath(), "enactive-tests", "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);

        var (run, _) = await Admit(admission, Call("run_command",
            System.Text.Json.JsonSerializer.Serialize(new { command = $"echo done > \"{Path.Combine(elsewhere, "report.txt")}\"" })));

        Assert.False(run);
        Assert.StartsWith("ERROR: ", LastReply);
        Assert.Equal(0, nobody.Asked);
    }

    // ── a call admitted ahead of its turn ───────────────────────────────────

    /// <summary>
    /// A call admitted ahead of its turn - a read the loop runs together with the reads before it - leaves nothing until
    /// the loop reaches it, and then exactly what it would have left in its turn: its events, its journal entry, how the
    /// open failures count it, its answer. Written at once, they stood in the feed and the journal before the calls the
    /// model had made ahead of it.
    /// </summary>
    [Fact]
    public async Task A_call_admitted_ahead_leaves_nothing_until_it_is_released()
    {
        var mail = new ToolDefinition("mcp__mail__send", "Sends a message.", "{}");
        var (_, catalog) = ToolBudget.Split([.. _tools.Definitions, mail], maxLoaded: 8);
        var admission = Admission(3, new ScriptedDecisionHandler("allow"), catalog: catalog);
        admission.BeginTurn(onlyHandOn: false);

        var held = await admission.AdmitAheadAsync(Call(mail.Name, "{}"), CancellationToken.None);

        Assert.False(held.Verdict.Run);
        Assert.Empty(_journal.Actions);
        Assert.Equal(0, _frame.Open.Count);
        Assert.Empty(_messages);

        var events = admission.Release(held);

        Assert.Equal([EventKind.ToolInvoked, EventKind.ToolResult], events.Select(e => e.Kind));
        Assert.Equal(ActionOutcome.Refused, Assert.Single(_journal.Actions).Outcome);
        Assert.Equal(1, _frame.Open.Count);
        Assert.StartsWith($"NOT RUN: '{mail.Name}' is not loaded", LastReply);
        Assert.Throws<InvalidOperationException>(() => admission.Release(held));
    }

    /// <summary>
    /// Only a call nobody is asked about can be admitted ahead: a question kept back until the loop reaches the call is
    /// one nobody sees while the step waits for its answer. Thrown before anybody is asked.
    /// </summary>
    [Fact]
    public async Task A_call_that_would_ask_somebody_cannot_be_admitted_ahead()
    {
        var decisions = new ScriptedDecisionHandler("allow");
        var admission = Admission(2, decisions);
        admission.BeginTurn(onlyHandOn: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admission.AdmitAheadAsync(Call("run_command", """{"command":"echo hi"}"""), CancellationToken.None));

        Assert.Empty(decisions.Requests);
        Assert.Empty(_journal.Actions);
        Assert.Empty(_messages);
    }

    // ── reads that need nobody's leave ──────────────────────────────────────

    [Fact]
    public async Task A_free_read_is_let_through_once_per_turn_and_again_the_next_turn()
    {
        var decisions = new ScriptedDecisionHandler("deny");
        var admission = Admission(2, decisions);
        admission.BeginTurn(onlyHandOn: false);

        var (first, _) = await Admit(admission, Call("read_file", """{"path":"disks.md"}"""));
        var (again, _) = await Admit(admission, Call("read_file", """{"path":"disks.md"}"""));
        var (other, _) = await Admit(admission, Call("read_file", """{"path":"mail.md"}"""));
        admission.BeginTurn(onlyHandOn: false);
        var (nextTurn, _) = await Admit(admission, Call("read_file", """{"path":"disks.md"}"""));

        Assert.True(first);
        Assert.False(again);
        Assert.StartsWith("Not run: this is the same call", _messages.Single().Content);
        Assert.True(other);
        Assert.True(nextTurn);
        Assert.Empty(decisions.Requests);
    }

    /// <summary>A handler that can approve nothing, and counts the questions it was asked anyway.</summary>
    private sealed class NobodyThere : IDecisionHandler
    {
        public int Asked { get; private set; }
        public bool CanApprove => false;

        public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(new DecisionOutcome("deny"));
        }
    }

    // ── calls that never ran, and the hand-over ─────────────────────────────

    /// <summary>
    /// A turn that came back cut off or with arguments that do not parse ran none of its calls: each is recorded as
    /// not run, and stays open so the step cannot end as though it had made them. The loop did this itself.
    /// </summary>
    [Fact]
    public void A_call_from_an_incomplete_turn_is_recorded_as_not_run_and_left_open()
    {
        var admission = Admission(2, _fx.Decisions);

        admission.NotRun([Call("write_file", """{"path":"disks.md","content":"C: 9""")], "model output reached its token limit");

        Assert.Equal(1, _frame.Open.Count);
        var recorded = Assert.Single(_journal.Actions);
        Assert.Equal((ActionOutcome.Refused, "model output reached its token limit; nothing executed"), (recorded.Outcome, recorded.Output));
    }

    private static readonly StepOutputSchema Findings = new("disk-findings", 1,
        [new StepOutputField("findings", StepOutputFieldType.Text, "What the disks came to")]);

    /// <summary>
    /// A hand-over sent with a handover note goes through the one check, unanswered - the note replaces the
    /// conversation - and is refused in the same words: the note's own copy of the check did not say that a
    /// submission sent again unchanged was one.
    /// </summary>
    [Fact]
    public void A_hand_over_sent_with_a_note_is_checked_as_any_other_and_not_answered()
    {
        var admission = Admission(2, _fx.Decisions, schema: Findings, slot: new StepOutputSlot());
        var wrong = Call(StepOutputContract.ToolName, """{"verdict":"fine"}""");
        var before = _messages.Count;

        _ = admission.HandOn(wrong, answered: false).ToList();
        _ = admission.HandOn(wrong with { Id = "again" }, answered: false).ToList();
        _ = admission.HandOn(Call(StepOutputContract.ToolName, """{"findings":"C: is at 91%"}"""), answered: false).ToList();

        Assert.Equal(before, _messages.Count);
        Assert.StartsWith("This is the same submission as the last one", _journal.Actions[1].Output);
        Assert.EndsWith("Handed on with the handover note.", _journal.Actions[2].Output);
        Assert.Equal(ActionOutcome.Succeeded, _journal.Actions[2].Outcome);
    }

    // ── the step's frame, read as it is now ─────────────────────────────────

    /// <summary>
    /// A handover starts the step's record of what it has done over (StepFrame.StartOver), and the admission reads the
    /// record as it is now. It used to keep the one it was built with, so a command made before the handover was refused
    /// as "already ran" after it - the very repeat the handover exists to allow.
    /// </summary>
    [Fact]
    public async Task After_a_handover_a_command_from_before_it_is_not_refused_as_a_repeat()
    {
        var admission = Admission(3, new ScriptedDecisionHandler("allow"));
        var status = Call("run_command", """{"command":"git status"}""");
        var version = _tools.WorkspaceVersion(_fx.Workspace.Id);
        _frame.Progress.Resulted(status, new ToolResult(true, "clean", null, [], new Dictionary<string, object?>(),
            WorkspaceEffect: WorkspaceEffect.None), version, version);

        _frame.StartOver();
        _frame.Progress.BeginTurn();
        admission.BeginTurn(onlyHandOn: false);
        var (run, _) = await Admit(admission, status);

        Assert.True(run);
    }

    /// <summary>
    /// What the loop may run together is what the admission lets through without asking - asked of the admission, by the
    /// test its own free-read path uses, where the loop held the gates and asked them itself.
    /// </summary>
    [Theory]
    [InlineData("read_file", """{"path":"disks.md"}""", 0, true)]
    [InlineData("write_file", """{"path":"disks.md","content":"x"}""", 3, false)]
    [InlineData("send_email", """{"to":"ops@example.com"}""", 3, false)]
    public async Task A_read_runs_freely_exactly_when_the_admission_lets_it_through_unasked(string tool, string arguments, int autonomy, bool freely)
    {
        _fx.Write("disks.md", "C: 91%");
        var decisions = new ScriptedDecisionHandler("deny");
        var admission = Admission(autonomy, decisions);
        admission.BeginTurn(onlyHandOn: false);
        var call = Call(tool, arguments);

        Assert.Equal(freely, admission.RunsFreely(call));
        if (freely)
        {
            var (run, _) = await Admit(admission, call);
            Assert.True(run);
            Assert.Empty(decisions.Requests);
        }
    }
}

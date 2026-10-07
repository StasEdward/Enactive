namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Whether a turn ends the step, and how - asked of the rule itself, one turn at a time.
///
/// <para>Until 2026-10-08 the rules sat inline in the tool loop, their once-only flags were its locals, and their
/// ORDER - which is the rule - could be reached only by scripting a whole run. Each test here is about a place where
/// the order or the "once" decides the answer.</para>
/// </summary>
public sealed class StepEndingTests : IDisposable
{
    private readonly EngineFixture _fx = new();
    private readonly ToolRegistry _tools = new(EngineFixture.ShippedTools());
    private readonly List<ChatMessage> _messages = [];
    private readonly ExecutionJournal _journal = new();
    private readonly OpenFailures _open;
    private readonly StepProgress _progress;

    public StepEndingTests()
    {
        _open = new OpenFailures(_tools.Definitions);
        _progress = new StepProgress(_tools.Definitions);
    }

    public void Dispose() => _fx.Dispose();

    private StepEnding Ending(IReadOnlyList<SuccessCriterionDefinition>? criteria = null, StepOutputSchema? schema = null,
        StepOutputSlot? slot = null, bool reviewed = false)
        => new(_journal, _open, _messages, _progress, Guid.NewGuid(), Guid.NewGuid(), stepNo: 1, _fx.Root,
            (_, _) => Task.FromResult<string?>(null), criteria, schema, slot, reviewed);

    private static StepEnding.Turn Says(string? reply, IReadOnlyList<ToolCall>? calls = null, bool onlyHandOn = false,
        ToolCall? described = null, int actionsTaken = 1, int reasoning = 0, int? completion = null, int? prompt = null, int? window = null)
        => new(reply, calls, onlyHandOn, described, actionsTaken, reasoning, completion, prompt, () => window);

    private static async Task<StepEnding.Verdict> End(StepEnding ending, StepEnding.Turn turn)
    {
        var verdict = new StepEnding.Verdict();
        await foreach (var _ in ending.EndAsync(turn, verdict, CancellationToken.None)) { }
        return verdict;
    }

    private string LastTold => _messages[^1].Content ?? "";

    private static ToolCall Call(string name, string arguments) => new("c" + Guid.NewGuid().ToString("N")[..6], name, arguments);

    private static readonly StepOutputSchema Findings = new("disk-findings", 1,
        [new StepOutputField("findings", StepOutputFieldType.Text, "What the disks came to")]);

    // ── turns that are not the end ──────────────────────────────────────────

    [Fact]
    public async Task A_turn_that_made_calls_goes_on_to_run_them()
        => Assert.Equal(StepEnding.Next.Work,
            (await End(Ending(), Says(null, [Call("read_file", """{"path":"disks.md"}""")]))).Next);

    [Fact]
    public async Task Text_in_a_turn_kept_for_the_hand_over_does_not_end_the_step()
    {
        var verdict = await End(Ending(schema: Findings, slot: new StepOutputSlot()), Says("I have read the disks.", onlyHandOn: true));

        Assert.Equal(StepEnding.Next.Continue, verdict.Next);
        Assert.StartsWith("Nothing was handed on.", LastTold);
    }

    [Fact]
    public async Task A_call_written_as_text_is_asked_for_once_and_then_the_reply_is_the_answer()
    {
        var ending = Ending();
        var described = Call("read_file", """{"path":"disks.md"}""");

        var first = await End(ending, Says("""{"name":"read_file"}""", described: described));
        var asked = ending.TakeResendAsked();
        var second = await End(ending, Says("""{"name":"read_file"}""", described: described));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.True(asked);
        Assert.False(ending.TakeResendAsked());
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (second.Next, second.Kind));
    }

    /// <summary>
    /// A step that said it was done and repeats a call it already made, with nothing changed since, is done - not
    /// stuck (run fba4d6): the repeat is not run, and the step ends on its message.
    /// </summary>
    [Fact]
    public async Task A_step_that_says_it_is_done_and_only_repeats_ends_on_its_message()
    {
        var check = Call("run_command", """{"command":"chkdsk C:"}""");
        _progress.Advanced([check]);

        var verdict = await End(Ending(), Says("All disks are healthy.", [check with { Id = "again" }]));

        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (verdict.Next, verdict.Kind));
        Assert.StartsWith("NOT RUN: this exact call already ran", LastTold);
    }

    // ── nothing came back ───────────────────────────────────────────────────

    [Fact]
    public async Task A_turn_spent_reasoning_with_nothing_to_show_fails_the_step_and_says_to_turn_thinking_off()
    {
        var verdict = await End(Ending(), Says(null, actionsTaken: 0, reasoning: 4200));

        Assert.Equal(StepOutcomeKind.Failed, verdict.Kind);
        Assert.Contains("reasoning (4,200 characters of it)", verdict.Reason);
        Assert.Contains("Turn Thinking off", verdict.Reason);
    }

    [Theory]
    [InlineData(950, 1000, true)]
    [InlineData(1786, 131072, false)]
    public void Nothing_came_back_names_the_window_only_when_the_prompt_was_near_it(int prompt, int window, bool named)
    {
        var said = StepEnding.NothingCameBack(Says(null, actionsTaken: 0, completion: 12, prompt: prompt, window: window));

        Assert.Contains("returned nothing while reporting 12 output token(s)", said);
        Assert.Equal(named, said.Contains("raising num_ctx", StringComparison.Ordinal));
    }

    // ── told once, before any verdict ───────────────────────────────────────

    [Fact]
    public async Task A_criterion_the_plan_set_this_step_is_told_once_as_the_step_ends()
    {
        var report = new SuccessCriterionDefinition("disk report", "") { Step = 0, Typed = new TypedCriterion(TypedCriterionKind.FileExists, "disk-report.md") };
        var ending = Ending(criteria: [report]);

        var first = await End(ending, Says("Done."));
        var told = LastTold;
        var second = await End(ending, Says("Done."));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.StartsWith("Before this step ends: the plan checks this step's work", told);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (second.Next, second.Kind));
    }

    /// <summary>
    /// Run 4f1d97: three steps that had written their findings never heard they were to hand them on, because the
    /// verdict on the calls they left open ended them first. The reminder comes before any verdict.
    /// </summary>
    [Fact]
    public async Task A_step_owing_its_result_is_reminded_before_any_verdict_on_its_open_calls()
    {
        _open.Failed(Call("run_command", """{"command":"wmic diskdrive get status"}"""), "'wmic' is not recognized");
        var ending = Ending(schema: Findings, slot: new StepOutputSlot());

        var reminded = await End(ending, Says("The disks look fine."));
        var remindedOf = LastTold;
        var toldOfCalls = await End(ending, Says("The disks look fine."));
        var toldOf = LastTold;
        var ended = await End(ending, Says("The disks look fine."));

        Assert.Equal(StepEnding.Next.Continue, reminded.Next);
        Assert.StartsWith("This step is not finished until it hands its result on", remindedOf);
        Assert.Contains("These calls are still open", remindedOf);
        Assert.Equal(StepEnding.Next.Continue, toldOfCalls.Next);
        Assert.StartsWith("Before this step ends: this call did not go through", toldOf);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (ended.Next, ended.Kind));
        Assert.StartsWith("unresolved tool call:", ended.Reason);
    }

    /// <summary>A block the engine can see - a permission refused and never made good - ends the step blocked, untold.</summary>
    [Fact]
    public async Task A_block_the_engine_can_see_ends_the_step_before_it_is_told_of_its_open_calls()
    {
        _open.Refused(Call("send_email", """{"to":"ops@example.com"}"""), "the user did not permit this action");
        var before = _messages.Count;

        var verdict = await End(Ending(), Says("I could not send the report."));

        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Blocked), (verdict.Next, verdict.Kind));
        Assert.Equal(OutcomeCause.BlockedPermission, verdict.Cause);
        Assert.Equal(before, _messages.Count);
    }

    [Fact]
    public async Task Calls_still_open_after_the_step_was_told_go_to_its_reviewer_named()
    {
        _open.Failed(Call("run_command", """{"command":"wmic diskdrive get status"}"""), "'wmic' is not recognized");
        var ending = Ending(reviewed: true);

        var told = await End(ending, Says("The disks look fine."));
        var ended = await End(ending, Says("The disks look fine."));

        Assert.Equal(StepEnding.Next.Continue, told.Next);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (ended.Next, ended.Kind));
        Assert.Contains(_journal.Actions, a => a.Tool == "engine_open_calls");
    }

    [Fact]
    public async Task A_step_that_was_reminded_and_still_hands_nothing_on_is_unfinished()
    {
        var ending = Ending(schema: Findings, slot: new StepOutputSlot());

        await End(ending, Says("Done."));
        var ended = await End(ending, Says("Done."));

        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (ended.Next, ended.Kind));
        Assert.Contains("without handing on its declared output", ended.Reason);
    }
}

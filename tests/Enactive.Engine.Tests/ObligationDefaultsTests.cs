namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
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
/// The shipped limits of the obligation loop are the engine as it was before it (Plan Phase 2): one reminder per kind
/// of obligation, each kind on its own, the first always given. Characterization - written against nothing the loop
/// added (StepEnding as it was constructed, turns as they were), and run against the engine before the loop as well as
/// after it. Each case is one that a different default would change: a position that moves, a ceiling that binds, and
/// the three paths where a carve-out or the context guard could have held back a first reminder.
/// </summary>
public sealed class ObligationDefaultsTests : IDisposable
{
    private readonly EngineFixture _fx = new();
    private readonly ToolRegistry _tools = new(EngineFixture.ShippedTools());
    private readonly List<ChatMessage> _messages = [];
    private readonly ExecutionJournal _journal = new();
    public void Dispose() => _fx.Dispose();

    private StepFrame Frame(StepOutputSchema? schema = null, StepOutputSlot? slot = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, _fx.Workspace, _tools.Definitions, _messages, _journal, new ReadLedger(),
            _fx.Artifacts.BeginStep(), schema, slot);

    private StepEnding Ending(IReadOnlyList<SuccessCriterionDefinition>? criteria = null, StepFrame? frame = null)
        => new(frame ?? Frame(), (_, _) => Task.FromResult<string?>(null), criteria);

    private static StepEnding.Turn Says(string reply, ToolCall? described = null, int? prompt = null, int? window = null)
        => new(reply, null, false, described, 1, 0, null, prompt, () => window);

    /// <summary>
    /// One turn's ending. At the shipped limits nothing is ever "not told again": the engine before the loop said nothing
    /// when a kind's once-only flag was spent, and a release is announced only when something held back a reminder the
    /// loop would have added - which at one per kind it never adds.
    /// </summary>
    private static async Task<StepEnding.Verdict> End(StepEnding ending, StepEnding.Turn turn)
    {
        var verdict = new StepEnding.Verdict();
        await foreach (var e in ending.EndAsync(turn, verdict, CancellationToken.None))
            Assert.False(e.Summary.StartsWith("Not told again", StringComparison.Ordinal), e.Summary);
        return verdict;
    }

    private string LastTold => _messages[^1].Content ?? "";

    private static ToolCall Call(string name, string arguments) => new("c" + Guid.NewGuid().ToString("N")[..6], name, arguments);

    private static SuccessCriterionDefinition Exists(string name, string path)
        => new(name, "") { Step = 0, Typed = new TypedCriterion(TypedCriterionKind.FileExists, path) };

    private static readonly StepOutputSchema Findings = new("disk-findings", 1,
        [new StepOutputField("findings", StepOutputFieldType.Text, "What the disks came to")]);

    /// <summary>A position that moves after the reminder is not told again at the shipped limits: one of two criteria made good, and the step ends.</summary>
    [Fact]
    public async Task A_step_that_moves_after_its_reminder_is_not_told_again()
    {
        var ending = Ending([Exists("disk report", "disk-report.md"), Exists("summary", "summary.md")]);

        var first = await End(ending, Says("Done."));
        _fx.Write("disk-report.md", "all disks healthy");
        var before = _messages.Count;
        var second = await End(ending, Says("Wrote the report."));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (second.Next, second.Kind));
        Assert.Contains("summary.md", second.Reason);
        Assert.Equal(before, _messages.Count);
    }

    /// <summary>
    /// Every kind in one step, in the order of the rules, each told once: a described call, the criteria, the result, the
    /// open calls - four reminders, so a ceiling below the number of kinds would cut the last of them.
    /// </summary>
    [Fact]
    public async Task Every_kind_in_one_step_is_told_once_in_the_order_of_the_rules()
    {
        var ending = Ending([Exists("disk report", "disk-report.md")], Frame(Findings, new StepOutputSlot()));
        ending.Open.Failed(Call("run_command", """{"command":"wmic diskdrive get status"}"""), "'wmic' is not recognized");
        var told = new List<string>();

        var described = await End(ending, Says("Done.", described: Call("read_file", """{"path":"disks.md"}""")));
        told.Add(LastTold);
        var criteria = await End(ending, Says("Done."));
        told.Add(LastTold);
        var output = await End(ending, Says("Done."));
        told.Add(LastTold);
        var open = await End(ending, Says("Done."));
        told.Add(LastTold);
        var ended = await End(ending, Says("Done."));

        Assert.All(new[] { described, criteria, output, open }, v => Assert.Equal(StepEnding.Next.Continue, v.Next));
        Assert.Contains("instead of invoking it", told[0], StringComparison.Ordinal);
        Assert.StartsWith("Before this step ends: the plan checks this step's work", told[1]);
        Assert.StartsWith("This step is not finished until it hands its result on", told[2]);
        Assert.StartsWith("Before this step ends: this call did not go through", told[3]);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (ended.Next, ended.Kind));
        Assert.StartsWith("unresolved tool call:", ended.Reason);
    }

    /// <summary>
    /// Run dd7ca94b's shape: a step with a declared output and a call it was refused - a block, as the engine reads it -
    /// is reminded to hand its result on, hands it on, the refusal is forgiven by the hand-over, and the step is done.
    /// </summary>
    [Fact]
    public async Task A_step_with_a_refused_call_is_reminded_of_its_result_hands_it_on_and_succeeds()
    {
        var slot = new StepOutputSlot();
        var ending = Ending(frame: Frame(Findings, slot));
        ending.Open.Refused(Call("send_email", """{"to":"ops@example.com"}"""), "the user did not permit this action");

        var reminded = await End(ending, Says("The disks are fine; I could not mail it."));
        var remindedOf = LastTold;
        slot.Accept(new StepOutputContract.Verdict(new JsonObject { ["findings"] = "all healthy" }, [], [], []));
        ending.Open.HandedOn();
        var ended = await End(ending, Says("Handed on."));

        Assert.Equal(StepEnding.Next.Continue, reminded.Next);
        Assert.StartsWith("This step is not finished until it hands its result on", remindedOf);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (ended.Next, ended.Kind));
    }

    /// <summary>
    /// Run 68f92f's shape: a step that only looked for the file it was to write and found nothing - a block, as the engine
    /// reads it - is told of the plan's criterion, writes the file, and is done.
    /// </summary>
    [Fact]
    public async Task A_step_that_found_nothing_is_told_of_its_criterion_writes_the_file_and_succeeds()
    {
        var ending = Ending([Exists("coverage report", "coverage.md")]);
        ending.Open.FoundNothing(Call("read_file", """{"path":"coverage.md"}"""), "File not found");

        var told = await End(ending, Says("The report is already there under another name."));
        var toldOf = LastTold;
        var write = Call("write_file", """{"path":"coverage.md","content":"covered"}""");
        _fx.Write("coverage.md", "covered");
        ending.Open.Succeeded(write, []);
        var ended = await End(ending, Says("Written."));

        Assert.Equal(StepEnding.Next.Continue, told.Next);
        Assert.StartsWith("Before this step ends: the plan checks this step's work", toldOf);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (ended.Next, ended.Kind));
    }

    /// <summary>A prompt past four fifths of the window still gets the first reminder: that turn is what the trim and the handover are for.</summary>
    [Fact]
    public async Task A_step_near_its_window_still_gets_its_first_reminder()
    {
        var verdict = await End(Ending([Exists("disk report", "disk-report.md")]), Says("Done.", prompt: 7900, window: 8192));

        Assert.Equal(StepEnding.Next.Continue, verdict.Next);
        Assert.StartsWith("Before this step ends: the plan checks this step's work", LastTold);
    }

    /// <summary>
    /// A described call in a run whose token budget the turn used up is asked to be re-sent, and the tool loop ends the
    /// step on the budget before another model call - the run is incomplete on its budget, not done on the description.
    /// </summary>
    [Fact]
    public async Task A_described_call_with_the_budget_spent_ends_incomplete_on_the_budget()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}""").Reporting(prompt: 5, completion: 5),
            Turn.Says("""
                ```json
                {"path":"notes.txt","content":"x"}
                ```
                """).Reporting(prompt: 60, completion: 10));

        var events = await fx.RunAsync(fx.Build(provider, limits: new ExecutionLimits(MaxTokens: 50)), "write a file");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed).Outcome());
        Assert.Contains("token(s)", events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed).OutcomeReason());
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(events, e => e.Summary.StartsWith("Not told again", StringComparison.Ordinal));
    }
}

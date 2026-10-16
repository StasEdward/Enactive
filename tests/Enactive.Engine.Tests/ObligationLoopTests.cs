namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What a step is told before it ends, and when it is no longer told (Plan Phase 2).
///
/// <para>Each obligation used to be told once, by a flag of its own: a nudge that worked in part - four of six fields
/// handed on, one of two failing calls made good - was followed by the end of the step anyway. The obligation loop keeps
/// telling while what the engine sees of the step changes, and lets go when it does not, within a limit per kind and a
/// ceiling per step.</para>
/// </summary>
public sealed class ObligationLoopTests : IDisposable
{
    private readonly EngineFixture _fx = new();
    private readonly ToolRegistry _tools = new(EngineFixture.ShippedTools());
    private readonly List<ChatMessage> _messages = [];
    private readonly ExecutionJournal _journal = new();
    public void Dispose() => _fx.Dispose();

    private StepFrame Frame(StepOutputSchema? schema = null, StepOutputSlot? slot = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, _fx.Workspace, _tools.Definitions, _messages, _journal, new ReadLedger(),
            _fx.Artifacts.BeginStep(), schema, slot);

    private static StepEnding.Turn Says(string? reply, ToolCall? described = null, int? prompt = null, int? window = null)
        => new(reply, null, false, described, 1, 0, null, prompt, () => window);

    private static async Task<(StepEnding.Verdict Verdict, List<WorkEvent> Events)> End(StepEnding ending, StepEnding.Turn turn)
    {
        var verdict = new StepEnding.Verdict();
        var events = new List<WorkEvent>();
        await foreach (var e in ending.EndAsync(turn, verdict, CancellationToken.None))
            events.Add(e);
        return (verdict, events);
    }

    private string LastTold => _messages[^1].Content ?? "";

    private static ToolCall Call(string name, string arguments) => new("c" + Guid.NewGuid().ToString("N")[..6], name, arguments);

    private static SuccessCriterionDefinition DiskReport()
        => new("disk report", "") { Step = 0, Typed = new TypedCriterion(TypedCriterionKind.FileExists, "disk-report.md") };

    private static readonly StepOutputSchema Findings = new("disk-findings", 1,
        [new StepOutputField("findings", StepOutputFieldType.Text, "What the disks came to")]);

    // ── the defaults are today's sequence ───────────────────────────────────

    public static TheoryData<string> Kinds => new() { "described", "criteria", "output", "open calls" };

    /// <summary>
    /// Characterization: with the shipped limits each kind of obligation is told once and then let go with the outcome
    /// it always had - the compatible default is one per kind and a ceiling of the number of kinds, not one in all.
    /// Passes before the loop and after it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task With_the_shipped_limits_each_kind_is_told_once_and_then_ends_as_it_always_did(string kind)
    {
        var described = Call("read_file", """{"path":"disks.md"}""");
        var ending = kind switch
        {
            "criteria" => new StepEnding(Frame(), (_, _) => Task.FromResult<string?>(null), [DiskReport()]),
            "output" => new StepEnding(Frame(Findings, new StepOutputSlot()), (_, _) => Task.FromResult<string?>(null)),
            _ => new StepEnding(Frame(), (_, _) => Task.FromResult<string?>(null))
        };
        if (kind == "open calls")
            ending.Open.Failed(Call("run_command", """{"command":"wmic diskdrive get status"}"""), "'wmic' is not recognized");
        StepEnding.Turn Turn() => Says("Done.", described: kind == "described" ? described : null);

        var (first, _) = await End(ending, Turn());
        var told = LastTold;
        var (second, events) = await End(ending, Turn());

        Assert.DoesNotContain(events, e => e.Summary.StartsWith("Not told again", StringComparison.Ordinal));
        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Contains(kind switch
        {
            "described" => "instead of invoking it",
            "criteria" => "the plan checks this step's work",
            "output" => "is not finished until it hands its result on",
            _ => "did not go through"
        }, told, StringComparison.Ordinal);
        Assert.Equal(StepEnding.Next.End, second.Next);
        Assert.Equal(kind == "described" ? StepOutcomeKind.Succeeded : StepOutcomeKind.Incomplete, second.Kind);
    }

    // ── the loop itself: progress is a new position ─────────────────────────

    private static ObligationSignature At(params string[] failing)
        => new(1, failing, OutputOwed: false, MissingFields: [], InvalidFields: [], OpenCalls: [], Described: null);

    /// <summary>A new position is progress and earns another nudge; a position seen before is not, whatever the limit.</summary>
    [Fact]
    public void A_new_position_is_progress_and_one_seen_before_is_not()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 3, Ceiling: 12));

        Assert.True(loop.Nudge(Obligation.Criteria, At("a", "b")).Nudge);
        Assert.True(loop.Nudge(Obligation.Criteria, At("b")).Nudge);
        var back = loop.Nudge(Obligation.Criteria, At("a", "b"));

        Assert.False(back.Nudge);
        Assert.Contains("nothing the engine can see has changed", back.Why, StringComparison.Ordinal);
    }

    /// <summary>A -> B -> A cannot reset the count: an alternation between two positions is told twice and then let go.</summary>
    [Fact]
    public void An_alternation_is_released_and_not_looped()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 3, Ceiling: 8));

        var told = Enumerable.Range(0, 10).Count(i => loop.Nudge(Obligation.Criteria, At(i % 2 == 0 ? "a" : "b")).Nudge);

        Assert.Equal(2, told);
    }

    /// <summary>
    /// The ceiling holds over every kind together, and counts only the reminders the loop adds: a small one stops the
    /// second reminder of a kind, never the first of another - the first of each kind is untouchable.
    /// </summary>
    [Fact]
    public void A_small_ceiling_stops_added_reminders_and_never_a_first_one()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 3, Ceiling: 1));

        var criteria = new[] { "a", "b", "c" }.Select(p => loop.Nudge(Obligation.Criteria, At(p))).ToArray();
        var firsts = new[] { Obligation.DescribedCall, Obligation.Output, Obligation.OpenCalls }
            .Select(kind => loop.Nudge(kind, At("x"))).ToArray();
        var addedOutput = loop.Nudge(Obligation.Output, At("y"));

        Assert.Equal([true, true, false], criteria.Select(d => d.Nudge));
        Assert.Contains("in all", criteria[2].Why, StringComparison.Ordinal);
        Assert.All(firsts, d => Assert.True(d.Nudge));
        Assert.False(addedOutput.Nudge);
    }

    /// <summary>A ceiling of 0 lets no kind be told twice, and every kind be told once.</summary>
    [Fact]
    public void A_ceiling_of_nothing_still_gives_every_kind_its_first_reminder()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 2, Ceiling: 0));

        Assert.All(Enum.GetValues<Obligation>(), kind => Assert.True(loop.Nudge(kind, At("a")).Nudge));
        Assert.All(Enum.GetValues<Obligation>(), kind => Assert.False(loop.Nudge(kind, At("b")).Nudge));
    }

    /// <summary>
    /// The default ceiling grows with the limit per kind: it is there to cap what the loop adds, and at two per kind every
    /// kind may be told twice - a fixed ceiling of four would have let the first two kinds use it up.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void The_default_ceiling_is_every_added_reminder_the_limit_per_kind_allows(int perKind, int ceiling)
        => Assert.Equal(ceiling, new ObligationLoop(new ObligationNudgeLimits(PerKind: perKind)).Ceiling);

    [Fact]
    public void The_same_position_twice_is_released_after_one_nudge_however_high_the_limit()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 3, Ceiling: 12));

        Assert.True(loop.Nudge(Obligation.OpenCalls, At("a")).Nudge);
        Assert.False(loop.Nudge(Obligation.OpenCalls, At("a")).Nudge);
    }

    /// <summary>Each kind counts apart: a position the step was told of for one obligation may still be told for another.</summary>
    [Fact]
    public void Each_kind_has_its_own_limit_and_its_own_positions()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 1, Ceiling: 4));

        Assert.True(loop.Nudge(Obligation.Criteria, At("a")).Nudge);
        Assert.False(loop.Nudge(Obligation.Criteria, At("b")).Nudge);
        Assert.True(loop.Nudge(Obligation.Output, At("a")).Nudge);
    }

    /// <summary>A limit above the hard ceiling in code is clamped, so a stray number in the settings cannot buy a step forty turns.</summary>
    [Fact]
    public void A_limit_above_the_hard_ceiling_is_clamped()
    {
        var loop = new ObligationLoop(new ObligationNudgeLimits(PerKind: 10, Ceiling: 100));

        var told = Enumerable.Range(0, 10).Count(i => loop.Nudge(Obligation.Output, At("field" + i)).Nudge);

        Assert.Equal(ObligationLoop.HardPerKind, told);
        Assert.Equal((ObligationLoop.HardPerKind - 1) * ObligationLoop.KindCount, loop.Ceiling);
    }

    [Fact]
    public void Nothing_owed_is_never_told()
        => Assert.False(new ObligationLoop(new ObligationNudgeLimits()).Nudge(Obligation.Criteria, At()).Nudge);

    /// <summary>
    /// The compatible default is one per kind - each kind told once, as by its old flag - with the default ceiling, which
    /// at one per kind adds nothing to cap, attended and unattended alike.
    /// </summary>
    [Fact]
    public void The_shipped_limits_are_one_per_kind_and_nothing_added()
    {
        var shipped = EngineOptions.Default.ObligationNudges;

        Assert.Equal(ObligationLoop.KindCount, Enum.GetValues<Obligation>().Length);
        Assert.Equal(new ObligationNudgeLimits(1), shipped.Attended);
        Assert.Equal(new ObligationNudgeLimits(1), shipped.Unattended);
        Assert.Null(shipped.Attended.Ceiling);
        Assert.Equal(0, new ObligationLoop(shipped.Attended).Ceiling);
    }

    // ── the step's ending, through the loop ─────────────────────────────────

    private StepEnding Ending(ObligationNudgeLimits? limits = null, IReadOnlyList<SuccessCriterionDefinition>? criteria = null,
        StepFrame? frame = null)
        => new(frame ?? Frame(), (_, _) => Task.FromResult<string?>(null), criteria, nudges: limits);

    private static SuccessCriterionDefinition Exists(string name, string path)
        => new(name, "") { Step = 0, Typed = new TypedCriterion(TypedCriterionKind.FileExists, path) };

    /// <summary>
    /// The signature is the engine's own state, never the model's text: a step that says it fixed it while nothing it
    /// can be checked on changed is let go, however many nudges are left - and the timeline says which and why.
    /// </summary>
    [Fact]
    public async Task A_step_that_says_it_fixed_it_without_changing_anything_is_let_go()
    {
        var ending = Ending(new ObligationNudgeLimits(PerKind: 3, Ceiling: 12), [DiskReport()]);

        var (first, _) = await End(ending, Says("Done."));
        var before = _messages.Count;
        var (second, events) = await End(ending, Says("I fixed it, the report is there now."));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (second.Next, second.Kind));
        Assert.Equal(before, _messages.Count);
        Assert.Contains(events, e => e.Summary.StartsWith("Not told again of the plan's criteria for this step", StringComparison.Ordinal)
                                     && e.Summary.Contains("nothing the engine can see has changed", StringComparison.Ordinal));
    }

    /// <summary>A nudge that worked in part is followed by another while the limit allows: one of two criteria made good is progress.</summary>
    [Fact]
    public async Task A_step_that_made_one_of_two_criteria_good_is_told_again_and_then_succeeds()
    {
        var ending = Ending(new ObligationNudgeLimits(PerKind: 2, Ceiling: 4),
            [Exists("disk report", "disk-report.md"), Exists("summary", "summary.md")]);

        var (first, _) = await End(ending, Says("Done."));
        _fx.Write("disk-report.md", "all disks healthy");
        var (second, _) = await End(ending, Says("Wrote the report."));
        var toldAgain = LastTold;
        _fx.Write("summary.md", "healthy");
        var (third, _) = await End(ending, Says("Wrote the summary."));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Equal(StepEnding.Next.Continue, second.Next);
        Assert.Contains("summary.md", toldAgain, StringComparison.Ordinal);
        Assert.DoesNotContain("disk-report.md", toldAgain, StringComparison.Ordinal);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Succeeded), (third.Next, third.Kind));
    }

    /// <summary>
    /// The loop's state lives for one attempt: a step tried again after a review starts with every kind untold. The output
    /// reminder used to be the one exception - its flag sat on the slot and outlived the attempt - so a second attempt
    /// that had handed nothing on ended unfinished without hearing it.
    /// </summary>
    [Fact]
    public async Task What_a_step_was_told_is_counted_per_attempt_and_kept_through_a_handover()
    {
        var slot = new StepOutputSlot();
        var frame = Frame(Findings, slot);
        var attempt = Ending(frame: frame);

        var (told, _) = await End(attempt, Says("Done."));
        frame.StartOver();
        var (afterHandover, _) = await End(attempt, Says("Done."));
        var (nextAttempt, _) = await End(Ending(frame: Frame(Findings, slot)), Says("Done."));

        Assert.Equal(StepEnding.Next.Continue, told.Next);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (afterHandover.Next, afterHandover.Kind));
        Assert.Equal(StepEnding.Next.Continue, nextAttempt.Next);
        Assert.StartsWith("This step is not finished until it hands its result on", LastTold);
    }

    // ── what is not told again ──────────────────────────────────────────────
    //
    // The carve-outs and the context guard hold back only the reminders the loop adds: the first of each kind is given
    // as it always was (ObligationDefaultsTests pins that at the shipped limits). Each case below moves the step after
    // its first reminder, so only the carve-out can be what keeps the second one back.

    private static readonly ObligationNudgeLimits TwoPerKind = new(PerKind: 2, Ceiling: 8);

    private StepEnding TwoCriteria()
        => Ending(TwoPerKind, [Exists("disk report", "disk-report.md"), Exists("summary", "summary.md")]);

    /// <summary>
    /// Context guard: every further nudge is a turn with the whole prompt, and near the window there is no room for one.
    /// The first reminder is still given there - it is the turn the trim and the handover exist for. The timeline says
    /// why the step was not told again; with no window stated the loop does not guess one.
    /// </summary>
    [Theory]
    [InlineData(950, 1000, false)]
    [InlineData(950, null, true)]
    [InlineData(500, 1000, true)]
    public async Task Near_the_context_window_a_step_is_told_once_and_not_again_and_the_timeline_says_why(int prompt, int? window, bool toldAgain)
    {
        var ending = TwoCriteria();

        var (first, _) = await End(ending, Says("Done.", prompt: prompt, window: window));
        _fx.Write("disk-report.md", "all disks healthy");
        var (second, events) = await End(ending, Says("Wrote the report.", prompt: prompt, window: window));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Equal(toldAgain ? StepEnding.Next.Continue : StepEnding.Next.End, second.Next);
        if (!toldAgain)
        {
            Assert.Equal(StepOutcomeKind.Incomplete, second.Kind);
            Assert.Contains(events, e => e.Summary.StartsWith("Not told again of the plan's criteria for this step", StringComparison.Ordinal)
                                         && e.Summary.Contains("950 of", StringComparison.Ordinal)
                                         && e.Summary.Contains("1000", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A kind told as often as its limit allows is let go by that limit alone, and silently: a carve-out that also holds
    /// is not named as the reason - here the window, which used to be said for a step that would not have been told
    /// again anyway.
    /// </summary>
    [Fact]
    public async Task A_kind_at_its_limit_is_let_go_without_naming_a_carve_out()
    {
        var ending = Ending(new ObligationNudgeLimits(PerKind: 1, Ceiling: 4), [Exists("disk report", "disk-report.md"), Exists("summary", "summary.md")]);

        var (first, _) = await End(ending, Says("Done.", prompt: 950, window: 1000));
        _fx.Write("disk-report.md", "all disks healthy");
        var (second, events) = await End(ending, Says("Wrote the report.", prompt: 950, window: 1000));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (second.Next, second.Kind));
        Assert.DoesNotContain(events, e => e.Summary.StartsWith("Not told again", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first reminder of each kind is untouchable: at two per kind, a step told of a described call, twice of its
    /// criteria and once of its result is still told of its open calls - run 0cf51c's reminder - where a ceiling of four
    /// in all used to have been spent.
    /// </summary>
    [Fact]
    public async Task At_two_per_kind_a_step_that_owes_every_kind_is_told_of_each_at_least_once()
    {
        var ending = Ending(new ObligationNudgeLimits(PerKind: 2), [Exists("disk report", "disk-report.md"), Exists("summary", "summary.md")],
            Frame(Findings, new StepOutputSlot()));
        ending.Open.Failed(Call("run_command", """{"command":"wmic diskdrive get status"}"""), "'wmic' is not recognized");
        var told = new List<string>();

        await End(ending, Says("Done.", described: Call("read_file", """{"path":"disks.md"}""")));
        told.Add(LastTold);
        await End(ending, Says("Done."));
        told.Add(LastTold);
        _fx.Write("disk-report.md", "all disks healthy");
        await End(ending, Says("Wrote the report."));
        told.Add(LastTold);
        await End(ending, Says("Done."));
        told.Add(LastTold);
        var (open, _) = await End(ending, Says("Done."));
        told.Add(LastTold);

        Assert.Contains("instead of invoking it", told[0], StringComparison.Ordinal);
        Assert.StartsWith("Before this step ends: the plan checks this step's work", told[1]);
        Assert.StartsWith("Before this step ends: the plan checks this step's work", told[2]);
        Assert.StartsWith("This step is not finished until it hands its result on", told[3]);
        Assert.Equal(StepEnding.Next.Continue, open.Next);
        Assert.StartsWith("Before this step ends: this call did not go through", told[4]);
    }

    /// <summary>
    /// A step the engine sees is blocked is told of its criteria once, as always, and not again: what blocks it is for a
    /// person to put right, and it ends blocked.
    /// </summary>
    [Fact]
    public async Task A_blocked_step_is_told_of_its_criteria_once_and_not_again()
    {
        var ending = TwoCriteria();
        ending.Open.Refused(Call("send_email", """{"to":"ops@example.com"}"""), "the user did not permit this action");

        var (first, _) = await End(ending, Says("I could not send the report."));
        _fx.Write("disk-report.md", "all disks healthy");
        var before = _messages.Count;
        var (second, events) = await End(ending, Says("Wrote the report; still no mail."));

        Assert.Equal(StepEnding.Next.Continue, first.Next);
        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Blocked), (second.Next, second.Kind));
        Assert.Equal(before, _messages.Count);
        Assert.Contains(events, e => e.Summary.Contains("the step is blocked", StringComparison.Ordinal));
    }

    /// <summary>Two criteria with the same name are two positions: making one of them good is progress.</summary>
    [Fact]
    public async Task Two_criteria_with_one_name_are_told_apart()
    {
        var ending = Ending(TwoPerKind, [Exists("a file", "disk-report.md"), Exists("a file", "summary.md")]);

        await End(ending, Says("Done."));
        _fx.Write("disk-report.md", "all disks healthy");
        var (second, _) = await End(ending, Says("Wrote one."));

        Assert.Equal(StepEnding.Next.Continue, second.Next);
    }

    /// <summary>A kind a limit of 0 lets go untold is said to have ended without being told - not "after being told 0 times".</summary>
    [Fact]
    public async Task A_kind_let_go_untold_says_so()
    {
        var (verdict, events) = await End(Ending(new ObligationNudgeLimits(PerKind: 0, Ceiling: 0), [DiskReport()]), Says("Done."));

        Assert.Equal((StepEnding.Next.End, StepOutcomeKind.Incomplete), (verdict.Next, verdict.Kind));
        Assert.Contains(events, e => e.Summary.Contains("still failing, without being told:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Summary.Contains("0 times", StringComparison.Ordinal));
    }

    // ── refusal state the engine can see ────────────────────────────────────

    private static readonly StepOutputSchema SixFields = new("six", 1,
        new[] { "a", "b", "c", "d", "e", "f" }.Select(n => new StepOutputField(n, StepOutputFieldType.Text, "field " + n)).ToArray());

    private const string SixOfSix = """{"a":"1","b":"2","c":"3","d":"4","e":"5","f":"6"}""";
    private const string FourOfSix = """{"a":"1","b":"2","c":"3","d":"4"}""";

    private CallAdmission Admission(StepOutputSchema schema, StepOutputSlot slot)
    {
        var frame = new StepFrame(Guid.NewGuid(), Guid.NewGuid(), 1, _fx.Workspace, _tools.Definitions, _messages, _journal,
            new ReadLedger(), _fx.Artifacts.BeginStep(), schema, slot);
        var worker = EngineFixture.WorkerWith("read_file");
        var policy = AutonomyTiers.PolicyFor(2);
        var offer = ToolOffers.For(["read_file"], _ => PermissionDecision.Allow, _ => true);
        return new CallAdmission(frame, _tools, new PermissionEngine(), worker, policy, offer,
            new TaskProgress(_fx.Root), _fx.Decisions, new SemaphoreSlim(1, 1), new Enactive.Core.Context.GrantedRoots(),
            new WritableRoots(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-roots.json")),
            null, false);
    }

    /// <summary>
    /// A refused submission was a message string: "4 of 6 fields" was nowhere the engine could read it. The slot keeps
    /// which required fields were missing and which were not valid, apart from the result it accepted last.
    /// </summary>
    [Fact]
    public void A_refused_four_of_six_records_the_two_missing_and_leaves_the_accepted_values_alone()
    {
        var slot = new StepOutputSlot();
        var admission = Admission(SixFields, slot);

        _ = admission.HandOn(Call(StepOutputContract.ToolName, SixOfSix), answered: false).ToList();
        var accepted = slot.Values!.ToJsonString();
        _ = admission.HandOn(Call(StepOutputContract.ToolName, FourOfSix), answered: false).ToList();

        Assert.Equal(["e", "f"], slot.MissingFields);
        Assert.Empty(slot.InvalidFields);
        Assert.Equal(accepted, slot.Values!.ToJsonString());
        Assert.NotNull(slot.LastRefused);
    }

    [Fact]
    public void A_field_sent_wrong_is_recorded_as_not_valid_and_an_accepted_submission_clears_the_refusal()
    {
        var slot = new StepOutputSlot();
        var admission = Admission(SixFields, slot);

        _ = admission.HandOn(Call(StepOutputContract.ToolName, """{"a":"1","b":"2","c":"3","d":"4","e":"5","f":7}"""), answered: false).ToList();
        var invalid = slot.InvalidFields;
        _ = admission.HandOn(Call(StepOutputContract.ToolName, SixOfSix), answered: false).ToList();

        Assert.Equal(["f"], invalid);
        Assert.Empty(slot.MissingFields);
        Assert.Empty(slot.InvalidFields);
        Assert.Null(slot.LastRefused);
    }

    // ── through a run ───────────────────────────────────────────────────────

    private const string SixFieldPlan = """
        {"disposition":"task","title":"six fields",
         "steps":[{"title":"collect","dependsOn":[],"output":{
            "a":{"type":"text","description":"a"},"b":{"type":"text","description":"b"},"c":{"type":"text","description":"c"},
            "d":{"type":"text","description":"d"},"e":{"type":"text","description":"e"},"f":{"type":"text","description":"f"}}}]}
        """;

    /// <summary>
    /// THE ONE THE LOOP IS FOR: told to hand its result on, the step sends four of six fields and is refused; told again -
    /// its position changed - it sends all six and the step succeeds. With one nudge per kind it ended after the first.
    /// </summary>
    [Theory]
    [InlineData(2, RunOutcomeKind.Completed)]
    [InlineData(1, RunOutcomeKind.Incomplete)]
    public async Task Four_of_six_after_one_nudge_and_six_of_six_after_another_succeeds_with_two_per_kind(int perKind, RunOutcomeKind outcome)
    {
        using var fx = new EngineFixture { StepOutputs = true, ObligationNudges = new() { Attended = new(perKind, 4) } };
        var worker = new FakeChatProvider(
            Turn.Says(SixFieldPlan),
            Turn.Says("Collected."),
            Turn.Calls1(StepOutputContract.ToolName, FourOfSix, "s1"),
            Turn.Says("Handed on."),
            Turn.Calls1(StepOutputContract.ToolName, SixOfSix, "s2"),
            Turn.Says("Handed on, all six."));

        var events = await fx.RunAsync(fx.Build(worker), "collect six things");

        Assert.Equal(outcome, events.Last().Outcome());
    }

    // ── unattended is the run's, not the decision handler's ─────────────────

    private sealed class DenyEverything : IDecisionHandler
    {
        public bool CanApprove => false;
        public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
            => Task.FromResult(new DecisionOutcome("deny"));
    }

    private static Turn Described(string content) => Turn.Says($$"""
        ```json
        {"path":"notes.txt","content":"{{content}}"}
        ```
        """);

    /// <summary>
    /// A run nobody watches (the window's background run, a schedule) takes the unattended pair; a console run under
    /// "--approve deny" is watched, though nothing it asks can be approved, and takes the attended pair. CanApprove is
    /// false for both, so it cannot be what decides.
    /// </summary>
    [Theory]
    [InlineData(IntentSource.Inbox, "parking", 2)]
    [InlineData(IntentSource.Schedule, "unattended", 2)]
    [InlineData(IntentSource.CommandBar, "deny", 1)]
    public async Task An_unattended_run_takes_the_unattended_limits_and_approve_deny_does_not(IntentSource source, string handler, int reAsks)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""),
            Described("one"), Described("two"), Described("three"), Described("four"));
        var models = new ModelResolver();
        var worker = EngineFixture.WorkerWith("write_file", "read_file");
        var settings = new AppSettings
        {
            Engine = new()
            {
                ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0,
                ObligationNudges = new() { Attended = new(1, 4), Unattended = new(2, 8) }
            }
        };
        var engine = new ComposedEngine(new SingleProviderFactory(provider), new StaticWorkerProvider([worker], worker.Id),
            new ModelRouter(models), models, new ToolRegistry(EngineFixture.ShippedTools()), [], settings, new LogHub(),
            new SessionApprovals())
        {
            Planner = new Planner(checksAuditEnabled: false),
            Approvals = new ApprovalStore(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-permissions.json"))
        };
        IDecisionHandler decisions = handler switch
        {
            "parking" => new ParkingDecisionHandler(),
            "unattended" => new UnattendedDecisionHandler(),
            _ => new DenyEverything()
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var composed = await RunComposer.ComposeAsync(engine,
                         new RunRequest(fx.Workspace, "write a file", source) { Defaults = new(3) }, decisions, cts.Token))
            await foreach (var _ in composed.Events(cts.Token)) { }

        // Every request carries the conversation so far: the most re-asks any one of them holds is how many were made.
        var asked = provider.Requests.Max(r => r.Messages.Count(m => m.Role == ChatRole.User
            && m.Content?.Contains("instead of invoking it", StringComparison.Ordinal) == true));
        Assert.Equal(reAsks, asked);
    }
}

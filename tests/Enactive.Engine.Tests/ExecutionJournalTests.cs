namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// What a step DID, kept apart from what it SAID.
///
/// <para>The reviewer is asked to judge a step against "the ACTUAL commands run and their real
/// stdout/stderr/exit codes", and that evidence was assembled by reading the model's own
/// conversation back. The conversation is the model's WORKING MEMORY: once it has to be shortened to
/// fit a context window, the tool results are replaced by a stub — so on exactly the runs under the
/// most pressure the reviewer was handed less evidence, with nothing saying so, and a gate that
/// passes for lack of evidence is not a gate.</para>
///
/// <para>It is recorded now at the moment each call returns, in a journal nothing that shortens a
/// prompt can reach.</para>
/// </summary>
public sealed class ExecutionJournalTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"run it"}""";

    /// <summary>Everything the reviewer was shown, across its requests.</summary>
    private static string PromptOf(FakeChatProvider reviewer)
        => string.Join("\n", reviewer.Requests.SelectMany(r => r.Messages).Select(m => m.Content ?? ""));

    // ── the journal itself ────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_journal_says_nothing_ran_rather_than_nothing_at_all()
        => Assert.Equal("(no tools were run in this step)", new ExecutionJournal().Describe().Text);

    [Fact]
    public void A_refusal_is_recorded_as_one_and_not_as_a_failure()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "write_file", """{"path":"a.md"}""", ActionOutcome.Refused, "the user did not permit this action");

        var evidence = journal.Describe().Text;

        Assert.Contains("REFUSED", evidence, StringComparison.Ordinal);
        Assert.Contains("did not permit", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", evidence, StringComparison.Ordinal);
    }

    // A review judges the CURRENT attempt. A retry starts from the mark, so a rejected attempt's
    // actions are not re-shown as though they were this one's.
    [Fact]
    public void A_mark_separates_one_attempt_from_the_next()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", "{}", ActionOutcome.Succeeded, "FIRST ATTEMPT");

        var second = journal.Mark();
        journal.Record(1, "run_command", "{}", ActionOutcome.Succeeded, "SECOND ATTEMPT");

        Assert.Contains("FIRST ATTEMPT", journal.Describe().Text, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST ATTEMPT", journal.Describe(second).Text, StringComparison.Ordinal);
        Assert.Contains("SECOND ATTEMPT", journal.Describe(second).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Still capped, and still says so — but the cap now falls on the OUTPUTS and the calls survive.
    /// It used to cut the whole text at the tail, which lost the LAST calls of a step and let a
    /// reviewer conclude that work it could not see had never happened (see EvidenceBudgetTests).
    /// </summary>
    [Fact]
    public void A_long_transcript_of_actions_is_capped_and_says_so()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 200; i++)
            journal.Record(1, "run_command", "{}", ActionOutcome.Succeeded, new string('x', 200));

        var evidence = journal.Describe(maxChars: 3000).Text;

        Assert.True(evidence.Length < 3600, $"{evidence.Length} characters");
        Assert.Contains("200 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.Contains("not shown here", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void It_can_say_whether_a_command_tool_was_used()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "write_file", "{}", ActionOutcome.Succeeded, "written");

        var tools = new[] { "run_command", "run_powershell" };
        Assert.False(journal.UsedAny(tools));

        journal.Record(1, "run_command", "{}", ActionOutcome.Succeeded, "exit code 0");
        Assert.True(journal.UsedAny(tools));
    }

    // ── through the engine ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_reviewer_is_shown_what_actually_ran()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo MARKER_ONE"}""", "c1"),
            Turn.Says("Ran it."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer),
            "run the command");

        Assert.Contains("MARKER_ONE", PromptOf(reviewer), StringComparison.Ordinal);
    }

    // The one that matters. A transcript shortened to fit the window loses its tool results — and
    // the reviewer used to lose them with it, on the run that could least afford a weaker gate.
    [Fact]
    public async Task The_evidence_survives_a_context_trim()
    {
        using var fx = new EngineFixture();

        var bulky = new string('x', 20000);

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo MARKER_ONE"}""", "c1"),
            Turn.Calls1("write_file", $$"""{"path":"big.txt","content":"{{bulky}}"}""", "c2"),
            // The weight arrives as tool RESULTS as well as the write's own arguments, so the trim has
            // plenty of OLD material to drop. (For two days in September 2026 a write's arguments were
            // shortened as soon as they were recorded; that misled models about their own work and
            // was withdrawn - see AModelSeesWhatItDidTests.)
            Turn.Calls1("read_file", """{"path":"big.txt"}""", "c3"),
            Turn.Calls1("read_file", """{"path":"big.txt"}""", "c4"),
            Turn.Says("Ran it and wrote it."))
        {
            Window = 4096
        };

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer),
            "run the command and write the file");

        Assert.Contains(events, e => e.Kind == EventKind.ContextTrimmed);

        // The transcript no longer holds it...
        var lastWorkerPrompt = string.Join("\n", worker.Requests[^1].Messages.Select(m => m.Content ?? ""));
        Assert.DoesNotContain("MARKER_ONE", lastWorkerPrompt, StringComparison.Ordinal);

        // ...and the reviewer was shown it anyway.
        Assert.Contains("MARKER_ONE", PromptOf(reviewer), StringComparison.Ordinal);
    }

    // A refused call is evidence too: "the user did not permit this" is exactly the sort of thing a
    // reviewer must not have to infer from silence — and it stays in the record even after the same
    // call is allowed on a second ask and succeeds, which is what clears it everywhere else.
    [Fact]
    public async Task A_refusal_stays_in_the_evidence_even_after_the_call_is_later_allowed()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo MARKER_ONE"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"a.md","content":"x"}""", "c2"),
            Turn.Calls1("write_file", """{"path":"a.md","content":"x"}""", "c3"),
            Turn.Says("Done."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        // write_file has to be asked about; the first ask is refused and the second allowed.
        var policy = new Enactive.Core.Permissions.PermissionPolicy(
            Enactive.Core.Permissions.PermissionLevel.Execute,
            new[] { "*" },
            new[] { "write_file" });

        fx.Decisions.Script.Enqueue("deny");
        fx.Decisions.Script.Enqueue("allow");

        await fx.RunAsync(
            fx.Build(worker,
                     worker: EngineFixture.WorkerWith("run_command", "write_file"),
                     policy: policy,
                     router: Routers.WithReviewer(),
                     reviewProvider: reviewer),
            "run and write");

        var shown = PromptOf(reviewer);

        Assert.Contains("REFUSED", shown, StringComparison.Ordinal);
        Assert.Contains("did not permit", shown, StringComparison.Ordinal);

        // And the work that did happen is there beside it.
        Assert.Contains("MARKER_ONE", shown, StringComparison.Ordinal);
    }
}

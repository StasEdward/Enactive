namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// The evidence window is the transcript window — between STEPS, not only between attempts.
///
/// <para><b>§9f, one scope up.</b> That section fixed a step whose two attempts shared a transcript
/// while the evidence covered only the last one: <i>"there is no winning move in that gap - doing
/// only what the reviewer asked is what produces it."</i> The rule it wrote down was that the
/// evidence window is the transcript window. It was applied to attempts and not to steps.</para>
///
/// <para>At <c>MaxParallelSteps == 1</c> the whole plan shares one conversation: step 2 can see
/// every call step 1 made, and did. The journal was created per step, so the reviewer saw only step
/// 2's. Reported 2026-09-08 15:04 — step 2 read the csproj, reported on the five files step 1 had
/// read, and was rejected: <i>"the evidence shows it only read the .csproj file."</i> True of the
/// evidence, false of the run. The retry re-read all five files inside step 2 and passed, having
/// spent the step's only retry on an artefact.</para>
///
/// <para>Above degree one a step gets its own fork of the conversation, seeded with a digest rather
/// than a transcript — so its window is its own calls, and it was never affected. The fix makes the
/// journal follow the conversation instead of the step, which is what makes the two cases one rule.
/// </para>
/// </summary>
public sealed class EvidenceWindowTests
{
    private const string TwoStepPlan = """
        {"disposition":"task","title":"sync the README",
         "steps":[{"title":"Analyze the code","dependsOn":[]},
                  {"title":"Update the README","dependsOn":[0]}]}
        """;

    private const string TwoBranchPlan = """
        {"disposition":"task","title":"look at both",
         "steps":[{"title":"Read the alpha note","dependsOn":[]},
                  {"title":"Read the beta note","dependsOn":[]}]}
        """;

    private static ExecutionJournal Journal(params (int Step, string Tool)[] calls)
        => Journal(spansSteps: false, calls);

    private static ExecutionJournal Journal(bool spansSteps, params (int Step, string Tool)[] calls)
    {
        var journal = new ExecutionJournal(spansSteps);
        foreach (var (step, tool) in calls)
            journal.Record(step, tool, """{"path":"x.cs"}""", ActionOutcome.Succeeded, "ok");
        return journal;
    }

    /// <summary>Everything the engine put in front of a provider, flattened for searching.</summary>
    private static string[] Prompts(FakeChatProvider provider)
        => provider.Requests
            .Select(r => string.Join("\n", r.Messages.Select(m => m.Content ?? "")))
            .ToArray();

    // -- the evidence, on its own --------------------------------------------

    /// <summary>
    /// A window that spans steps says so, and every call says which step made it. Without the
    /// labels the reviewer would be shown one undifferentiated list and asked about one step of it.
    /// </summary>
    [Fact]
    public void Evidence_that_spans_two_steps_says_so_and_names_them()
    {
        var evidence = Journal(spansSteps: true,
            (1, "read_file"), (1, "list_dir"), (2, "read_file")).Describe();

        Assert.Contains("3 tool call(s) in this run so far", evidence, StringComparison.Ordinal);
        Assert.Contains("[1] (step 1) -> read_file", evidence, StringComparison.Ordinal);
        Assert.Contains("[3] (step 2) -> read_file", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// And one step's evidence reads exactly as it did before. The step number would be the same on
    /// every line, and the reviewer was told which step it is judging in the sentence above.
    /// </summary>
    [Fact]
    public void Evidence_of_one_step_still_says_in_this_step_and_does_not_repeat_the_number()
    {
        var evidence = Journal((1, "read_file"), (1, "list_dir")).Describe();

        Assert.Contains("2 tool call(s) in this step", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("(step 1)", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected attempt that was discarded from the transcript is discarded from the evidence by
    /// the same act. The two used to be kept in step by two separate lines of code, and the invariant
    /// they maintain was broken elsewhere while both were correct.
    /// </summary>
    [Fact]
    public void A_discarded_attempt_is_gone_from_the_evidence()
    {
        var journal = Journal((1, "read_file"));
        var attempt = journal.Mark();
        journal.Record(1, "write_file", """{"path":"draft.md"}""", ActionOutcome.Succeeded, "written");

        journal.Discard(attempt);
        var evidence = journal.Describe();

        Assert.Contains("1 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("write_file", evidence, StringComparison.Ordinal);
        Assert.Equal(1, journal.CountFrom());
    }

    /// <summary>
    /// The one place the two windows CANNOT be aligned: a resumed run restores the interrupted run's
    /// transcript and starts a fresh journal. So the evidence says it does not cover everything the
    /// agent can see, which is the same obligation a shortened result already carries.
    /// </summary>
    [Fact]
    public void A_resumed_run_says_its_evidence_does_not_cover_what_came_before()
    {
        var journal = Journal((1, "read_file"));
        journal.NotePriorTranscript();

        Assert.Contains("RESUMED", journal.Describe(), StringComparison.Ordinal);
    }

    // -- through the engine --------------------------------------------------

    /// <summary>
    /// The reported run. Step 2 answers from what step 1 read, and the reviewer is shown what step 1
    /// read. Asserted on the PROMPT rather than the verdict, because the verdict here comes from a
    /// scripted fake: the defect was never that the reviewer judged badly, it was that it was shown
    /// less than the agent was.
    /// </summary>
    [Fact]
    public async Task A_step_is_judged_on_the_whole_conversation_it_answered_from()
    {
        using var fx = new EngineFixture();
        fx.Write("alpha.md", "the alpha note\n");

        var worker = new FakeChatProvider(
            Turn.Says(TwoStepPlan),
            Turn.Calls1("read_file", """{"path":"alpha.md"}""", "r1"),
            Turn.Says("I read alpha.md. It says what it says."))
        {
            // Step 2 makes no call of its own: it answers from what step 1 read, which the shared
            // conversation lets it do and which used to read as a fabrication.
            WhenExhausted = Turn.Says("alpha.md is already correct, so I changed nothing.")
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0),
            "check alpha.md against the code");

        var stepTwoReview = Prompts(reviewer).Last();

        // The CALL LINE, not the file name: the reviewer prompt carries the agent's report too, and
        // that names alpha.md whatever the evidence holds. Only the evidence renders "-> read_file".
        // With the window back to the step alone this reads "(no tools were run in this step)" under
        // a report about a file - the shape the reported run was rejected for.
        Assert.Contains("-> read_file", stepTwoReview, StringComparison.Ordinal);
        Assert.Contains("alpha.md", stepTwoReview, StringComparison.Ordinal);
        Assert.Contains("in this run so far", stepTwoReview, StringComparison.Ordinal);
    }

    /// <summary>
    /// And above degree one it does not, because there the step did not see the others either: its
    /// conversation is a fork seeded with a digest, so its window is its own calls.
    ///
    /// <para>Asserted on the header rather than on which file each review saw. Two independent steps
    /// start at once and draw from ONE scripted provider, so which turn lands in which step is a
    /// race — a test that named the files would be pinning the order of a queue, not the window.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_step_of_a_parallel_run_is_judged_on_its_own_calls_only()
    {
        using var fx = new EngineFixture();
        fx.Write("alpha.md", "the alpha note\n");
        fx.Write("beta.md", "the beta note\n");

        var worker = new FakeChatProvider(
            Turn.Says(TwoBranchPlan),
            Turn.Calls1("read_file", """{"path":"alpha.md"}""", "r1"),
            Turn.Says("read alpha"),
            Turn.Calls1("read_file", """{"path":"beta.md"}""", "r2"),
            Turn.Says("read beta"))
        {
            WhenExhausted = Turn.Says("done")
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, maxParallelSteps: 2),
            "read both notes");

        Assert.NotEmpty(Prompts(reviewer));
        Assert.DoesNotContain(Prompts(reviewer),
            p => p.Contains("in this run so far", StringComparison.Ordinal));
    }
}

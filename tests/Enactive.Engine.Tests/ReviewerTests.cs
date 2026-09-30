namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// The reviewer must fail closed on an answer it cannot read. It used to return PASS with the note
/// "review not parseable", so a model that wandered off format approved everything.
/// </summary>
public sealed class ReviewerVerdictTests
{
    private static readonly string[] NoArtifacts = Array.Empty<string>();

    [Fact]
    public async Task A_clean_verdict_is_taken_at_face_value()
    {
        var provider = new FakeChatProvider(Verdicts.Fail("the command never ran"));

        var result = await Review(provider);

        Assert.False(result.Pass);
        Assert.Contains("never ran", result.Notes, StringComparison.Ordinal);
    }

    // One re-ask, with a stricter instruction — a model that wandered off format usually recovers
    // when it is shown exactly what shape is wanted.
    [Fact]
    public async Task An_unparseable_answer_earns_one_stricter_re_ask()
    {
        var provider = new FakeChatProvider(
            Turn.Says("Well, it looks broadly fine to me, though I have some reservations."),
            Verdicts.Pass("on second look the evidence is there"));

        var result = await Review(provider);

        Assert.True(result.Pass);
        Assert.Equal(2, provider.Requests.Count);

        // The second request must actually SAY what was wrong, not just repeat the first.
        var reAsk = provider.Requests[1].Messages.Last();
        Assert.Contains("did not contain a verdict", reAsk.Content ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twice_unparseable_is_a_FAIL_not_a_pass()
    {
        var provider = new FakeChatProvider(
            Turn.Says("Looks fine."),
            Turn.Says("I said it looks fine."));

        var result = await Review(provider);

        Assert.False(result.Pass);
        Assert.Contains("did not return a verdict", result.Notes, StringComparison.Ordinal);
        Assert.Equal(2, provider.Requests.Count);
    }

    // "unsure" is not one of the two answers, and defaulting anything-but-fail to pass is the bug.
    [Theory]
    [InlineData("""{"verdict":"unsure","notes":"hard to say"}""")]
    [InlineData("""{"verdict":"","notes":"x"}""")]
    [InlineData("""{"notes":"I forgot the verdict"}""")]
    public async Task A_verdict_that_says_neither_pass_nor_fail_is_not_a_pass(string answer)
    {
        var provider = new FakeChatProvider(Turn.Says(answer), Turn.Says(answer));

        var result = await Review(provider);

        Assert.False(result.Pass);
    }

    [Fact]
    public async Task A_verdict_wrapped_in_prose_or_think_tags_still_parses()
    {
        var provider = new FakeChatProvider(Turn.Says(
            "<think>weighing it up</think> Here is my verdict:\n"
            + """{"verdict":"pass","notes":"fine"}"""
            + "\nHope that helps."));

        var result = await Review(provider);

        Assert.True(result.Pass);
        Assert.Single(provider.Requests);
    }

    private static Task<ReviewResult> Review(FakeChatProvider provider)
        => new Reviewer().ReviewAsync(
            "a step", "I did the thing", "-> run_command …\n<- exit code 0",
            NoArtifacts, provider, "reviewer-model", CancellationToken.None);
}

/// <summary>
/// The second review mode (2026-09-06). A step that only writes text has no exit code to judge, so
/// execution review passes anything it produces — which is how a cluster guide full of invented
/// package names, made-up pcs syntax and stray CJK characters inside an identifier finished green.
/// </summary>
public sealed class ContentReviewTests
{
    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    [Fact]
    public async Task A_step_that_only_writes_is_reviewed_on_its_content()
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a guide"}"""),
            Turn.Calls1("write_file", """{"path":"guide.md","content":"Install libmkfailover-dev."}"""),
            Turn.Says("Wrote the guide."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "write a guide");

        // The reviewer was shown the actual text, not a 120-character preview of the arguments.
        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";
        Assert.Contains("Install libmkfailover-dev.", prompt, StringComparison.Ordinal);
        Assert.Contains("guide.md", prompt, StringComparison.Ordinal);

        // ...and the run says which review it was.
        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("Content review", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_that_ran_a_command_is_still_reviewed_on_execution()
    {
        using var fx = new EngineFixture { ShortReview = false };
        var command = OperatingSystem.IsWindows() ? "echo hello" : "echo hello";

        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"run something"}"""),
            Turn.Calls1("run_command", $$"""{"command":"{{command}}"}"""),
            Turn.Says("Ran it."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "run something");

        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("Execution review", StringComparison.Ordinal));
    }

    // The point of the whole thing: a rejected document stops the run.
    [Fact]
    public async Task Invented_content_fails_the_run()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a guide"}"""),
            Turn.Calls1("write_file", """{"path":"guide.md","content":"sudo apt install libmkfailover-dev"}"""),
            Turn.Says("Wrote the guide."));

        var reviewer = new FakeChatProvider
        {
            WhenExhausted = Verdicts.Fail("libmkfailover-dev is not a real package")
        };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.ReviewFailed
                                     && e.Summary.Contains("libmkfailover-dev", StringComparison.Ordinal));
    }

    // ── a step that composed nothing ─────────────────────────────────────────────────

    /// <summary>
    /// Copying a file is not writing one, and the copy is not this step's prose to be judged.
    ///
    /// <para>The mode turned on "did anything land in the store", and a copy lands. So a step that
    /// moved bytes from one name to another was asked whether its CONTENT was true - and the
    /// reviewer dutifully answered, about a document somebody else had written, possibly months
    /// ago. Reported from a real run: "PASS (Content review): The excerpt contains no factually
    /// incorrect assertions. The technologies, commands, flags and paths all appear accurate."
    /// Nothing about that judgement is about the copy.</para>
    ///
    /// <para>It is not merely a wasted model call. The reviewer returns PASS or FAIL, a FAIL reverts
    /// the step, and a verdict on the wrong question can fail a copy that was performed perfectly
    /// because the file it duplicated says something the reviewer disagrees with.</para>
    /// </summary>
    [Fact]
    public async Task A_step_that_only_copied_a_file_is_reviewed_on_execution()
    {
        using var fx = new EngineFixture { ShortReview = false };
        fx.Write("notes.md", "Install libmkfailover-dev.\n");

        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"copy the notes"}"""),
            Turn.Calls1("copy_file", """{"from":"notes.md","to":"notes-backup.md"}"""),
            Turn.Says("Copied it."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(
            worker,
            // The harness's default role does not carry the file tools this is about.
            worker: EngineFixture.WorkerWith("read_file", "write_file", "copy_file"),
            router: Routers.WithReviewer(),
            reviewProvider: reviewer);

        var events = await fx.RunAsync(orchestrator, "copy the notes");

        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("Execution review", StringComparison.Ordinal));
    }

    /// <summary>Same for a rename, and for a deletion - neither composes anything either.</summary>
    [Theory]
    [InlineData("move_file", """{"from":"notes.md","to":"renamed.md"}""")]
    [InlineData("delete_file", """{"path":"notes.md"}""")]
    public async Task A_step_that_only_relocated_a_file_is_reviewed_on_execution(
        string tool, string arguments)
    {
        using var fx = new EngineFixture { ShortReview = false };
        fx.Write("notes.md", "Install libmkfailover-dev.\n");

        var worker = new FakeChatProvider(
            Turn.Says($$"""{"disposition":"quick_action","title":"{{tool}}"}"""),
            Turn.Calls1(tool, arguments),
            Turn.Says("Done."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(
            worker,
            worker: EngineFixture.WorkerWith("read_file", "write_file", "move_file", "delete_file"),
            router: Routers.WithReviewer(),
            reviewProvider: reviewer,
            // delete_file asks at every tier, and the fixture's default answer is not "allow" - so
            // without this the step is refused and there is no review to have an opinion about.
            decisions: new ScriptedDecisionHandler("allow"));

        var events = await fx.RunAsync(orchestrator, "relocate it");

        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("Execution review", StringComparison.Ordinal));
    }

    /// <summary>
    /// But a step that wrote something AND moved something is still judged on what it wrote. The
    /// rule is about steps that composed nothing, not about the presence of a file operation - and
    /// getting that backwards would take content review away from most real work, since a step that
    /// writes a file and then puts it where it belongs is an ordinary shape.
    /// </summary>
    [Fact]
    public async Task A_step_that_wrote_and_then_moved_is_still_reviewed_on_its_content()
    {
        using var fx = new EngineFixture { ShortReview = false };

        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write then file it"}"""),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"Install libmkfailover-dev."}""", "c1"),
            Turn.Calls1("move_file", """{"from":"draft.md","to":"guide.md"}""", "c2"),
            Turn.Says("Wrote and filed it."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(
            worker,
            worker: EngineFixture.WorkerWith("read_file", "write_file", "move_file"),
            router: Routers.WithReviewer(),
            reviewProvider: reviewer);

        var events = await fx.RunAsync(orchestrator, "write then file it");

        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("Content review", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Content_review_can_be_switched_off()
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a guide"}"""),
            Turn.Calls1("write_file", """{"path":"guide.md","content":"anything at all"}"""),
            Turn.Says("Wrote it."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(), reviewProvider: reviewer, reviewContent: false);
        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("Execution review", StringComparison.Ordinal));
    }

    // The last write to a path is what the user ends up with; an earlier draft is not the deliverable.
    [Fact]
    public async Task Only_the_final_version_of_a_file_is_reviewed()
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"draft then fix"}"""),
            Turn.Calls1("write_file", """{"path":"g.md","content":"FIRST DRAFT"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"g.md","content":"CORRECTED VERSION"}""", "c2"),
            Turn.Says("Fixed it."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        await fx.RunAsync(orchestrator, "draft then fix");

        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";
        Assert.Contains("CORRECTED VERSION", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST DRAFT", prompt, StringComparison.Ordinal);
    }

    // ── an excerpt has to announce itself ─────────────────────────────────

    /// <summary>
    /// 2026-09-07, third run of the menu-button task. The edit was perfect - a one-line diff adding
    /// exactly the requested link - and the step failed anyway.
    ///
    /// <para>Content review was handed the first 8000 characters of a 21400-character page: 161
    /// lines of 414, ending mid-&lt;article&gt;, with nothing saying it was an excerpt. The reviewer
    /// is instructed to fail on "a truncated line", so it failed - and, asked to name the offending
    /// lines, produced a specific and entirely invented one: a &lt;div&gt; supposedly closed with
    /// "&lt;/div" at line 43. The worker then tried three times to fix a defect that did not exist
    /// and the stall detector stopped it. TaskFailed, over a file that was correct.</para>
    ///
    /// <para>The notice existed. It could not fire: <c>ReadWrittenAsync</c> had already cut the
    /// content to 8000 characters, so the prompt's own check compared 8000 against 8000. Two caps of
    /// the same size in two places, and the honesty of the second was defeated by the silence of the
    /// first.</para>
    /// </summary>
    [Fact]
    public void An_excerpt_says_so_even_when_the_caller_did_the_cutting()
    {
        var whole = new string('x', 21_400);
        // Exactly the shape ReadWrittenAsync produces: content already trimmed, real size carried.
        var file = new WrittenFile("web-site/index.html", whole[..8_000], whole.Length);

        var prompt = Reviewer.BuildContentUserPrompt("Add the link", "done", new[] { file });

        Assert.Contains("END OF EXCERPT", prompt);
        Assert.Contains("8000", prompt);
        Assert.Contains("21400", prompt);
    }

    /// <summary>A file shown whole must not be announced as an excerpt - that would teach the
    /// reviewer to discount the ending of every document it is given.</summary>
    [Fact]
    public void A_file_shown_whole_is_not_announced_as_an_excerpt()
    {
        var prompt = Reviewer.BuildContentUserPrompt("Write notes", "done", new[]
        {
            new WrittenFile("notes.md", "all of it, and not one character more")
        });

        Assert.DoesNotContain("EXCERPT", prompt);
    }

    /// <summary>
    /// The convenience constructor is what makes the whole-file case honest by default: a
    /// WrittenFile built from content alone is, by definition, all of that content.
    /// </summary>
    [Fact]
    public void A_written_file_knows_whether_it_is_an_excerpt()
    {
        Assert.False(new WrittenFile("a.txt", "abc").IsExcerpt);
        Assert.True(new WrittenFile("a.txt", "abc", 99).IsExcerpt);

        // The removed-file placeholder is not an excerpt of anything.
        Assert.False(new WrittenFile("gone.txt", "(this file was removed, or could not be read back)").IsExcerpt);
    }

    /// <summary>
    /// The instruction that primed the confabulation. The reviewer is told to fail on a truncated
    /// line - correct in general, and exactly wrong when we are the ones doing the truncating - and
    /// it is told to name offending lines, which is what turned "this looks cut off" into an invented
    /// line 43.
    /// </summary>
    [Fact]
    public void The_reviewer_is_told_not_to_judge_an_excerpt_by_how_it_ends()
    {
        var instructions = Reviewer.ContentSystemPrompt;

        Assert.Contains("excerpt", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Only name something you can actually see", instructions);
    }
}

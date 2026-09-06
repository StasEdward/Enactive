namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A rejected step must leave the workspace as it found it. Before this, the gate stopped the
/// REPORT — the run said Failed — while the rejected document stayed on disk, which is the version
/// a person would open next. Seen for real on 2026-09-06: a guide full of invented pcs syntax sat
/// in the workspace under a red status.
/// </summary>
public sealed class RevertOnRejectTests
{
    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    private static FakeChatProvider RejectingReviewer()
        => new() { WhenExhausted = Verdicts.Fail("invented command syntax") };

    private static FakeChatProvider WriterThatKeepsWriting(string path, string content)
        => new(
            Turn.Says("""{"disposition":"quick_action","title":"write a guide"}"""),
            Turn.Calls1("write_file", $$"""{"path":"{{path}}","content":"{{content}}"}""", "c1"),
            Turn.Says("Wrote it."),
            Turn.Calls1("write_file", $$"""{"path":"{{path}}","content":"{{content}} (second try)"}""", "c2"),
            Turn.Says("Rewrote it."))
        {
            WhenExhausted = Turn.Says("done")
        };

    [Fact]
    public async Task A_file_the_rejected_step_created_is_removed()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "invented content"),
            router: Routers.WithReviewer(),
            reviewProvider: RejectingReviewer());

        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.False(fx.Exists("guide.md"), "the rejected file should not have been left behind");
        Assert.Contains(events, e => e.Kind == EventKind.ArtifactReverted);
    }

    [Fact]
    public async Task A_file_the_rejected_step_overwrote_is_restored()
    {
        using var fx = new EngineFixture();
        fx.Write("guide.md", "the version I wrote by hand");

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "the agent's invention"),
            router: Routers.WithReviewer(),
            reviewProvider: RejectingReviewer());

        var events = await fx.RunAsync(orchestrator, "improve the guide");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.Equal("the version I wrote by hand", fx.Read("guide.md"));
    }

    // Two writes in one rejected step must both come back, to the state before the STEP — not to
    // the state after its own first attempt.
    [Fact]
    public async Task Several_writes_to_one_path_revert_to_the_state_before_the_step()
    {
        using var fx = new EngineFixture();
        fx.Write("guide.md", "original");

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "draft"),
            router: Routers.WithReviewer(),
            reviewProvider: RejectingReviewer());

        await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal("original", fx.Read("guide.md"));
    }

    // A content retry drops the rejected attempt from the transcript, so the paths to put back
    // cannot be read off the transcript at the end: a file written ONLY by the first attempt would
    // be invisible to the revert and left behind. They are accumulated per attempt instead.
    [Fact]
    public async Task A_file_written_only_by_the_discarded_first_attempt_is_still_put_back()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write two files"}"""),
            Turn.Calls1("write_file", """{"path":"only-in-attempt-one.md","content":"x"}""", "c1"),
            Turn.Says("Wrote it."),
            // The second attempt writes a DIFFERENT file and forgets the first.
            Turn.Calls1("write_file", """{"path":"attempt-two.md","content":"y"}""", "c2"),
            Turn.Says("Wrote the other one."))
        {
            WhenExhausted = Turn.Says("done")
        };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(), reviewProvider: RejectingReviewer());

        await fx.RunAsync(orchestrator, "write two files");

        Assert.False(fx.Exists("only-in-attempt-one.md"), "the first attempt's file was left behind");
        Assert.False(fx.Exists("attempt-two.md"));
    }

    // The one case where undoing would itself destroy something.
    [Fact]
    public async Task A_file_edited_after_the_step_wrote_it_is_left_alone()
    {
        using var fx = new EngineFixture();

        // The reviewer edits the file while it is "reviewing" — standing in for the user doing it.
        var reviewer = new EditingReviewer(fx, "guide.md", "I edited this myself");

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "the agent's version"),
            router: Routers.WithReviewer(),
            reviewProvider: reviewer);

        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal("I edited this myself", fx.Read("guide.md"));
        Assert.Contains(events, e =>
            e.Kind == EventKind.ArtifactReverted
            && e.Summary.Contains("Left as it is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reverting_can_be_switched_off()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "invented content"),
            router: Routers.WithReviewer(),
            reviewProvider: RejectingReviewer(),
            revertRejectedSteps: false);

        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.True(fx.Exists("guide.md"), "with reverting off the rejected file stays for inspection");
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
    }

    [Fact]
    public async Task A_step_that_PASSES_keeps_its_work()
    {
        using var fx = new EngineFixture();
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "perfectly good content"),
            router: Routers.WithReviewer(),
            reviewProvider: reviewer);

        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.True(fx.Exists("guide.md"));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
    }

    // With staging, "putting it back" is simply dropping the proposals — nothing reached disk.
    [Fact]
    public async Task Staged_proposals_from_a_rejected_step_are_dropped()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        var orchestrator = fx.Build(
            WriterThatKeepsWriting("guide.md", "invented content"),
            artifacts: staging,
            router: Routers.WithReviewer(),
            reviewProvider: RejectingReviewer());

        await fx.RunAsync(orchestrator, "write a guide");

        Assert.Empty(staging.PendingPaths);
        Assert.All(staging.Changes, c => Assert.True(c.Rejected));
    }

    /// <summary>A reviewer that edits the file behind the engine's back, then rejects the step.</summary>
    private sealed class EditingReviewer : Enactive.Core.Providers.IChatProvider
    {
        private readonly EngineFixture _fx;
        private readonly string _path;
        private readonly string _content;

        public EditingReviewer(EngineFixture fx, string path, string content)
        {
            _fx = fx;
            _path = path;
            _content = content;
        }

        public async IAsyncEnumerable<Enactive.Core.Chat.ChatStreamEvent> StreamChatAsync(
            Enactive.Core.Chat.ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Enactive.Core.Chat.ChatCompletion> CompleteAsync(
            Enactive.Core.Chat.ChatRequest request, CancellationToken ct)
        {
            _fx.Write(_path, _content);

            return Task.FromResult(new Enactive.Core.Chat.ChatCompletion(
                new Enactive.Core.Chat.ChatMessage(
                    Enactive.Core.Chat.ChatRole.Assistant,
                    """{"verdict":"fail","notes":"invented syntax"}""",
                    null),
                "stop", null, null));
        }
    }
}

/// <summary>
/// How many times a rejected step may be redone, and what the retry is given to work from.
/// </summary>
public sealed class ReviewRetryTests
{
    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    [Fact]
    public async Task Zero_retries_means_one_attempt()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""))
        {
            WhenExhausted = Turn.Says("wrote it")
        };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(),
            reviewProvider: new FakeChatProvider { WhenExhausted = Verdicts.Fail("no") },
            reviewRetries: 0);

        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Single(events.OfKind(EventKind.ReviewFailed));
        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
    }

    [Fact]
    public async Task Three_retries_means_four_attempts()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""))
        {
            WhenExhausted = Turn.Says("wrote it")
        };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(),
            reviewProvider: new FakeChatProvider { WhenExhausted = Verdicts.Fail("still no") },
            reviewRetries: 3);

        var events = await fx.RunAsync(orchestrator, "write a guide");

        Assert.Equal(4, events.OfKind(EventKind.ReviewFailed).Count());
    }

    // The rejected draft is dropped from the transcript before the retry. Keeping it cost tokens
    // twice over — with num_ctx at 8192 a real run reached 6.7k on the second retry — and anchored
    // the model on the version it had just been told was wrong.
    [Fact]
    public async Task A_content_retry_does_not_carry_the_rejected_draft_forward()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""),
            Turn.Calls1("write_file", """{"path":"g.md","content":"THE REJECTED DRAFT"}""", "c1"),
            Turn.Says("Wrote it."),
            Turn.Calls1("write_file", """{"path":"g.md","content":"a second attempt"}""", "c2"),
            Turn.Says("Rewrote it."))
        {
            WhenExhausted = Turn.Says("done")
        };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(),
            reviewProvider: new FakeChatProvider { WhenExhausted = Verdicts.Fail("wrong") });

        await fx.RunAsync(orchestrator, "write a guide");

        // The request that started the second attempt must not still contain the first draft.
        var retryRequest = worker.Requests[2];
        Assert.DoesNotContain(
            retryRequest.Messages,
            m => m.Content?.Contains("THE REJECTED DRAFT", StringComparison.Ordinal) == true
                 || m.ToolCalls?.Any(c => c.ArgumentsJson.Contains("THE REJECTED DRAFT", StringComparison.Ordinal)) == true);

        // ...but the feedback that explains why must be there.
        Assert.Contains(
            retryRequest.Messages,
            m => m.Content?.Contains("wrong", StringComparison.Ordinal) == true);
    }

    // An execution step is the opposite case: its command output IS the evidence, and throwing the
    // transcript away would mean re-running commands that have already had their effect.
    [Fact]
    public async Task An_execution_retry_keeps_the_transcript()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"run it"}"""),
            Turn.Calls1("run_command", """{"command":"echo MARKER_ONE"}""", "c1"),
            Turn.Says("Ran it."))
        {
            WhenExhausted = Turn.Says("ran it again")
        };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(),
            reviewProvider: new FakeChatProvider { WhenExhausted = Verdicts.Fail("not convincing") });

        await fx.RunAsync(orchestrator, "run the command");

        var retryRequest = worker.Requests[2];
        Assert.Contains(
            retryRequest.Messages,
            m => m.Content?.Contains("MARKER_ONE", StringComparison.Ordinal) == true);
    }
}

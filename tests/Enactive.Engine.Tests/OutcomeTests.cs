namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// A run must not report success it did not have (review findings #8, #9, #17). Each test drives the
/// real engine and asserts on the TERMINAL EVENT, which is what the UI, the history and the Inbox
/// all read — the bug was that they read "Completed" for runs that had failed.
/// </summary>
public sealed class OutcomeTests
{
    private const string TwoStepPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"prepare","dependsOn":[]},{"title":"apply","dependsOn":[0]}]}
        """;

    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    // Bench scenario: "provider threw in step 1 -> failed + skipped -> TaskCompleted".
    [Fact]
    public async Task A_step_that_throws_fails_the_run_and_skips_its_dependents()
    {
        using var fx = new EngineFixture();

        // The planner answers (CompleteAsync), then the execution stream throws.
        var providers = new MapProviderFactory(
            new ThrowingChatProvider("the model endpoint is down"),
            ("fake", new PlanThenThrowProvider(TwoStepPlan, "the model endpoint is down")));

        var orchestrator = fx.Build(providers);
        var events = await fx.RunAsync(orchestrator, "prepare then apply");

        var terminal = Terminal(events);
        Assert.Equal(EventKind.TaskFailed, terminal.Kind);
        Assert.Equal(RunOutcomeKind.Failed, terminal.Outcome());
        Assert.DoesNotContain(events, e => e.Kind == EventKind.TaskCompleted);
        Assert.Contains(events, e => e.Summary.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    // Bench scenario: "reply stopped by length -> ErrorObserved -> TaskCompleted".
    [Fact]
    public async Task Output_cut_off_at_the_token_limit_is_not_a_completed_run()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a long file"}"""),
            new Turn("half of an ans", null, FinishReason: "length"))
        { WhenExhausted = new Turn("still cut", FinishReason: "length") };

        var orchestrator = fx.Build(provider);
        var events = await fx.RunAsync(orchestrator, "write something long");

        var terminal = Terminal(events);
        Assert.Equal(EventKind.TaskFailed, terminal.Kind);
        Assert.Equal(RunOutcomeKind.Incomplete, terminal.Outcome());
        Assert.Contains("token limit", terminal.OutcomeReason() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_clean_run_still_completes()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a file"}"""),
            Turn.Calls1("write_file", """{"path":"ok.txt","content":"hello"}"""),
            Turn.Says("Wrote ok.txt."));

        var orchestrator = fx.Build(provider);
        var events = await fx.RunAsync(orchestrator, "write ok.txt");

        var terminal = Terminal(events);
        Assert.Equal(EventKind.TaskCompleted, terminal.Kind);
        Assert.Equal(RunOutcomeKind.Completed, terminal.Outcome());
        Assert.True(fx.Exists("ok.txt"));
    }

    // Bench scenario: "reviewer always FAIL -> dependent step ran, TaskCompleted overall".
    // A review that does not block is not a gate.
    [Fact]
    public async Task A_rejected_step_blocks_its_dependents_and_fails_the_run()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") };
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Fail() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "prepare then apply");

        var terminal = Terminal(events);
        Assert.Equal(EventKind.TaskFailed, terminal.Kind);
        Assert.Equal(RunOutcomeKind.Failed, terminal.Outcome());

        // The dependent step must never have started.
        Assert.DoesNotContain(events, e =>
            e.Kind == EventKind.StepStarted && e.Summary.Contains("apply", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, e => e.Kind == EventKind.ReviewFailed);
    }

    // The UI decides a step card's colour from this value. It used to search the summary text for
    // "FAILED:" and "skipped (dependency failed)", so rewording a message turned a red card green.
    [Fact]
    public async Task Step_events_carry_their_outcome_as_a_value()
    {
        using var fx = new EngineFixture();
        var providers = new MapProviderFactory(
            new ThrowingChatProvider("down"),
            ("fake", new PlanThenThrowProvider(TwoStepPlan, "down")));

        var orchestrator = fx.Build(providers);
        var events = await fx.RunAsync(orchestrator, "prepare then apply");

        var completed = events.OfKind(EventKind.StepCompleted).ToArray();
        Assert.Equal(2, completed.Length);
        Assert.Contains(completed, e => e.StepOutcome() == StepOutcomeKind.Failed);
        Assert.Contains(completed, e => e.StepOutcome() == StepOutcomeKind.Skipped);
    }

    [Fact]
    public async Task A_successful_step_says_so_in_its_payload()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var orchestrator = fx.Build(worker);
        var events = await fx.RunAsync(orchestrator, "prepare then apply");

        Assert.All(
            events.OfKind(EventKind.StepCompleted),
            e => Assert.Equal(StepOutcomeKind.Succeeded, e.StepOutcome()));
    }

    [Fact]
    public async Task A_passing_reviewer_lets_the_plan_finish()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") };
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "prepare then apply");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.Contains(events, e =>
            e.Kind == EventKind.StepStarted && e.Summary.Contains("apply", StringComparison.OrdinalIgnoreCase));
    }

    // Review finding #17 — the reviewer was configured, shown in routing, and then skipped entirely
    // for QuickActions, which the planner is explicitly told to prefer.
    [Fact]
    public async Task A_quick_action_goes_through_the_reviewer()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"write a file"}"""))
        {
            WhenExhausted = Turn.Says("wrote it")
        };
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Fail() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        var events = await fx.RunAsync(orchestrator, "write a file");

        Assert.Contains(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
    }

    // Not in the review: an unreachable reviewer used to be scored as PASS, so a stopped Ollama
    // turned every step green while the gate looked configured.
    [Fact]
    public async Task A_reviewer_that_throws_does_not_pass_the_step()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"write a file"}"""))
        {
            WhenExhausted = Turn.Says("wrote it")
        };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(),
            reviewProvider: new ThrowingChatProvider("connection refused"));

        var events = await fx.RunAsync(orchestrator, "write a file");

        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("review error"));
        Assert.Equal(RunOutcomeKind.Incomplete, Terminal(events).Outcome());
    }
}

/// <summary>Plans once, then throws on every execution turn.</summary>
public sealed class PlanThenThrowProvider : Enactive.Core.Providers.IChatProvider
{
    private readonly string _plan;
    private readonly string _error;

    public PlanThenThrowProvider(string plan, string error)
    {
        _plan = plan;
        _error = error;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException(_error);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        => Task.FromResult(new ChatCompletion(
            new ChatMessage(ChatRole.Assistant, _plan, null), "stop", null, null));
}

/// <summary>
/// Two tool calls in one Anthropic completion (review finding #7). They were all emitted as index 0
/// and the orchestrator merges by index, so they collapsed into one call with the LAST name and the
/// FIRST arguments — and the other action disappeared.
/// </summary>
public sealed class ToolCallMergeTests
{
    [Fact]
    public async Task Two_calls_in_one_turn_keep_their_own_names_and_arguments()
    {
        using var fx = new EngineFixture();
        fx.Write("source.txt", "the original content");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read then write"}"""),
            new Turn(null, new[]
            {
                new ToolCall("call_read", "read_file", """{"path":"source.txt"}"""),
                new ToolCall("call_write", "write_file", """{"path":"copy.txt","content":"the copy"}""")
            }),
            Turn.Says("Read the source and wrote the copy."));

        var orchestrator = fx.Build(provider);
        var events = await fx.RunAsync(orchestrator, "copy source.txt to copy.txt");

        // Both actions happened, and the write kept its own arguments instead of inheriting the read's.
        Assert.True(fx.Exists("copy.txt"), events.Text());
        Assert.Equal("the copy", fx.Read("copy.txt"));
        Assert.Equal("the original content", fx.Read("source.txt"));
    }

    // The same merge, seen from the provider side: the deltas must carry distinct indexes. This is
    // the shape AnthropicProvider now emits; it used to send index 0 for every call.
    [Fact]
    public async Task Streamed_deltas_carry_one_index_per_call()
    {
        var calls = new[]
        {
            new ToolCall("a", "read_file", """{"path":"x"}"""),
            new ToolCall("b", "write_file", """{"path":"y","content":"z"}""")
        };

        var provider = new FakeChatProvider(new Turn(null, calls));
        var indexes = new List<int>();

        await foreach (var ev in provider.StreamChatAsync(
            new ChatRequest("m", Array.Empty<ChatMessage>()), CancellationToken.None))
        {
            if (ev is ToolCallDelta delta)
                indexes.Add(delta.Index);
        }

        Assert.Equal(new[] { 0, 1 }, indexes);
    }

    // Half of what made the merge destructive: the write inherited arguments with no "content", and
    // that used to be silently treated as an empty file.
    [Fact]
    public async Task Write_file_without_content_is_an_error_not_an_empty_file()
    {
        using var fx = new EngineFixture();
        fx.Write("important.txt", "do not lose this");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""),
            Turn.Calls1("write_file", """{"path":"important.txt"}"""),
            Turn.Says("I could not write it."));

        var orchestrator = fx.Build(provider);
        await fx.RunAsync(orchestrator, "write the file");

        Assert.Equal("do not lose this", fx.Read("important.txt"));
    }

    [Fact]
    public async Task An_explicitly_empty_content_still_writes_an_empty_file()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"empty it"}"""),
            Turn.Calls1("write_file", """{"path":"blank.txt","content":""}"""),
            Turn.Says("Emptied it."));

        var orchestrator = fx.Build(provider);
        await fx.RunAsync(orchestrator, "create an empty file");

        Assert.True(fx.Exists("blank.txt"));
        Assert.Equal("", fx.Read("blank.txt"));
    }
}

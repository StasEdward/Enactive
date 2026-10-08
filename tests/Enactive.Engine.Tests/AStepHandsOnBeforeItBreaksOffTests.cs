namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run 21df3d79, 2026-09-28: a step that was to hand its findings on made 146 calls, was started again
/// three times in five minutes, trimmed its way round the same claim over and over, and never called
/// submit_step_output - nine minutes of checked claims ended as "result not provided". A step with a
/// declared output is asked to hand on what it has at every fresh start, and, with no fresh start left
/// and the window full again, once to hand on its result now.
/// </summary>
public sealed class AStepHandsOnBeforeItBreaksOffTests
{
    private const string OneStep = """
        {"disposition":"task","title":"check the claims",
         "steps":[{"title":"check claims","dependsOn":[],
                   "output":{"findings":{"type":"text","description":"what is right and wrong"}}}]}
        """;

    private const string FirstHandOn = "First, hand on what you have established so far";
    private const string AgainHandOn = "call " + StepOutputContract.ToolName + " again with the whole result";
    private const string HandOnNow = "Hand on your result NOW";

    private static Turn Read(EngineFixture fx, int i, int prompt)
    {
        fx.Write($"claim{i}.md", $"claim {i}");
        return Turn.Calls1("read_file", $$"""{"path":"claim{{i}}.md"}""", $"r{i}").Reporting(prompt: prompt);
    }

    private static int Asked(FakeChatProvider provider, string text)
        => provider.Requests.Count(r => r.Messages.LastOrDefault()?.Content?.Contains(text, StringComparison.Ordinal) == true);

    [Fact]
    public async Task A_step_is_asked_to_hand_on_at_each_fresh_start_and_once_more_when_none_is_left()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        var provider = new FakeChatProvider(
            Turn.Says(OneStep),
            Read(fx, 1, 3_000), Read(fx, 2, 9_000),
            Turn.Says("# Note to self - claims 1 and 2 checked."),
            // fresh start 1: nothing handed on yet
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"claims 1-2 right; 3+ not checked"}""", "s1"),
            Read(fx, 3, 9_000),
            Turn.Says("# Note to self - claims 1-3 checked, handed on 1-2."),
            // fresh start 2: handed on already
            Read(fx, 4, 9_000),
            Turn.Says("# Note to self - claims 1-4 checked."),
            // fresh start 3, the last
            Read(fx, 5, 9_000),
            // no fresh start left, window full: asked to hand on now
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"claims 1-5 right; 6 not checked"}""", "s2"),
            Turn.Says("Handed on.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.Equal(3, events.Count(e => e.Kind == EventKind.ContextTrimmed
                                          && e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal)));
        Assert.Equal(1, Asked(provider, FirstHandOn));
        Assert.Equal(2, Asked(provider, AgainHandOn));
        Assert.Equal(1, Asked(provider, HandOnNow));
        Assert.Single(events, e => e.Summary.Contains("asked to hand on its result now", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// Run ddca5350, 18:18: asked for its note, the step wrote 4,211 characters of checked claims AND handed
    /// the same findings on with submit_step_output. Both were thrown away - the note for carrying a call,
    /// the call for being in a note. The note is kept, and the hand-over is checked and kept like any other.
    /// </summary>
    [Fact]
    public async Task A_result_handed_on_with_the_note_keeps_both()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        var provider = new FakeChatProvider(
            Turn.Says(OneStep),
            Read(fx, 1, 3_000), Read(fx, 2, 9_000),
            new Turn("# Note to self - claims 1 and 2 checked, both right.",
                [new ToolCall("h1", StepOutputContract.ToolName, """{"findings":"claims 1-2 right"}""")], "tool_calls"),
            Turn.Says("Handed on; done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.Contains(events, e => e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Summary.Contains("Handover note not written", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith(
            $"{StepOutputContract.ToolName} -> ok: Accepted as this step's output (revision 1); the steps after it receive these values.", StringComparison.Ordinal)
            && e.Summary.EndsWith("Handed on with the handover note.", StringComparison.Ordinal));
        Assert.Equal(1, Asked(provider, AgainHandOn));                           // the fresh start knows it was handed on
        Assert.Contains(events, e => e.Kind == EventKind.StepOutputRecorded && e.Summary.Contains("claims 1-2 right", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>Any other call in a note is still an intention, not a record: no note.</summary>
    [Fact]
    public async Task A_note_that_calls_another_tool_is_still_no_note()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        var provider = new FakeChatProvider(
            Turn.Says(OneStep),
            Read(fx, 1, 3_000), Read(fx, 2, 9_000),
            new Turn("# Note - next I read claim 3.", [new ToolCall("r9", "read_file", """{"path":"claim1.md"}""")], "tool_calls"),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"claims 1-2 right"}""", "s1"),
            Turn.Says("Done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.Contains(events, e => e.Summary.Contains("Handover note not written (attempt 1 of 2): the model called read_file instead of writing it", StringComparison.Ordinal));
    }

    /// <summary>
    /// Run 16d57849: asked in text at three fresh starts and once more, the step read on each time. The turn after
    /// a fresh start, while nothing has been handed on, requires a call and runs only the hand-over; then the step
    /// carries on as before.
    /// </summary>
    [Fact]
    public async Task The_turn_after_a_fresh_start_offers_only_the_hand_over_until_something_is_handed_on()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        var provider = new FakeChatProvider(
            Turn.Says(OneStep),
            Read(fx, 1, 3_000), Read(fx, 2, 9_000),
            Turn.Says("# Note to self - claims 1 and 2 checked."),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"claims 1-2 right; 3 not checked"}""", "s1"),
            Read(fx, 3, 3_000),
            Turn.Says("Done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        var forced = provider.Requests.Single(r => r.RequireToolCall);
        // The list itself is not cut to one tool (run fba4d6: a list that changes for one turn is the conversation
        // read again, in and out): a call is required, and anything but the hand-over is answered, not run.
        Assert.Contains(forced.Tools!, t => t.Name == StepOutputContract.ToolName);
        Assert.Equal(forced.Tools!.Select(t => t.Name), provider.Requests[provider.Requests.IndexOf(forced) - 1].Tools!.Select(t => t.Name));
        var after = provider.Requests[provider.Requests.IndexOf(forced) + 1];
        Assert.False(after.RequireToolCall);
        Assert.Contains(after.Tools!, t => t.Name == "read_file");
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task Text_or_another_call_on_that_turn_does_not_end_the_step_or_run()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        var provider = new FakeChatProvider(
            Turn.Says(OneStep),
            Read(fx, 1, 3_000), Read(fx, 2, 9_000),
            Turn.Says("# Note to self - claims 1 and 2 checked."),
            Turn.Calls1("read_file", """{"path":"claim1.md"}""", "x1"),                  // not the hand-over: not run
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"claims 1-2 right"}""", "s1"),
            Turn.Says("Done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.True(events.Any(e => e.Kind == EventKind.ToolResult
                                     && e.Summary.StartsWith("read_file -> not run: this turn is for submit_step_output only", StringComparison.Ordinal)), events.Text());
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());

        using var fx2 = new EngineFixture { StepOutputs = true };
        var provider2 = new FakeChatProvider(
            Turn.Says(OneStep),
            Read(fx2, 1, 3_000), Read(fx2, 2, 9_000),
            Turn.Says("# Note to self - claims 1 and 2 checked."),
            Turn.Says("I will keep checking."),                                              // text: the step carries on
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"claims 1-2 right"}""", "s1"),
            Turn.Says("Done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events2 = await fx2.RunAsync(fx2.Build(provider2, EngineFixture.Role("developer")), "check the claims");

        Assert.Contains(events2, e => e.Summary.Contains("was answered with text; the step carries on", StringComparison.Ordinal));
        Assert.Contains(events2, e => e.Kind == EventKind.StepOutputRecorded);
    }

    [Fact]
    public void An_openai_compatible_provider_is_asked_for_a_call()
    {
        var handler = new CapturingHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var provider = new Enactive.Providers.OpenAiCompatibleProvider(http,
            new Enactive.Core.Providers.ProviderDescriptor("p", "p", Enactive.Core.Providers.ProviderKind.OpenAiCompatible, "http://test.invalid/v1", null, []));
        try
        {
            provider.CompleteAsync(new ChatRequest("m", [ChatMessage.User("hi")],
                [StepOutputContract.Tool(new Enactive.Core.Tasks.StepOutputSchema("s", 1,
                    [new Enactive.Core.Tasks.StepOutputField("x", Enactive.Core.Tasks.StepOutputFieldType.Text, "x", Required: true)]))],
                RequireToolCall: true), default).GetAwaiter().GetResult();
        }
        catch (Exception) { /* the reply is not the point */ }
        Assert.Contains("\"tool_choice\":\"required\"", handler.Body, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler : System.Net.Http.HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(System.Net.HttpStatusCode.BadRequest) { Content = new System.Net.Http.StringContent("{}") };
        }
    }

    [Fact]
    public async Task A_step_with_nothing_to_hand_on_is_not_asked_to()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"a long job"}"""),
            Read(fx, 1, 3_000), Read(fx, 2, 9_000),
            Turn.Says("# Note to self - read 1 and 2."),
            Read(fx, 3, 3_000),
            Turn.Says("Done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        Assert.Contains(events, e => e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Requests, r => r.Messages.Any(m => m.Content?.Contains(StepOutputContract.ToolName, StringComparison.Ordinal) == true
                                                                          && m.Role == ChatRole.User));
    }
}

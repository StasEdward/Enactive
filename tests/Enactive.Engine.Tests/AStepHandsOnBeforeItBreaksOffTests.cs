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
            $"{StepOutputContract.ToolName} -> ok: Accepted as this step's output (revision 1), handed on with the handover note", StringComparison.Ordinal));
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

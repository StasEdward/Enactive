namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
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

namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Xunit;

/// <summary>
/// A step that hands over is restarted with ITS OWN instruction, not the first step's.
///
/// <para><b>Measured 2026-09-24 10:29, run 1942b0.</b> Steps share one conversation by default
/// (<c>MaxParallelSteps = 1</c>), and the handover kept "everything before the first assistant
/// message" — which, once step 1 has replied, includes step 1's instruction. All three handovers in
/// that run restarted their step with the same sentence:</para>
///
/// <code>
/// #1 handover → "Proceed with this step of the plan: Verify wiki pages 1-3 against src"
/// #3 handover → "Proceed with this step of the plan: Verify wiki pages 1-3 against src"
/// #4 handover → "Proceed with this step of the plan: Verify wiki pages 1-3 against src"
/// </code>
///
/// <para>Step 3 was pages 7–9. Its execution review failed on exactly that — <i>"the report says
/// outright that 'this step was pages 1–3'"</i> — and the step had to be done again. Step 4 was
/// pages 10–12; handed step 1's instruction together with a note saying that work was finished, it
/// agreed and closed thirty-one seconds later.</para>
///
/// <para>Both handovers landed on turn 60, after the real work was already on disk, so that run
/// lost only a review cycle. The same bug on a step that hands over early loses the step.</para>
/// </summary>
public sealed class AHandoverKeepsItsOwnStepTests
{
    private const string Marker = "Proceed with this step of the plan: ";
    private const string Alpha = "Alpha checks the early pages";
    private const string Beta = "Beta checks the later pages";

    /// <summary>
    /// Two steps whose titles share no prefix, so the one a conversation belongs to is never in
    /// doubt.
    /// </summary>
    private const string TwoSteps = """
        {"disposition":"task","title":"Two sets",
         "steps":[{"title":"Alpha checks the early pages","dependsOn":[]},
                  {"title":"Beta checks the later pages","dependsOn":[0]}]}
        """;

    /// <summary>Which step a conversation was told it is, from its LAST instruction.</summary>
    private static string? StepOf(ChatRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var content = request.Messages[i].Content;
            if (content is null)
                continue;

            var at = content.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var from = at + Marker.Length;
            var end = content.IndexOf('\n', from);
            return end < 0 ? content[from..] : content[from..end];
        }

        return null;
    }

    /// <summary>
    /// Beta's turns. Sixty reads take it to <c>TurnsBeforeHandover</c>, and the turn after those is
    /// the one the handover spends asking the model to summarise itself — so a note has to sit
    /// exactly there. A tool call in that position produces no note and the step is not cut at all,
    /// which is what the first draft of this test measured instead of what it meant to.
    ///
    /// <para>Distinct files, because reading one over and over is a stall and the stall guard is
    /// right to stop it.</para>
    /// </summary>
    private static Turn[] BetaScript(EngineFixture fx)
    {
        var turns = new List<Turn>();

        for (var i = 0; i < 60; i++)
        {
            fx.Write($"beta{i}.md", $"page {i}");
            turns.Add(Turn.Calls1("read_file", $$"""{"path":"beta{{i}}.md"}""", $"b{i}"));
        }

        turns.Add(Turn.Says("# Note to self — the LATER pages. Read beta0-beta59; the rest is left."));

        for (var i = 60; i < 64; i++)
        {
            fx.Write($"beta{i}.md", $"page {i}");
            turns.Add(Turn.Calls1("read_file", $$"""{"path":"beta{{i}}.md"}""", $"b{i}"));
        }

        turns.Add(Turn.Says("Beta is done."));
        return turns.ToArray();
    }

    private static ByStepChatProvider Scripted(EngineFixture fx)
    {
        var provider = new ByStepChatProvider(TwoSteps);
        provider.Step(Alpha, Turn.Says("Alpha is done."));
        provider.Step(Beta, BetaScript(fx));
        return provider;
    }

    /// <summary>
    /// THE ONE THAT MATTERS. Beta runs past <c>TurnsBeforeHandover</c>, hands over, and the
    /// conversation it comes back to must still be Beta's.
    /// </summary>
    [Fact]
    public async Task A_second_step_that_hands_over_is_not_restarted_as_the_first()
    {
        using var fx = new EngineFixture();
        var provider = Scripted(fx);

        var events = await fx.RunAsync(fx.Build(provider), "check both sets");

        // The handover has to have happened, or this test proves nothing.
        Assert.Contains(events, e => e.Kind == EventKind.ContextTrimmed
                                     && e.Summary.Contains("fresh conversation", StringComparison.Ordinal));

        // Every request made from the moment Beta started names Beta. Under the old behaviour the
        // rebuilt conversation carried Alpha's instruction and nothing else, so the engine, the
        // provider and the model all believed Beta's remaining turns belonged to Alpha.
        var afterBetaStarted = provider.Requests.SkipWhile(r => StepOf(r) != Beta).ToArray();

        Assert.NotEmpty(afterBetaStarted);
        Assert.All(afterBetaStarted, r => Assert.Equal(Beta, StepOf(r)));
    }

    /// <summary>
    /// THE BOUNDARY. What the step restarts from is the RUN's preamble, not an empty list: the
    /// worker instructions and the request have to survive a handover, or the model comes back
    /// knowing neither who it is nor what was asked.
    /// </summary>
    [Fact]
    public async Task What_the_step_restarts_from_still_carries_the_request()
    {
        using var fx = new EngineFixture();
        var provider = Scripted(fx);

        await fx.RunAsync(fx.Build(provider), "check both sets");

        // The first request AFTER the handover - the one whose conversation was just rebuilt.
        var rebuilt = provider.Requests.First(
            r => r.Messages.Any(m => m.Content?.Contains("started again from your own notes",
                                                         StringComparison.Ordinal) == true));

        Assert.Equal(ChatRole.System, rebuilt.Messages[0].Role);
        Assert.Contains(rebuilt.Messages,
                        m => m.Content?.Contains("check both sets", StringComparison.Ordinal) == true);
        Assert.Equal(Beta, StepOf(rebuilt));
    }
}

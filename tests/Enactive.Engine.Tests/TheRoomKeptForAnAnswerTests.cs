namespace Enactive.Engine.Tests;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// How much of a context window is held back for the model's answer, and why it is not 2,048.
///
/// <para><b>Measured 2026-09-24 04:19, run 98325c, step 3 of 5.</b> The reserve was
/// <c>Math.Min(Math.Clamp(window / 8, 256, 2048), window / 2)</c> — and on a 131,072-token window
/// the clamp bound, so 2,048 tokens, 1.5%, were kept for the answer. The guard's budget was
/// therefore 129,024, a prompt of 124,297 tokens was honestly under it, and nothing trimmed:</para>
///
/// <code>
/// 04:14:36.695  StepStarted [3/5] Verify wiki pages 7-9, append findings
/// 04:14:36.697  ContextTrimmed: dropped the contents of 20 earlier tool message(s)
/// 04:19:31.955  finish=length  prompt_tokens=124297  completion_tokens=6775
/// 04:19:31.989  StepCompleted [3/5] — INCOMPLETE: the context window filled up
/// </code>
///
/// <para>One trim, at the step boundary, then five minutes and a dead step — and the two steps
/// after it skipped with it. 6,775 is exactly 131,072 − 124,297: the answer ran out of window
/// mid-<c>edit_file</c>.</para>
///
/// <para><b>The reserve cost the remedy, not the tokens.</b> Because the guard never fired again,
/// the consecutive-trim count never passed one, and the handover that exists for exactly this case
/// — a window that will not stay under its budget — sat unused while the model wrote the same
/// twenty VERIFIED lines ten times over at 95% occupancy.</para>
///
/// <para><b>2,048 was right once.</b> It was chosen when a window was 8,192, where it means a
/// quarter. Nothing marked it as a fraction wearing a constant's clothes, so it survived the window
/// growing sixteen-fold.</para>
/// </summary>
public sealed class TheRoomKeptForAnAnswerTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"read a lot"}""";

    /// <summary>The window this was measured on.</summary>
    private const int Window = 131_072;

    /// <summary>
    /// What the reserve must be at this window, and so what a trim must trim TO: an eighth of the
    /// window. It was a fixed ceiling twice - 2,048, then 8,192 - and wrong for the next model both
    /// times (an answer of 14,000 tokens did not fit the second), so it is a proportion now, and a
    /// per-provider setting for anything else.
    /// </summary>
    private const int Budget = Window - Window / 8;

    private static async Task<int> LeftAfterTheFirstTrim(FakeChatProvider provider, EngineFixture fx)
    {
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "read a lot");
        var text = events.Text();

        var announced = Regex.Match(text, $@"about (\d+) of {Window} tokens now");
        Assert.True(announced.Success,
                    "a trim must say how much of the window is left after it: " + Excerpt(text));

        return int.Parse(announced.Groups[1].Value);
    }

    private static Turn[] ManyReads(EngineFixture fx)
    {
        var script = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 0; i < 70; i++)
        {
            fx.Write($"page{i}.md", new string('x', 9_000));
            script.Add(Turn.Calls1("read_file", $$"""{"path":"page{{i}}.md"}""", $"c{i}"));
        }
        return script.ToArray();
    }

    /// <summary>
    /// THE ONE THAT MATTERS. The assertion is on what the trim trims TO, not on when it fires —
    /// the system prompt and the tool definitions move the trigger point around, but they do not
    /// move the target. Trimming stops as soon as the transcript is under budget, so with the old
    /// 2,048 reserve it would land just under 30,720 and with 8,192 just under 24,576; a single
    /// dropped <c>read_file</c> result is about 2,700 tokens, far too coarse for the old code to
    /// reach this number by accident.
    /// </summary>
    [Fact]
    public async Task A_trim_leaves_room_for_an_answer_and_says_how_much_is_left()
    {
        using var fx = new EngineFixture();

        var left = await LeftAfterTheFirstTrim(new FakeChatProvider(ManyReads(fx)) { Window = Window }, fx);

        Assert.True(left <= Budget,
                    $"the trim left {left} of {Window} tokens, so only {Window - left} for the answer - "
                    + $"it must trim to {Budget} or below, keeping an eighth of the window back.");
    }

    /// <summary>
    /// A reserve configured for the provider is the one used. A model known to write long answers
    /// into a small window is the case the setting exists for.
    /// </summary>
    [Fact]
    public async Task A_configured_reserve_is_the_one_kept()
    {
        using var fx = new EngineFixture();

        var left = await LeftAfterTheFirstTrim(
            new FakeChatProvider(ManyReads(fx)) { Window = Window, Reserve = 40_000 }, fx);

        Assert.True(left <= Window - 40_000, $"the trim left {left} of {Window}; 40,000 were to be kept back");
    }

    /// <summary>
    /// THE BOUNDARY. A small window is governed by the proportion, not the ceiling: an 8,192 window
    /// reserves 1,024, and raising the ceiling to 8,192 must not start reserving the whole thing.
    /// A guard that refused every request on a small model would be worse than the cut it prevents.
    /// </summary>
    [Fact]
    public async Task A_small_window_still_has_room_to_work_in()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", new string('x', 4_000));

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"a.md"}"""),
            Turn.Says("Read it.")) { Window = 8_192 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "read a lot");

        // It got through: a 4,000-character read is ordinary work and an 8,192 window holds it.
        Assert.False(events.Has(Enactive.Core.Events.EventKind.TaskFailed), Excerpt(events.Text()));
    }

    private static string Excerpt(string text)
        => text.Length <= 2_000 ? text : text[..2_000] + " …";
}

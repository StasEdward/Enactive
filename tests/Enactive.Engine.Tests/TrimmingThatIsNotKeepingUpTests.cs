namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A window that will not stay under its budget hands the step over instead of being nibbled at.
///
/// <para><b>Measured 2026-09-24 03:09, run 7034e2.</b> Step 2 trimmed the window SEVEN times —
/// each one freeing two or three thousand tokens that the next few turns ate again — and then
/// died:</para>
///
/// <code>
/// … dropped the contents of 9 earlier tool message(s) (about 63119 of 65536 tokens now)
/// … dropped the contents of 4 earlier tool message(s) (about 60996 of 65536 tokens now)
/// INCOMPLETE: the context window filled up: 61121 of 65536 tokens
/// </code>
///
/// <para>On its <b>46th turn</b> — fourteen short of <c>TurnsBeforeHandover</c>, which would have
/// emptied the window rather than nibbling at it. Two mechanisms that each work, and nothing
/// between them: trimming is a nibble, a handover is a reset, and the step was trimmed to death
/// with the reset sitting unused.</para>
///
/// <para>The turn count was always a rough proxy for "this conversation has got long". A window
/// that keeps returning to its ceiling is that same fact, measured rather than guessed.</para>
/// </summary>
public sealed class TrimmingThatIsNotKeepingUpTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"a long job"}""";

    /// <summary>
    /// A stated window is what makes the guard run at all: a provider that declares none is never
    /// trimmed against, which is every cloud provider and was the whole of §9br.
    /// </summary>
    private const int Window = 8192;

    /// <summary>
    /// Every turn reads the same big file back, so each tool result is large and the transcript
    /// climbs to the ceiling and stays there — the shape of the reported run, where 128 reads and
    /// searches kept refilling what trimming had just freed.
    /// </summary>
    [Fact]
    public async Task A_window_that_keeps_filling_is_cut_rather_than_nibbled()
    {
        using var fx = new EngineFixture();

        // DIFFERENT files each turn - reading one over and over is a stall, and the stall guard is
        // right to say so.
        //
        // Since 2026-09-24 a trim cuts to HALF the window rather than to just under the budget
        // (see TrimmingCutsDeep below), so a window that keeps filling now means each turn brings
        // back more than a deep cut freed: four files of 4,000 characters per turn, through
        // read_files. Anything less, and one deep trim holds - which is the point of cutting deep.
        var script = new List<Turn> { Turn.Says(QuickAction) };
        var file = 0;

        for (var turn = 0; turn < 12; turn++)
        {
            var paths = new List<string>();
            for (var k = 0; k < 4; k++, file++)
            {
                fx.Write($"page{file}.md", new string('x', 4_000));
                paths.Add($"\"page{file}.md\"");
            }
            script.Add(Turn.Calls1("read_files", $$"""{"paths":[{{string.Join(",", paths)}}]}""", $"c{turn}"));
        }
        script.Add(Turn.Says("Finished."));

        // The note is answered whenever the engine asks for it, not at a position this test would
        // have to predict.
        var provider = new FakeChatProvider(script.ToArray())
        {
            Window = Window,
            Answering = r => r.Messages.Any(m => (m.Content ?? "").Contains("Before you continue", StringComparison.Ordinal))
                && r.Messages[^1].Content?.Contains("Before you continue", StringComparison.Ordinal) == true
                ? Turn.Says("Done: the first pages read. Left: the rest.")
                : null
        };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        var text = events.Text();

        // It was cut, and it said WHY it was cut - which is the half that tells a person the
        // difference between a long step and one whose window will not hold it.
        Assert.Contains("trimming is not keeping up", text, StringComparison.Ordinal);
        Assert.Contains("fresh conversation", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOUNDARY. One or two trims is a big tool result passing through, and a step recovers
    /// from that by itself. Cutting on the first one would throw away a conversation over a single
    /// large file.
    /// </summary>
    [Fact]
    public async Task One_large_result_passing_through_is_not_a_reason_to_cut()
    {
        using var fx = new EngineFixture();
        fx.Write("big.md", new string('x', 5_000));
        fx.Write("small.md", "tiny");

        var script = new List<Turn>
        {
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"big.md"}"""),
            Turn.Calls1("read_file", """{"path":"small.md"}""", "c2"),
            Turn.Calls1("read_file", """{"path":"small.md"}""", "c3"),
            Turn.Calls1("read_file", """{"path":"small.md"}""", "c4"),
            Turn.Says("Read them.")
        };

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(script.ToArray()) { Window = Window },
                     EngineFixture.Role("developer")),
            "a long job");

        Assert.DoesNotContain("trimming is not keeping up", events.Text(), StringComparison.Ordinal);
        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// ONE trim buys room: it cuts to half the window, not to just under the budget.
    ///
    /// <para><b>Measured 2026-09-24 15:32-15:35, run a2142be6.</b> Three trims on one step in three
    /// minutes, each cutting only to just under the budget and so each followed within a few turns
    /// by the next - and every trim rewrites the prompt near its start, so every provider with a
    /// prefix cache reads the whole prompt again: about 110,000 tokens, some 55 seconds each on
    /// that machine. Nothing here is about that model: a hosted provider pays the same re-read in
    /// money.</para>
    /// </summary>
    [Fact]
    public async Task One_trim_cuts_to_half_the_window()
    {
        using var fx = new EngineFixture();
        const int big = 32_768;

        var script = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 0; i < 40; i++)
        {
            fx.Write($"page{i}.md", new string('x', 9_000));
            script.Add(Turn.Calls1("read_file", $$"""{"path":"page{{i}}.md"}""", $"c{i}"));
        }
        script.Add(Turn.Says("Finished."));

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(script.ToArray()) { Window = big }, EngineFixture.Role("developer")),
            "a long job");

        var text = events.Text();
        var announced = System.Text.RegularExpressions.Regex.Match(text, $@"about (\d+) of {big} tokens now");
        Assert.True(announced.Success, text.Length > 2000 ? text[..2000] : text);

        // Cut to just under the budget, this lands above 28,000; cut deep, at or below 16,384.
        var left = int.Parse(announced.Groups[1].Value);
        Assert.True(left <= big / 2, $"the first trim left {left} of {big} - it must cut to half the window");
    }
}


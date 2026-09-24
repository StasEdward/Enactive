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

        // DIFFERENT files each turn. Reading one file over and over is a stall, and the stall guard
        // is right to say so - it fired here on the first attempt at this test. The reported run
        // made 128 distinct reads and searches, which is real work whose results happen to be big,
        // and that is the shape the window guard has to survive.
        //
        // Each result is a good slice of the budget but not larger than it: a single result bigger
        // than the whole window is a different failure - trimming with nothing to give - and never
        // reaches the third consecutive trim this is about.
        var script = new List<Turn> { Turn.Says(QuickAction) };

        void Read(int i)
        {
            fx.Write($"page{i}.md", new string('x', 5_000));
            script!.Add(Turn.Calls1("read_file", $$"""{"path":"page{{i}}.md"}""", $"c{i}"));
        }

        // Five reads fill the window; the third consecutive trim falls on the fifth, and the
        // handover asks its question there. The fake serves streamed and non-streamed turns from
        // ONE queue, so the answer has to sit at exactly the position the engine will ask it -
        // the same bargain AStepThatHandsOverToItselfTests documents.
        for (var i = 0; i < 5; i++) Read(i);
        script.Add(Turn.Says("Done: pages 0-4 read. Left: the rest."));

        for (var i = 5; i < 12; i++) Read(i);
        script.Add(Turn.Says("Finished."));

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(script.ToArray()) { Window = Window },
                     EngineFixture.Role("developer")),
            "a long job");

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
}

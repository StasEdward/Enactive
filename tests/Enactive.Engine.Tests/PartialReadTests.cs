namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A whole-file write of a file the step has only seen part of.
///
/// <para><c>read_file</c> ends a shortened result with "… showing lines 1–400 of 518. Read on with
/// offset 401." On 2026-09-07 a model ignored that line and rewrote the file from the part it had
/// read; the 118 lines it never saw were gone. The defect log carried this as open with the
/// remedy already named: announcing a cut is necessary and not sufficient, because a sentence is
/// advice and advice can be skipped. Track what the step has READ, and treat a whole-file write of a
/// partially-read file as the dangerous case.</para>
///
/// <para>Half of these tests are about what the guard must NOT do. A rule this shape is one careless
/// clause away from refusing ordinary work — creating a file, deliberately overwriting one, or
/// reading a long file in the two windows the tool itself asks for.</para>
/// </summary>
public sealed class PartialReadTests
{
    /// <summary>
    /// Long enough that one read cannot cover it: the tool's window is 400 lines. Deliberately
    /// without a trailing newline, so the line count in these tests is the number written here -
    /// a final "\n" makes an empty last line, and 519 in an assertion about 518 is a puzzle nobody
    /// should have to solve twice.
    /// </summary>
    private static string LongFile(int lines = 518)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= lines; i++)
        {
            if (i > 1) sb.Append('\n');
            sb.Append("line ").Append(i);
        }
        return sb.ToString();
    }

    private const string QuickPlan = """{"disposition":"quick_action","title":"do the thing"}""";

    private static Turn Reads(string path, int? offset = null)
        => Turn.Calls1(
            "read_file",
            offset is { } n ? $$"""{"offset":{{n}},"path":"{{path}}"}""" : $$"""{"path":"{{path}}"}""",
            "r" + (offset ?? 0));

    private static Turn WritesWhole(string path, string content = "replaced\\nwholesale\\n")
        => Turn.Calls1("write_file", $$"""{"path":"{{path}}","content":"{{content}}","allow_shrink":true}""", "w");

    /// <summary>The LAST thing a tool said to the model - joining them all buries it in the file.</summary>
    private static string LastToolReply(FakeChatProvider provider)
        => provider.Requests[^1].Messages
            .Last(m => m.Role == Enactive.Core.Chat.ChatRole.Tool)
            .Content ?? "";

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>
    /// The reported shape: read 400 of 518 lines, then write the whole file. The write is refused
    /// and the file is untouched — including the 118 lines nobody looked at.
    /// </summary>
    [Fact]
    public async Task A_whole_file_write_after_a_partial_read_is_refused()
    {
        using var fx = new EngineFixture();
        fx.Write("big.txt", LongFile());

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Reads("big.txt"),
            WritesWhole("big.txt"),
            Turn.Says("Rewrote it."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        Assert.Contains("line 518", fx.Read("big.txt"));
        Assert.DoesNotContain("wholesale", fx.Read("big.txt"));

        // And it is a refusal, not a silence: the run does not come back Completed over it.
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// The refusal names both ways out, and the second one is the point: read the rest. An
    /// alternative the model cannot act on is the same as no alternative — §9j and §9k were both
    /// about advice that could not be followed.
    /// </summary>
    [Fact]
    public async Task The_refusal_says_how_many_lines_were_never_seen_and_what_to_do()
    {
        using var fx = new EngineFixture();
        fx.Write("big.txt", LongFile());

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Reads("big.txt"),
            WritesWhole("big.txt"),
            Turn.Says("It refused."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        var told = LastToolReply(provider);

        Assert.Contains("lines 1-400 of 518", told);
        Assert.Contains("118 line(s) it has never seen", told);
        Assert.Contains("edit_file", told);
        Assert.Contains("\"offset\": 401", told);
    }

    /// <summary>A different spelling of the path is the same file, or the guard is decoration.</summary>
    [Fact]
    public async Task Another_spelling_of_the_path_does_not_get_round_it()
    {
        using var fx = new EngineFixture();
        fx.Write("nested/big.txt", LongFile());

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Reads("nested/big.txt"),
            WritesWhole("./nested/big.txt"),
            Turn.Says("Rewrote it."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        Assert.Contains("line 518", fx.Read("nested/big.txt"));
    }

    // ── what it must not break ──────────────────────────────────────────────

    /// <summary>
    /// Read in full in the two windows the tool itself asks for, and the write goes ahead. Refusing
    /// this would punish the one behaviour the whole guard exists to encourage.
    /// </summary>
    [Fact]
    public async Task Reading_the_rest_first_makes_the_write_allowed()
    {
        using var fx = new EngineFixture();
        fx.Write("big.txt", LongFile());

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Reads("big.txt"),
            Reads("big.txt", 401),
            WritesWhole("big.txt"),
            Turn.Says("Rewrote it, having read all of it."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        Assert.Contains("wholesale", fx.Read("big.txt"));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A file short enough to arrive in one read is read in full, and writing it is fine.</summary>
    [Fact]
    public async Task A_short_file_read_in_one_go_can_be_rewritten()
    {
        using var fx = new EngineFixture();
        fx.Write("small.txt", "alpha\nbeta\ngamma\n");

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Reads("small.txt"),
            WritesWhole("small.txt"),
            Turn.Says("Rewrote it."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        Assert.Contains("wholesale", fx.Read("small.txt"));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A file the step never read is not this guard's business. Creating one, or overwriting one
    /// deliberately, is ordinary work — the shrink guard in write_file is what stands between that
    /// and a rewrite meant as a small change.
    /// </summary>
    [Fact]
    public async Task A_file_the_step_never_read_is_written_as_before()
    {
        using var fx = new EngineFixture();
        fx.Write("big.txt", LongFile());

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            WritesWhole("big.txt"),
            Turn.Says("Overwrote it deliberately."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "overwrite it");

        Assert.Contains("wholesale", fx.Read("big.txt"));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// And a partial read does not stop an EDIT. edit_file replaces one exact passage and leaves the
    /// rest alone, which is precisely the thing the refusal recommends; blocking it too would leave
    /// the model with nothing it could do.
    /// </summary>
    [Fact]
    public async Task A_partial_read_does_not_block_an_edit()
    {
        using var fx = new EngineFixture();
        fx.Write("big.txt", LongFile());

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Reads("big.txt"),
            Turn.Calls1("edit_file", """{"path":"big.txt","old_string":"line 7\n","new_string":"LINE SEVEN\n"}""", "e"),
            Turn.Says("Edited one line."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "change line 7");

        Assert.Contains("LINE SEVEN", fx.Read("big.txt"));
        Assert.Contains("line 518", fx.Read("big.txt"));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }
}

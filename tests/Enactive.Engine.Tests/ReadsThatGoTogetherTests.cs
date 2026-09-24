namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Fewer turns for the same reading: a search shows the lines around each match, and read_file reads
/// several files in one call.
///
/// <para><b>Measured 2026-09-24, run 71a546.</b> 113 model calls, the prompt growing from 2,368 to
/// 75,323 tokens. 87 read_file calls and not one read_files; of 55 searches, 30 were followed at once by
/// a read_file of the same place. Every turn re-sends the whole conversation, so a turn spent on one
/// small read is the most expensive way there is to read - on any provider, for any task.</para>
/// </summary>
public sealed class ReadsThatGoTogetherTests
{
    private static string Numbered(int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => $"line {i}"));

    // ── search_files: the lines around a match ───────────────────────────────

    /// <summary>SearchFilesTool.DefaultContextLines — two lines either side, marked as grep marks them.</summary>
    [Fact]
    public async Task A_match_comes_with_the_lines_around_it()
    {
        using var fx = new EngineFixture();
        fx.Write("f.txt", Numbered(10).Replace("line 5", "line 5 needle"));

        var result = await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("f.txt-3- line 3", result.Output, StringComparison.Ordinal);
        Assert.Contains("f.txt-4- line 4", result.Output, StringComparison.Ordinal);
        Assert.Contains("f.txt:5: line 5 needle", result.Output, StringComparison.Ordinal);
        Assert.Contains("f.txt-7- line 7", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("line 2", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("line 8", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Blocks that overlap are printed once, a match inside another's context is still marked as a
    /// match, and blocks that do not touch are separated.
    /// </summary>
    [Fact]
    public async Task Overlapping_blocks_are_printed_once_and_apart_ones_separated()
    {
        using var fx = new EngineFixture();
        var text = Numbered(30).Replace("line 5\n", "line 5 needle\n")
                               .Replace("line 7\n", "line 7 needle\n")
                               .Replace("line 20\n", "line 20 needle\n");
        fx.Write("f.txt", text);

        var output = (await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""")).Output!;

        Assert.Single(output.Split('\n'), l => l.Contains("line 6", StringComparison.Ordinal));
        Assert.Contains("f.txt:7: line 7 needle", output, StringComparison.Ordinal);
        Assert.Contains("--", output, StringComparison.Ordinal);
        Assert.True(output.IndexOf("line 9", StringComparison.Ordinal) < output.IndexOf("--", StringComparison.Ordinal));
    }

    /// <summary>THE BOUNDARY. Context 0 is the old listing, one line per match and no separators.</summary>
    [Fact]
    public async Task Context_zero_lists_the_matching_lines_only()
    {
        using var fx = new EngineFixture();
        fx.Write("f.txt", Numbered(30).Replace("line 5\n", "line 5 needle\n").Replace("line 20\n", "line 20 needle\n"));

        var output = (await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle","context":0}""")).Output!;

        Assert.Contains("f.txt:5: line 5 needle", output, StringComparison.Ordinal);
        Assert.Contains("f.txt:20: line 20 needle", output, StringComparison.Ordinal);
        Assert.DoesNotContain("line 4", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--", output, StringComparison.Ordinal);
    }

    /// <summary>SearchFilesTool.MaxContextLines — more than that is a read, and the answer says so.</summary>
    [Fact]
    public async Task Context_past_the_limit_is_held_to_it_and_says_so()
    {
        using var fx = new EngineFixture();
        fx.Write("f.txt", Numbered(60).Replace("line 30\n", "line 30 needle\n"));

        var output = (await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle","context":50}""")).Output!;

        Assert.Contains("f.txt-20- line 20", output, StringComparison.Ordinal);
        Assert.DoesNotContain("line 19", output, StringComparison.Ordinal);
        Assert.Contains("context is at most 10 lines", output, StringComparison.Ordinal);
    }

    /// <summary>A search stopped by its caps names context 0 as a way to see more matches.</summary>
    [Fact]
    public async Task A_capped_search_offers_context_zero()
    {
        using var fx = new EngineFixture();
        for (var file = 0; file < 5; file++)
            fx.Write($"f{file}.txt", string.Join('\n', Enumerable.Range(0, 60).Select(i => $"needle {i}")));

        var output = (await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""")).Output!;

        Assert.Contains("stopped at", output, StringComparison.Ordinal);
        Assert.Contains("\"context\": 0", output, StringComparison.Ordinal);
    }

    // ── read_file: several paths in one call ─────────────────────────────────

    [Fact]
    public async Task Read_file_reads_several_paths_in_one_call()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "ALPHA");
        fx.Write("b.md", "BRAVO");

        var result = await fx.Invoke(new ReadFileTool(), """{"paths":["a.md","b.md"]}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("ALPHA", result.Output, StringComparison.Ordinal);
        Assert.Contains("BRAVO", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_file_with_no_path_names_both_ways_to_give_one()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new ReadFileTool(), "{}");

        Assert.False(result.Success);
        Assert.Contains("'paths'", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file seen in a batch only as an excerpt - its start and its end - is not a basis for writing
    /// it whole, and ReadLedger says so; a file the same batch showed whole may be written.
    /// </summary>
    [Fact]
    public async Task A_file_read_in_a_batch_as_an_excerpt_is_not_rewritten_whole()
    {
        using var fx = new EngineFixture();
        fx.Write("big.md", Numbered(1_000));   // well past read_files' share for one file
        fx.Write("small.md", "small file");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"rewrite them"}"""),
            Turn.Calls1("read_file", """{"paths":["big.md","small.md"]}""", "r1"),
            Turn.Calls1("write_file", """{"path":"big.md","content":"short"}""", "w1"),
            Turn.Calls1("write_file", """{"path":"small.md","content":"rewritten"}""", "w2"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite them");

        var said = string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.Contains("seen 'big.md' only as an excerpt", said, StringComparison.Ordinal);
        Assert.Equal(Numbered(1_000), fx.Read("big.md"));
        Assert.Equal("rewritten", fx.Read("small.md"));
    }

    // ── the rule every role is given ─────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EngineFixture.ShippingRoles), MemberType = typeof(EngineFixture))]
    public void Every_role_is_told_to_read_together(string role)
    {
        var instructions = DefaultWorkers.Augment(EngineFixture.Role(role).Instructions);

        Assert.Contains("do not depend on each other go TOGETHER", instructions, StringComparison.Ordinal);
        Assert.Contains("'paths'", instructions, StringComparison.Ordinal);
    }
}

namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The aggregate reads: <c>count_matches</c>, <c>file_stats</c>, <c>compare_files</c>. Every one of
/// them answers a question with a number, a list or a verdict, and the property they all have to
/// keep is that NO file content comes back — that is the whole reason they exist, and it is the
/// thing a careless change would quietly undo.
/// </summary>
public sealed class AggregateToolsTests
{
    private static Task<ToolResult> Call(ITool tool, EngineFixture fx, string argumentsJson,
        IArtifactStore? store = null)
        => fx.Invoke(tool, argumentsJson, store);

    // ── count_matches ─────────────────────────────────────────────────────────────────

    // The point of the tool, stated as an assertion: the line that matched must not come back. A
    // count that returns its evidence is search_files with extra steps.
    [Fact]
    public async Task A_count_names_the_files_and_returns_none_of_their_content()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "alpha SENTINEL_TEXT_ONE omega\n");
        fx.Write("b.md", "nothing here\n");

        var result = await Call(new CountMatchesTool(), fx, """{"pattern":"SENTINEL_TEXT_ONE"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("a.md", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("b.md", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("omega", result.Output!, StringComparison.Ordinal);
    }

    // Occurrences, not lines with an occurrence. Counting one per line is the easy mistake and it
    // gives the wrong answer to "how many callers are there" for every file with two on a line.
    [Fact]
    public async Task A_count_counts_every_occurrence_not_every_matching_line()
    {
        using var fx = new EngineFixture();
        fx.Write("a.cs", "Foo(); Foo(); Foo();\nFoo();\n");

        var result = await Call(new CountMatchesTool(), fx, """{"pattern":"Foo\\(\\)"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, result.Metadata["matches"]);
        Assert.Equal(2, result.Metadata["matchingLines"]);
    }

    // Zero is the ANSWER to "is this string anywhere", and somebody deciding whether a symbol is
    // dead needs it to arrive as a success. NotFound would make an unresolved failure out of the
    // most useful thing this tool says.
    [Fact]
    public async Task Nothing_anywhere_is_a_successful_answer()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "nothing of interest\n");

        var result = await Call(new CountMatchesTool(), fx, """{"pattern":"ABSENT_EVERYWHERE"}""");

        Assert.True(result.Success, result.Error);
        Assert.False(result.IsAnswer);   // a plain Ok, not the NotFound path
        Assert.Equal(0, result.Metadata["matches"]);
        Assert.Contains("No matches", result.Output!, StringComparison.Ordinal);
    }

    // The same skip list as search_files, which is the reason WorkspaceScan exists: a count that
    // includes bin/ and a search that excludes it describe two different workspaces, and nothing
    // in the answer would say which one the model is looking at.
    [Fact]
    public async Task A_count_skips_the_same_build_folders_a_search_does()
    {
        using var fx = new EngineFixture();
        fx.Write("src/a.cs", "MARKER_IN_SOURCE\n");
        fx.Write("bin/generated.cs", "MARKER_IN_SOURCE\nMARKER_IN_SOURCE\n");

        var count = await Call(new CountMatchesTool(), fx, """{"pattern":"MARKER_IN_SOURCE"}""");
        var search = await Call(new SearchFilesTool(), fx, """{"pattern":"MARKER_IN_SOURCE"}""");

        Assert.Equal(1, count.Metadata["matches"]);
        Assert.Equal(1, search.Metadata["matches"]);
        Assert.DoesNotContain("bin/", count.Output!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>CountMatchesTool.MaxFilesListed</c>, driven past its cap. What separates this from a
    /// search that stops early: the search's NUMBERS stop too, and this one's do not. So the notice
    /// has to say that the totals still hold, or a reader takes the listing for the whole answer and
    /// reports 60 when the truth is 70.
    /// </summary>
    [Fact]
    public async Task A_count_over_many_files_lists_some_and_says_the_totals_still_hold()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i < 70; i++)
            fx.Write($"f{i:000}.md", "WIDESPREAD\n");

        var result = await Call(new CountMatchesTool(), fx, """{"pattern":"WIDESPREAD"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(70, result.Metadata["matches"]);          // the count is of all 70
        Assert.Equal(70, result.Metadata["files"]);
        Assert.True((bool)result.Metadata["listingTruncated"]!); // only the listing was cut
        Assert.Contains("not listed", result.Output!, StringComparison.Ordinal);
        Assert.Contains("count all of them", result.Output!, StringComparison.Ordinal);
    }

    // ── file_stats ────────────────────────────────────────────────────────────────────

    /// <summary><c>FileStatsTool.MaxFilesListed</c>, driven past its cap — same rule as above.</summary>
    [Fact]
    public async Task Stats_over_many_files_list_the_largest_and_say_the_totals_still_hold()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i < 110; i++)
            fx.Write($"f{i:000}.md", "x\n");

        var result = await Call(new FileStatsTool(), fx, """{"glob":"*.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(110, result.Metadata["files"]);
        Assert.True((bool)result.Metadata["listingTruncated"]!);
        Assert.Contains("not listed", result.Output!, StringComparison.Ordinal);
        Assert.Contains("include all of them", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stats_report_size_and_lines_without_any_content()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "one\ntwo\nthree\n");

        var result = await Call(new FileStatsTool(), fx, """{"glob":"*.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("doc.md", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("two", result.Output!, StringComparison.Ordinal);
        Assert.Equal(1, result.Metadata["files"]);
        Assert.Equal(4, result.Metadata["lines"]);   // trailing newline opens a fourth, empty line
    }

    // An empty file has no lines. Counting separators and adding one gives 1, which is the kind of
    // off-by-one that makes a plan for "every file with content" include the empty ones.
    [Fact]
    public async Task An_empty_file_has_no_lines()
    {
        using var fx = new EngineFixture();
        fx.Write("empty.md", "");

        var result = await Call(new FileStatsTool(), fx, """{"glob":"*.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Metadata["lines"]);
    }

    // A binary file is SIZED and not line-counted, and the answer says which files those were.
    // Silently counting zero lines for it would be a number presented as a fact about text.
    [Fact]
    public async Task A_binary_file_is_sized_but_not_line_counted_and_the_answer_says_so()
    {
        using var fx = new EngineFixture();
        fx.Write("text.md", "one\n");
        File.WriteAllBytes(fx.PathOf("blob.dat"), new byte[] { 1, 2, 0, 3, 4 });

        var result = await Call(new FileStatsTool(), fx, "{}");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Metadata["files"]);
        Assert.Equal(1, result.Metadata["filesLineCounted"]);
        Assert.Contains("not line-counted", result.Output!, StringComparison.Ordinal);
    }

    // ── compare_files ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_identical_files_compare_identical()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "same\ntext\n");
        fx.Write("b.md", "same\ntext\n");

        var result = await Call(new CompareFilesTool(), fx, """{"a":"a.md","b":"b.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.True((bool)result.Metadata["identical"]!);
    }

    /// <summary>
    /// The case this tool was built for, kept as a test because it is the one a per-line comparison
    /// gets wrong. On 2026-09-12 a run's output turned out to be an earlier artifact reproduced
    /// exactly — but hard-wrapped differently, so the two files share no line at all. Exact says
    /// "different at line 1" and is useless; normalised says what actually happened.
    /// </summary>
    [Fact]
    public async Task The_same_text_wrapped_differently_is_different_exactly_and_identical_normalised()
    {
        using var fx = new EngineFixture();
        fx.Write("wrapped.md", "The quick brown fox\njumps over the lazy dog.\n");
        fx.Write("flowed.md", "The quick brown fox jumps over the lazy dog.\n");

        var exact = await Call(new CompareFilesTool(), fx,
            """{"a":"wrapped.md","b":"flowed.md"}""");
        var normalised = await Call(new CompareFilesTool(), fx,
            """{"a":"wrapped.md","b":"flowed.md","ignore_whitespace":true}""");

        Assert.False((bool)exact.Metadata["identical"]!);
        Assert.True((bool)normalised.Metadata["identical"]!);

        // And the exact answer points at the mode that would have helped, rather than leaving
        // "different" as the last word on two files that say the same thing.
        Assert.Contains("ignore_whitespace", exact.Output!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>CompareFilesTool.MaxShownChars</c>, driven past its cap. The verdict is the answer here,
    /// but the two lines quoted under it are still file content, and a 4000-character line would put
    /// back exactly what this tool exists to keep out of the prompt.
    /// </summary>
    [Fact]
    public async Task A_very_long_differing_line_is_shown_cut()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", new string('a', 400) + "END_OF_A\n");
        fx.Write("b.md", new string('a', 400) + "END_OF_B\n");

        var result = await Call(new CompareFilesTool(), fx, """{"a":"a.md","b":"b.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("END_OF_A", result.Output!, StringComparison.Ordinal);
        Assert.Contains("…", result.Output!, StringComparison.Ordinal);
    }

    // A staged write is this run's version of the file. Comparing past it to the disk copy would
    // answer a question about the file as it was before the step touched it — which is exactly the
    // wrong answer for "did my write land".
    [Fact]
    public async Task A_staged_write_is_what_gets_compared()
    {
        using var fx = new EngineFixture();
        fx.Write("target.md", "old content\n");
        fx.Write("wanted.md", "new content\n");
        var staging = new StagingArtifactStore(fx.Root);

        await Call(new WriteFileTool(), fx,
            """{"path":"target.md","content":"new content\n"}""", staging);

        var result = await Call(new CompareFilesTool(), fx,
            """{"a":"target.md","b":"wanted.md"}""", staging);

        Assert.True(result.Success, result.Error);
        Assert.True((bool)result.Metadata["identical"]!);
        Assert.Equal("old content\n", fx.Read("target.md"));   // the disk copy is untouched
    }

    // "Identical" about one file compared with itself is true and worthless: a verification that
    // passes because both sides are the same path has verified nothing, and saying so is the only
    // way the caller finds out.
    [Fact]
    public async Task Comparing_a_file_with_itself_says_so_instead_of_identical()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "text\n");

        var result = await Call(new CompareFilesTool(), fx, """{"a":"a.md","b":"a.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.True((bool)result.Metadata["samePath"]!);
        Assert.Contains("same file", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_side_answers_rather_than_fails()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "text\n");

        var result = await Call(new CompareFilesTool(), fx, """{"a":"a.md","b":"absent.md"}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer);   // a lookup that found nothing has ANSWERED
    }

    // ── the notice on a cut read ──────────────────────────────────────────────────────

    /// <summary>
    /// A truncated read used to offer exactly one way forward — "Read on with offset N" — and got
    /// it taken: on 2026-09-12 a model paged the same ten-line region six times. Paging is still
    /// right when the file is being read, so the notice keeps it; what it must not do is leave
    /// reading as the only door.
    /// </summary>
    [Fact]
    public async Task A_cut_read_names_something_other_than_reading_on()
    {
        using var fx = new EngineFixture();
        fx.Write("long.md", string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}")));

        var result = await Call(new ReadFileTool(), fx, """{"path":"long.md","offset":1,"limit":5}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Read on with offset 6", result.Output!, StringComparison.Ordinal);
        Assert.Contains("count_matches", result.Output!, StringComparison.Ordinal);
    }

    // The reverse of the above: a read that reached the end of the file has nothing to page and
    // nothing to suggest, and a notice there would be noise on every complete read in every run.
    [Fact]
    public async Task A_complete_read_carries_no_notice_at_all()
    {
        using var fx = new EngineFixture();
        fx.Write("short.md", "one\ntwo\n");

        var result = await Call(new ReadFileTool(), fx, """{"path":"short.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("count_matches", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("Read on with offset", result.Output!, StringComparison.Ordinal);
    }

    // ── reachable, not merely registered ──────────────────────────────────────────────

    /// <summary>
    /// The defect this project has shipped four times: a tool registered by the host that no role
    /// names is a tool nobody can call. And a role somebody SAVED weeks ago is the one that matters
    /// — which is what <see cref="WorkerTools.WithImplied"/> is for.
    /// </summary>
    [Theory]
    [InlineData("count_matches")]
    [InlineData("file_stats")]
    [InlineData("compare_files")]
    public void Every_aggregate_read_reaches_a_role_that_can_read(string tool)
    {
        foreach (var role in DefaultWorkers.Seed(new Enactive.Core.Providers.ModelRef("fake", "fake-model")))
        {
            if (!role.ToolAllowlist.Contains("read_file", StringComparer.OrdinalIgnoreCase))
                continue;

            Assert.Contains(tool, role.ToolAllowlist, StringComparer.OrdinalIgnoreCase);
        }

        // And a saved role from before these existed gets them on the next migration, rather than
        // waiting for the user to notice a settings checkbox.
        var saved = WorkerTools.WithImplied(new[] { "read_file", "list_dir" });
        Assert.Contains(tool, saved, StringComparer.OrdinalIgnoreCase);
    }
}

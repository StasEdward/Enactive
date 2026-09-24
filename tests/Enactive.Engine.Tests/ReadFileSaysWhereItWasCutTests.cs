namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// When <c>read_file</c> cuts its output, it says where - and where to read on from.
///
/// <para><b>Measured 2026-09-24, run 71a546.</b> A window of many lines is cut at 8,000 characters,
/// part-way through some line. The tool said "line truncated at 8000 characters" and then "showing
/// lines 1-400 ... Read on with offset 401". Both untrue: it was the window that was cut, and the
/// lines after the cut were never shown - anyone who read on as told skipped them. A reviewer believed
/// the "line" in that sentence, told the step the limit was per line, the step corrected its report,
/// and the next review, reading the code, rejected the correction.</para>
/// </summary>
public sealed class ReadFileSaysWhereItWasCutTests
{
    [Fact]
    public async Task A_window_cut_part_way_says_which_line_and_reads_on_from_there()
    {
        using var fx = new EngineFixture();

        // 400 lines of 97 characters and a line break: 39,200 characters, cut at 8,000 inside line 82.
        fx.Write("long.md", string.Join("\n", Enumerable.Range(1, 400).Select(i => $"{i:D4} " + new string('x', 92))));

        var result = await fx.Invoke(new ReadFileTool(), """{"path":"long.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("this output is cut at 8000 characters, part-way through line 82", result.Output, StringComparison.Ordinal);
        Assert.Contains("showing lines 1–82 (line 82 only in part) of 400", result.Output, StringComparison.Ordinal);
        Assert.Contains("Read on with offset 82.", result.Output, StringComparison.Ordinal);

        // Not the promise it used to make: that lines up to 400 were shown.
        Assert.DoesNotContain("Read on with offset 401", result.Output, StringComparison.Ordinal);

        // And the read is recorded as covering only the lines shown WHOLE - ReadLedger trusts this
        // when it decides whether a whole-file write of the file may go ahead.
        Assert.Equal(81, Convert.ToInt32(result.Metadata["lastLine"]));
    }

    /// <summary>
    /// One line longer than the whole cap: reading on "from this line" would return the same cut, so
    /// the next read starts after it, and the tool says what can look inside it instead.
    /// </summary>
    [Fact]
    public async Task One_line_longer_than_the_cap_is_stepped_over_not_repeated()
    {
        using var fx = new EngineFixture();
        fx.Write("min.js", new string('m', 20_000) + "\nsecond line\n");

        var result = await fx.Invoke(new ReadFileTool(), """{"path":"min.js"}""");

        Assert.Contains("line 1 is longer than 8000 characters on its own", result.Output, StringComparison.Ordinal);
        Assert.Contains("Read on with offset 2.", result.Output, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. A window that fits is not cut and says nothing about cutting.</summary>
    [Fact]
    public async Task A_window_that_fits_says_nothing_about_a_cut()
    {
        using var fx = new EngineFixture();
        fx.Write("short.md", "one\ntwo\nthree\n");

        var result = await fx.Invoke(new ReadFileTool(), """{"path":"short.md"}""");

        Assert.DoesNotContain("cut", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Read on", result.Output, StringComparison.Ordinal);
    }
}

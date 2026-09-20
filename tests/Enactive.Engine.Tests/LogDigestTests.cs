namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Diagnostics;
using Xunit;

/// <summary>
/// Turning a log nobody can read into one that answers the question.
///
/// <para>The excerpt this replaces for large logs kept the start and the end and dropped the
/// middle — right for a few thousand lines, useless for a few hundred million, and impossible
/// above two gigabytes because it took the whole log as a <c>string</c>. What makes a log enormous
/// is not its records but the prompt and response bodies written beneath them, and those are what
/// a diagnosis needs least.</para>
/// </summary>
public sealed class LogDigestTests
{
    // The file sink's spelling: the level as the enum name padded to five, an eight-hex run.
    private const string FileLine =
        "12:34:56.789 Info  Orchestrator run=1a2b3c4d#2 [StepStarted] step 2 of 4: read the schema";

    // The log window's Export: a three-letter level and a six-hex run. A digest that knew only one
    // of these would silently produce nothing from half the logs it was handed.
    private const string ExportLine =
        "12:34:57.001 WRN Reviewer     1a2b3c#2 [ReviewFailed] the report cites a call that is not there";

    private static (string Text, DigestStats Stats) Digest(params string[] lines)
        => LogDigest.Of(new StringReader(string.Join("\n", lines)));

    [Fact]
    public void Both_spellings_of_a_record_line_are_read()
    {
        Assert.Equal(LogLevel.Info, LogDigest.Parse(FileLine)!.Value.Level);
        Assert.Equal("1a2b3c4d#2", LogDigest.Parse(FileLine)!.Value.Run);

        Assert.Equal(LogLevel.Warn, LogDigest.Parse(ExportLine)!.Value.Level);
        Assert.Equal("1a2b3c#2", LogDigest.Parse(ExportLine)!.Value.Run);
    }

    /// <summary>
    /// The whole point. A prompt body is written beneath its record as indented continuations, and
    /// at Trace so is every raw HTTP body — that is where the gigabytes are, and none of it is
    /// needed to say what happened.
    /// </summary>
    [Fact]
    public void The_bodies_written_beneath_a_record_are_dropped_and_counted()
    {
        var lines = new List<string> { FileLine };
        for (var i = 0; i < 5_000; i++)
            lines.Add("    | " + new string('x', 400));
        lines.Add(ExportLine);

        var (text, stats) = Digest(lines.ToArray());

        Assert.Equal(5_000, stats.DetailLinesDropped);
        Assert.Equal(2, stats.RecordsKept);
        Assert.DoesNotContain("xxxx", text, StringComparison.Ordinal);

        // Two megabytes of detail in, and what comes out is a page.
        Assert.True(text.Length < 2_000, $"digest was {text.Length} characters");
    }

    [Fact]
    public void A_warning_or_an_error_always_reaches_the_timeline()
    {
        var (text, _) = Digest(FileLine, ExportLine);

        Assert.Contains("ReviewFailed", text, StringComparison.Ordinal);
        Assert.Contains("WRN", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Categories that only restate the line before them are counted and not printed. Their
    /// records still appear in the totals, so nothing is hidden — only unsaid twice.
    /// </summary>
    [Fact]
    public void Noise_is_counted_but_not_listed()
    {
        var (text, stats) = Digest(
            "12:00:00.000 Info  Tools        run=aaaaaaaa#1 [ToolInvoked] ToolInvoked: read_file {\"path\":\"a.cs\"}",
            "12:00:00.100 Info  Tools        run=aaaaaaaa#1 [ToolResult] read_file -> ok: 120 lines",
            "12:00:00.101 Debug Tools        run=aaaaaaaa#1 [ToolCompleted] read_file in 98 ms");

        Assert.Equal(3, stats.RecordsKept);
        Assert.Contains("read_file=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolResult", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolCompleted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_are_totalled_for_each_run_and_step()
    {
        var (text, _) = Digest(
            "12:00:01.000 Info  Llm          run=bbbbbbbb#1 [UsageReported] tokens: 1200 in, 340 out",
            "12:00:02.000 Info  Llm          run=bbbbbbbb#1 [UsageReported] tokens: 800 in, 60 out");

        Assert.Contains("calls=  2", text, StringComparison.Ordinal);
        Assert.Contains("2,000", text, StringComparison.Ordinal);
        Assert.Contains("400", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that failed ten thousand times does not need ten thousand lines to say so — but the
    /// cap must announce itself, or the digest is the silent truncation it was written to replace.
    /// </summary>
    [Fact]
    public void The_timeline_is_capped_and_says_how_much_it_capped()
    {
        var lines = Enumerable.Range(0, 500)
            .Select(i => $"12:00:{i % 60:00}.000 ERR Orchestrator cccccc#1 [StepFailed] failure {i}")
            .ToArray();

        var (text, stats) = LogDigest.Of(new StringReader(string.Join("\n", lines)), maxTimelineEntries: 50);

        Assert.Equal(450, stats.TimelineDropped);
        Assert.Contains("450 further timeline entries are NOT SHOWN", text, StringComparison.Ordinal);

        // Counted even though they are not listed: the totals are what say it happened.
        Assert.Contains("ERRORS=500", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single record can carry a very long message — a whole command line, a whole path list.
    /// The digest cuts it, and the cut announces itself, like every other cap in the engine.
    /// </summary>
    [Fact]
    public void A_message_too_long_for_the_digest_is_cut_and_says_so()
    {
        var huge = new string('z', 4_000);
        var (text, _) = Digest($"12:00:00.000 ERR Orchestrator eeeeee#1 [StepFailed] {huge}");

        Assert.Contains("…", text, StringComparison.Ordinal);
        Assert.DoesNotContain(huge, text, StringComparison.Ordinal);
        Assert.True(text.Length < 1_500, $"the long message was not cut: {text.Length} characters");
    }

    [Fact]
    public void A_line_that_is_neither_a_record_nor_a_body_is_not_counted_as_kept()
    {
        var (_, stats) = Digest("this is not a log line at all", FileLine);

        Assert.Equal(2, stats.LinesRead);
        Assert.Equal(1, stats.RecordsKept);
    }

    /// <summary>
    /// The size of the log is the size of the file, not the size of anything held. Asserted by
    /// feeding more lines than a digest could keep and checking what comes out stays small.
    /// </summary>
    /// <summary>
    /// The floor under the digest. Said here rather than left to be noticed: an existing excerpt
    /// test began passing again because of this fallback, and a property that holds by accident is
    /// one the next change removes without anything going red.
    /// </summary>
    [Theory]
    [InlineData(0, 100, false)]      // nothing recognised at all — a file in some other shape
    [InlineData(10, 100, false)]     // a tenth: a format that has drifted
    [InlineData(100, 100, true)]     // a real log: every line is a record
    [InlineData(30, 100, true)]      // a fifth or better is enough to be worth sending
    public void A_log_the_digest_could_not_read_falls_back_to_the_excerpt(
        long kept, long lines, bool recognised)
    {
        Assert.Equal(recognised,
            Enactive.Agents.LogAnalyst.Recognised(new DigestStats(lines, 0, kept, 0)));
    }

    /// <summary>Prompt bodies do not count against it: a log is mostly them, and that is normal.</summary>
    [Fact]
    public void The_bodies_are_not_held_against_the_digest()
    {
        Assert.True(Enactive.Agents.LogAnalyst.Recognised(
            new DigestStats(LinesRead: 100_000, DetailLinesDropped: 99_000, RecordsKept: 1_000, TimelineDropped: 0)));
    }

    [Fact]
    public void A_log_far_larger_than_the_digest_is_read_through()
    {
        var big = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            big.Append("12:00:00.000 Info  Llm          run=dddddddd#1 [UsageReported] tokens: 10 in, 2 out\n");
            big.Append("    | ").Append('y', 500).Append('\n');
        }

        var (text, stats) = LogDigest.Of(new StringReader(big.ToString()), maxTimelineEntries: 100);

        Assert.Equal(40_000, stats.LinesRead);
        Assert.Equal(20_000, stats.DetailLinesDropped);
        Assert.Contains("calls=20,000".Replace(",", ""), text.Replace(",", ""), StringComparison.Ordinal);
        Assert.True(text.Length < 10_000, $"digest was {text.Length} characters");
    }
}

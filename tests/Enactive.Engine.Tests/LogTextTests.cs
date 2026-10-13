namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Diagnostics;
using Xunit;

/// <summary>
/// The log as text (LogText), shared by the window's log and the service's log page - and what a model is given to
/// analyse. Pinned, because the export a person compares an answer against has to be the text the answer was about.
/// </summary>
public sealed class LogTextTests
{
    private static readonly Guid Run = Guid.Parse("0123456789abcdef0123456789abcdef");

    private static LogEntry Entry(long seq, DateTimeOffset at, string message, string? detail = null, int? step = null)
        => new(seq, at, LogLevel.Warn, LogSource.Tool, Run, null, message, detail, "run_command", step);

    [Fact]
    public void A_line_says_time_level_source_run_step_category_and_message()
    {
        var at = new DateTimeOffset(2026, 10, 12, 9, 30, 15, 250, TimeSpan.Zero);

        var line = LogText.Line(Entry(1, at, "dotnet build exited 1", step: 2));

        Assert.Equal($"{at.ToLocalTime():HH:mm:ss.fff}  WRN  Tool         012345#2  [run_command]  dotnet build exited 1", line);
    }

    /// <summary>One line is one line: a message's own line breaks are flattened, and a long one is cut.</summary>
    [Fact]
    public void A_long_message_with_line_breaks_stays_one_line_of_bounded_length()
    {
        var line = LogText.Line(Entry(1, DateTimeOffset.Now, "first\r\nsecond " + new string('x', 400)));

        Assert.DoesNotContain('\n', line);
        Assert.EndsWith("…", line);
        Assert.Contains("first  second", line);
    }

    /// <summary>In time order - entries arrive out of it - with each detail indented under its own line.</summary>
    [Fact]
    public void The_export_is_in_time_order_with_details_indented()
    {
        var early = DateTimeOffset.Now;
        var text = LogText.Render([
            Entry(2, early.AddSeconds(1), "second"),
            Entry(1, early, "first", detail: "stdout line 1\nstdout line 2")
        ]);

        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith("first", lines[0]);
        Assert.Equal(["    | stdout line 1", "    | stdout line 2"], lines[1..3]);
        Assert.EndsWith("second", lines[3]);
    }

    /// <summary>
    /// The time reads the same on every machine: a format's ":" is the culture's time separator, and under one whose
    /// separator is not a colon the same line was another text - the export a person compares an answer against too.
    /// </summary>
    [Fact]
    public void A_line_s_time_is_the_same_under_any_culture()
    {
        var at = new DateTimeOffset(2026, 10, 12, 9, 30, 15, 250, TimeSpan.Zero);
        var expected = at.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A culture whose time separator is not a colon, made so whatever the machine's own data says.
            var odd = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            odd.DateTimeFormat.TimeSeparator = ".";
            System.Globalization.CultureInfo.CurrentCulture = odd;

            Assert.StartsWith(expected + "  WRN", LogText.Line(Entry(1, at, "dotnet build exited 1")));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }
}

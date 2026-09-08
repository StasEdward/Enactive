namespace Enactive.Engine.Tests;

using Enactive.Core.Diagnostics;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Log files were written one per day, appended forever, and deleted by nobody.
///
/// <para>The size is not incidental to the feature: the prompt bodies are what make these files
/// worth reading when a run goes wrong, and a single documentation run exported at three megabytes
/// on 2026-09-08. A day of ordinary use is tens of megabytes, kept until somebody notices the
/// folder.</para>
///
/// <para>Deleting files is the dangerous half. Everything here is about what must NOT go: a file
/// this sink could not have written, a file whose name does not carry a date, the file currently
/// being written, and — when retention is off — anything at all.</para>
/// </summary>
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "enactive-logtests", Guid.NewGuid().ToString("N"));

    public LogRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    private string Old(int daysAgo, string name = "enactive-{0}.log")
    {
        var day = DateTime.Now.AddDays(-daysAgo);
        var path = Path.Combine(_dir, string.Format(name, day.ToString("yyyyMMdd")));
        File.WriteAllText(path, "old\n");
        return path;
    }

    private static void WriteSomething(FileLogSink sink)
        => sink.Log(new LogEntry(1, DateTimeOffset.Now, LogLevel.Info, LogSource.System,
                                 null, null, "hello", null, null));

    [Fact]
    public void A_file_older_than_the_window_goes()
    {
        var stale = Old(30);
        using var sink = new FileLogSink(_dir, retentionDays: 14);

        WriteSomething(sink);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_file_inside_the_window_stays()
    {
        var recent = Old(3);
        using var sink = new FileLogSink(_dir, retentionDays: 14);

        WriteSomething(sink);

        Assert.True(File.Exists(recent));
    }

    /// <summary>
    /// The first line of the new day's file says what the roll removed. A gap in the history with
    /// nothing explaining it is found by somebody looking for a run that is missing, which is the
    /// worst moment to have to guess why.
    /// </summary>
    [Fact]
    public void The_new_file_says_what_it_deleted()
    {
        var stale = Path.GetFileName(Old(30));
        using var sink = new FileLogSink(_dir, retentionDays: 14);

        WriteSomething(sink);
        sink.Dispose();

        var today = Path.Combine(_dir, $"enactive-{DateTime.Now:yyyyMMdd}.log");
        var text = File.ReadAllText(today);
        Assert.Contains("log retention: deleted 1 file(s)", text, StringComparison.Ordinal);
        Assert.Contains(stale, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero keeps everything. It is the behaviour that shipped, and someone choosing it is choosing
    /// it — which is why an unparseable number in the settings falls back to the default and not
    /// to this.
    /// </summary>
    [Fact]
    public void Retention_of_zero_deletes_nothing()
    {
        var ancient = Old(400);
        using var sink = new FileLogSink(_dir, retentionDays: 0);

        WriteSomething(sink);

        Assert.True(File.Exists(ancient));
    }

    /// <summary>
    /// Only files this sink could have written. Anything else in that folder belongs to somebody
    /// else — a saved copy, an export, another tool — and a retention policy that tidies away what
    /// it did not create is a data-loss bug wearing a feature's name.
    /// </summary>
    [Theory]
    [InlineData("enactive-notadate.log")]
    [InlineData("enactive-20260101-copy.log")]
    [InlineData("something-else.log")]
    [InlineData("enactive-20240101.log.bak")]
    public void A_file_this_sink_did_not_write_is_left_alone(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not mine\n");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-500));

        using var sink = new FileLogSink(_dir, retentionDays: 1);
        WriteSomething(sink);

        Assert.True(File.Exists(path), $"'{name}' was deleted and it was not this sink's to delete");
    }

    /// <summary>
    /// Today's file survives however small the window - by arithmetic rather than by a guard. The
    /// cutoff is the current day minus the window, so the current day is never below it. An explicit
    /// check was written first and removed: reverting it failed no test because nothing could reach
    /// it. This pins the behaviour, which is what a change to the arithmetic would break.
    /// </summary>
    [Fact]
    public void The_file_being_written_survives_a_window_of_one_day()
    {
        var sink = new FileLogSink(_dir, retentionDays: 1);

        WriteSomething(sink);
        var today = Path.Combine(_dir, $"enactive-{DateTime.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(today));

        // Closed before reading: the sink holds the file open for writing, and File.ReadAllText asks
        // for exclusive-of-writers sharing. An artefact of reading a live log in-process, not of the
        // sink - a person opening it in an editor gets FileShare.ReadWrite and no such trouble.
        sink.Dispose();

        Assert.Contains("hello", File.ReadAllText(today), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lowering the setting prunes at once. The UI builds this sink before it has read any
    /// settings, so the value arrives afterwards — and a person who lowers it should not have to
    /// wait until midnight to see the folder shrink.
    /// </summary>
    [Fact]
    public void Lowering_the_window_prunes_immediately()
    {
        var stale = Old(10);
        using var sink = new FileLogSink(_dir, retentionDays: 0);
        WriteSomething(sink);
        Assert.True(File.Exists(stale));

        sink.RetentionDays = 5;

        Assert.False(File.Exists(stale));
    }
}

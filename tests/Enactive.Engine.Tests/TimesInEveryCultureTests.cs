namespace Enactive.Engine.Tests;

using System.Globalization;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Xunit;

/// <summary>
/// Every time the window and the service's pages write is the window's "yyyy-MM-dd HH:mm" (and "HH:mm:ss", "MMM d")
/// under every culture. The service writes in its own process's culture, not the reader's: under fi-FI, whose time
/// separator is a dot and whose months are Finnish, the same run read "09.05" and "loka 1" on one screen and "09:05"
/// and "Oct 1" on another. Each projection is read under fi-FI here.
/// </summary>
public sealed class TimesInEveryCultureTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 9, 5, 7, TimeSpan.Zero);
    private readonly CultureInfo _was = CultureInfo.CurrentCulture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-times", Guid.NewGuid().ToString("N"));

    public TimesInEveryCultureTests()
    {
        // fi-FI writes the time with a dot ("09.05") and the months in Finnish.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _was;
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static string Invariant(DateTimeOffset at, string format)
        => at.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    private static RunSummary Summary(DateTimeOffset at)
        => new(Guid.NewGuid(), Guid.NewGuid(), "mail the digest", "qwen", at, at.AddSeconds(95), "Completed", [], []);

    [Fact]
    public void A_run_s_row_says_its_time_as_the_window_does()
    {
        Assert.Contains("\n" + Invariant(At, "yyyy-MM-dd HH':'mm") + " · ", RunRows.Tooltip(Summary(At)));

        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), At, At, "tidy the wiki", "tidy the wiki", null, null,
            [new CheckpointStep(Guid.NewGuid(), "first", [], "low", "Done")], [], [], [], 1, 0);
        Assert.StartsWith($"stopped {Invariant(At, "MMM d")}, {Invariant(At, "HH':'mm")} · ", RunRows.Meta(checkpoint));

        var weeksAgo = DateTimeOffset.Now.AddDays(-30);
        Assert.Equal(Invariant(weeksAgo, "MMM d"), RunRows.Ago(weeksAgo));
    }

    [Fact]
    public void A_past_run_s_head_says_its_time_as_the_window_does()
    {
        var record = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", At, At.AddSeconds(95.4), "Completed",
            [new RunEventRecord(At, nameof(EventKind.IntentReceived), "Intent", Payload: WorkEventPayload.RequestPayload("digest the inbox"))],
            [], [], null, null);

        Assert.StartsWith(Invariant(At, "yyyy-MM-dd HH':'mm") + " · qwen · ", RunDigest.Of(record).Meta);
        Assert.Equal("1m 35s", RunDigest.Duration(TimeSpan.FromSeconds(95.4)));
    }

    [Fact]
    public void A_timeline_line_says_its_time_as_the_window_does()
        => Assert.Equal(Invariant(At, "HH':'mm':'ss"), new RunEventViewModel(At, "StepStarted", "step 1").When);

    [Fact]
    public void A_schedule_s_outcome_and_a_past_run_to_repeat_say_their_time_as_the_window_does()
    {
        var item = new InboxItem(Guid.NewGuid(), Guid.NewGuid(), "result", "digest", "done", Guid.NewGuid(), "read", At);
        Assert.Equal(Invariant(At, "yyyy-MM-dd HH':'mm"), new ScheduleOutcomeLine(item).When);

        Assert.EndsWith($"({Invariant(At, "yyyy-MM-dd HH':'mm")})", WorkChoice.PastRun(Summary(At)).Label);
    }

    [Fact]
    public void The_question_before_deleting_a_file_says_when_it_was_last_changed_as_the_window_does()
    {
        var file = Path.Combine(_root, "notes.md");
        File.WriteAllText(file, "edited since");

        var question = RunHistoryService.DeleteFile(_root, "notes.md")!;

        Assert.Contains("last changed " + File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH':'mm", CultureInfo.InvariantCulture),
            question.Body);
    }
}

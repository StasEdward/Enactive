namespace Enactive.Engine.Tests;

using System.Collections.Specialized;
using Enactive.App.Ui;
using Enactive.Core.Diagnostics;
using Enactive.Workspace;

public sealed class UiHotPathTests
{
    [Fact]
    public void Tail_overflow_uses_one_notification_and_preserves_order()
    {
        var rows = new BatchObservableCollection<int>();
        rows.ReplaceWith(Enumerable.Range(0, 4000));
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => notifications.Add(e.Action);
        rows.AppendTail(Enumerable.Range(4000, 1000), 4000);
        Assert.Equal(Enumerable.Range(1000, 4000), rows);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
        rows.AppendTail(Enumerable.Range(5000, 10000), 4000);
        Assert.Equal(Enumerable.Range(11000, 4000), rows);
    }

    [Fact]
    public void Replace_can_filter_its_own_collection_without_losing_rows()
    {
        var rows = new BatchObservableCollection<int>();
        rows.ReplaceWith(Enumerable.Range(0, 20000));
        rows.ReplaceWith(rows.Where(n => n % 2 == 0));
        Assert.Equal(10000, rows.Count);
        Assert.Equal(19998, rows[^1]);
    }

    [Fact]
    public void Search_matches_payload_category_and_run_but_respects_level_and_source()
    {
        var run = Guid.NewGuid();
        var entry = new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Warn, LogSource.Tool,
            run, null, "Build failed", "Compiler CS0123", "dotnet", 3);
        var sources = new HashSet<LogSource> { LogSource.Tool };
        foreach (var search in new[] { "BUILD", "cs0123", "DOTNET", run.ToString("N")[..6] + "#3", " " })
            Assert.True(LogFilter.Matches(entry, LogLevel.Info, sources, search));
        Assert.False(LogFilter.Matches(entry, LogLevel.Error, sources, ""));
        Assert.False(LogFilter.Matches(entry, LogLevel.Info, [], ""));
        Assert.False(LogFilter.Matches(entry, LogLevel.Info, sources, "absent"));
    }

    [Fact]
    public void Searching_large_payload_does_not_allocate_a_payload_copy()
    {
        var entry = new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Info, LogSource.Llm,
            null, null, "response", new string('x', 1_000_000), null);
        var sources = new HashSet<LogSource> { LogSource.Llm };
        LogFilter.Matches(entry, LogLevel.Info, sources, "absent");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < 50; n++)
            Assert.False(LogFilter.Matches(entry, LogLevel.Info, sources, "absent"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 100_000);
    }

    [Fact]
    public async Task Pending_saves_coalesce_and_flush_waits_for_latest_snapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var written = new List<string>();
        var writer = new CoalescingWriter<string>(value =>
        {
            if (value == "first")
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            written.Add(value);
        }, TimeSpan.Zero);
        writer.Queue("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            writer.Queue("obsolete");
            writer.Queue("latest");
            Assert.False(writer.FlushAsync().IsCompleted);
        }
        finally { release.Set(); }
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "first", "latest" }, written);
        writer.Queue("after flush");
        await writer.FlushAsync();
        Assert.Equal("after flush", written[^1]);
    }

    [Fact]
    public async Task Failed_save_does_not_disable_later_saves()
    {
        var written = "";
        var writer = new CoalescingWriter<string>(value =>
        {
            if (value == "fail") throw new IOException();
            written = value;
        }, TimeSpan.Zero);
        writer.Queue("fail");
        await writer.FlushAsync();
        writer.Queue("ok");
        await writer.FlushAsync();
        Assert.Equal("ok", written);
    }
}

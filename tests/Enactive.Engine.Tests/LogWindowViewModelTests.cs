namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Diagnostics;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The global log's view (LogWindowViewModel), shared by the window and the service's log page from the application layer:
/// lines come in when the host drains them, at the host's pace; what Clear and the raw-wire switch do to the hub goes where
/// the host says (ILogHubChanges) - the window's own hub, or the service, which may refuse; Export hands the visible log to
/// the host when it says where it goes.
/// </summary>
public sealed class LogWindowViewModelTests : IDisposable
{
    private readonly LogHub _hub = new(minLevel: LogLevel.Debug);
    private Action? _drain;
    private bool _stopped;

    public void Dispose() => _hub.Dispose();

    /// <summary>The host's clock, held by the test: nothing comes in until the test drains.</summary>
    private IDisposable Drain(Action drain)
    {
        _drain = drain;
        return new Stopped(() => _stopped = true);
    }

    private sealed class Stopped(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    private LogWindowViewModel Open(ILogHubChanges? changes = null) => new(_hub, Drain, changes);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The view did not come to the state the test waited for.");
            await Task.Delay(10);
        }
    }

    /// <summary>Logs a line and waits for the hub's pump to have handed it on.</summary>
    private async Task Logged(string message, LogLevel level = LogLevel.Info)
    {
        var seen = new TaskCompletionSource();
        void OnEntry(LogEntry e) { if (e.Message == message) seen.TrySetResult(); }
        _hub.Entry += OnEntry;
        try
        {
            _hub.Log(new LogEntry(0, DateTimeOffset.UtcNow, level, LogSource.Host, null, null, message, null, null));
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _hub.Entry -= OnEntry;
        }
    }

    private static IEnumerable<string> Shown(LogWindowViewModel log) => log.Rows.Select(r => r.Entry.Message);

    [Fact]
    public async Task A_line_comes_in_when_the_host_drains_and_not_before()
    {
        await Logged("before the view");
        var log = Open();
        await Until(() => Shown(log).Contains("before the view"));

        await Logged("after the view");
        Assert.DoesNotContain("after the view", Shown(log));

        // Drained until it shows: a drain that lands while the view is still filtering leaves the line to that filter.
        await Until(() => { _drain!(); return Shown(log).Contains("after the view"); });
        await Until(() => log.Status == "2 shown · 2 captured · hub level ≥ Debug");

        log.Detach();
        Assert.True(_stopped);
    }

    [Fact]
    public async Task The_window_s_clear_empties_the_hub_and_a_view_only_clear_leaves_it()
    {
        await Logged("everybody's line");
        var viewOnly = Open(new ViewOnly());
        await Until(() => Shown(viewOnly).Any());

        viewOnly.ClearCommand.Execute(null);
        await Until(() => !Shown(viewOnly).Any());
        Assert.Contains(_hub.Snapshot(), e => e.Message == "everybody's line");

        var window = Open();
        await Until(() => Shown(window).Any());
        window.ClearCommand.Execute(null);
        await Until(() => !Shown(window).Any());
        Assert.Empty(_hub.Snapshot());
    }

    [Fact]
    public void The_window_s_switch_sets_the_hub_s_level_and_back()
    {
        var log = Open();
        Assert.False(log.CaptureRaw);

        log.CaptureRaw = true;
        Assert.Equal(LogLevel.Trace, _hub.MinLevel);
        Assert.Equal("Trace", log.SelectedLevel);

        log.CaptureRaw = false;
        Assert.Equal(LogLevel.Debug, _hub.MinLevel);
    }

    /// <summary>A refused switch stays where the hub is and says why; it never shows capture on while nothing is captured.</summary>
    [Fact]
    public void A_refused_switch_stays_off_and_says_why()
    {
        var log = Open(new ViewOnly(refusal: "Your role does not include changing the service's log level."));

        log.CaptureRaw = true;

        Assert.False(log.CaptureRaw);
        Assert.Equal(LogLevel.Debug, _hub.MinLevel);
        Assert.Equal("Your role does not include changing the service's log level.", log.Status);
    }

    /// <summary>A view opened while capture is on says so: somebody else turned it on, and it is on.</summary>
    [Fact]
    public void A_view_opened_while_capture_is_on_shows_it_on()
    {
        _hub.MinLevel = LogLevel.Trace;

        Assert.True(Open().CaptureRaw);
    }

    [Fact]
    public async Task Export_hands_the_visible_log_to_the_host_when_it_says_where()
    {
        await Logged("a warning to keep", LogLevel.Warn);
        await Logged("a line to leave out");
        var log = Open();
        log.SearchText = "keep";
        await Until(() => Shown(log).SequenceEqual(["a warning to keep"]));
        (string Name, string Text)? handed = null;
        log.ExportTo = (name, text) => { handed = (name, text); return Task.CompletedTask; };

        log.ExportCommand.Execute(null);
        await Until(() => handed is not null);

        Assert.Matches(@"^export-\d{8}-\d{6}\.log$", handed!.Value.Name);
        Assert.Equal(LogText.Render(log.Rows.Select(r => r.Entry)), handed.Value.Text);
        Assert.Contains("a warning to keep", handed.Value.Text);
        Assert.DoesNotContain("a line to leave out", handed.Value.Text);
        await Until(() => log.Status == $"Exported 1 rows → {handed.Value.Name}");
    }

    /// <summary>Under a culture with a calendar of its own (Thai: the year 2569) the file was named in that year.</summary>
    [Fact]
    public void The_export_s_name_is_in_the_same_digits_on_every_machine()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal("export-20261012-093015.log", LogWindowViewModel.ExportName(new DateTime(2026, 10, 12, 9, 30, 15)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }

    /// <summary>The service's kind: the hub is everybody's, so Clear leaves it, and the switch may be refused.</summary>
    private sealed class ViewOnly(string? refusal = null) : ILogHubChanges
    {
        public bool Capturing => false;
        public string? Capture(bool trace) => refusal;
        public void Cleared() { }
    }
}

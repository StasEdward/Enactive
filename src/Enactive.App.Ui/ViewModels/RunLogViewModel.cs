namespace Enactive.App.Ui.ViewModels;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Diagnostics;
using Enactive.Workspace;

/// <summary>
/// The log of ONE run, for the tab beside its steps: the same hub the global log window reads, kept
/// to the entries carrying this run's id. It answers "what actually went to the model in this run"
/// without leaving the window; the full log, across every run and with the filters, stays a window.
/// </summary>
internal sealed class RunLogViewModel : ObservableObject
{
    /// <summary>One run's worth of lines. Past that the oldest go - this is a tail, not an archive.</summary>
    private const int MaxRetained = 4000;

    private readonly LogHub _hub;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _drain;

    private Guid? _runId;
    private sealed record Selection(Guid Id);
    private Selection? _selection;
    private string _header = "No run yet.";

    public RunLogViewModel(LogHub hub)
    {
        _hub = hub;

        // The hub raises on its own pump thread; queue there and move to the UI thread in batches,
        // so a burst of token-level entries cannot flood the dispatcher one at a time.
        _drain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _drain.Tick += (_, _) => Drain();
        _drain.Start();
        _hub.Entry += OnEntry;
    }

    public BatchObservableCollection<LogRow> Rows { get; } = new();

    public string Header { get => _header; set => Set(ref _header, value); }

    /// <summary>
    /// Points the tab at a run. Called with the id off the first event of a run, because the id is
    /// the orchestrator's to mint - the caller never knows it before the run starts talking.
    /// </summary>
    public void SetRun(Guid runId)
    {
        if (_runId == runId)
            return;

        _runId = runId;
        Volatile.Write(ref _selection, new Selection(runId));
        _pending.Clear();
        Rows.Clear();
        Header = "Filtered to run " + runId.ToString("N")[..6];

        // The run has usually said a few things before the UI got here, so start from the backlog
        // rather than only from what arrives next.
        Rows.ReplaceWith(_hub.Snapshot().Where(entry => entry.RunId == runId)
            .TakeLast(MaxRetained).Select(entry => new LogRow(entry)));
    }

    public void Detach()
    {
        _hub.Entry -= OnEntry;
        _drain.Stop();
    }

    private void OnEntry(LogEntry entry)
    {
        if (Volatile.Read(ref _selection) is { } selection && entry.RunId == selection.Id)
            _pending.Enqueue(entry);
    }

    private void Drain()
    {
        if (_pending.IsEmpty)
            return;

        var batch = new List<LogRow>();
        var seen = Rows.Count == 0 ? 0 : Rows[^1].Entry.Seq;
        for (var n = 0; n < 1000 && _pending.TryDequeue(out var entry); n++)
            if (entry.RunId == _runId && entry.Seq > seen) batch.Add(new LogRow(entry));
        Rows.AppendTail(batch, MaxRetained);
    }
}

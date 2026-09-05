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

    public ObservableCollection<LogRow> Rows { get; } = new();

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
        Rows.Clear();
        Header = "Filtered to run " + runId.ToString("N")[..6];

        // The run has usually said a few things before the UI got here, so start from the backlog
        // rather than only from what arrives next.
        foreach (var entry in _hub.Snapshot())
            if (entry.RunId == runId)
                Rows.Add(new LogRow(entry));
    }

    public void Detach()
    {
        _hub.Entry -= OnEntry;
        _drain.Stop();
    }

    private void OnEntry(LogEntry entry)
    {
        if (_runId is { } id && entry.RunId == id)
            _pending.Enqueue(entry);
    }

    private void Drain()
    {
        if (_pending.IsEmpty)
            return;

        while (_pending.TryDequeue(out var entry))
            Rows.Add(new LogRow(entry));

        if (Rows.Count > MaxRetained)
            for (var i = Rows.Count - MaxRetained; i > 0; i--)
                Rows.RemoveAt(0);
    }
}

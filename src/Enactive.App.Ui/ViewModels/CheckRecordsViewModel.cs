namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Enactive.Application.Mvvm;
using Enactive.Application.Projections;

/// <summary>
/// "Run checks now" for a staged run and the records it kept (Plan Phase 3.2), as the window shows them: on the run's page
/// in the history, and beside the live run's proposals once it has ended. The words are the service's too
/// (CheckRecordViews). Running them is the window's (MainWindow.RunChecksNowAsync), as applying a proposal is: there is one
/// person here, so no role is asked - its staged apply asks none either.
/// </summary>
internal sealed class CheckRecordsViewModel : ObservableObject
{
    private bool _busy;
    private string _said = string.Empty;

    /// <param name="offered">The run is staged and was started with checks to run (ChecksNow.Refusal is null).</param>
    public CheckRecordsViewModel(bool offered)
    {
        Offered = offered;
        RunCommand = new RelayCommand(() => RunRequested?.Invoke(this), () => Offered && !Busy);
    }

    /// <summary>Whether the action is offered on this run at all.</summary>
    public bool Offered { get; }

    public string Explanation => CheckRecordViews.Explanation;

    public RelayCommand RunCommand { get; }

    /// <summary>The window runs them; this only asks.</summary>
    public event Action<CheckRecordsViewModel>? RunRequested;

    /// <summary>While the checks run: a second press is not a second set of commands.</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value))
                return;
            OnPropertyChanged(nameof(ButtonText));
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    public string ButtonText => Busy ? "Running checks…" : "Run checks now";

    /// <summary>What the last press came to, or why it was not run.</summary>
    public string Said
    {
        get => _said;
        set
        {
            if (Set(ref _said, value ?? string.Empty))
                OnPropertyChanged(nameof(HasSaid));
        }
    }

    public bool HasSaid => Said.Length > 0;

    /// <summary>The run's records, newest first - each "checked at" or "outdated", never a green.</summary>
    public ObservableCollection<CheckRecordView> Records { get; } = new();

    public bool HasRecords => Records.Count > 0;

    /// <summary>Shown for a run it is offered on, or one that has records from before.</summary>
    public bool IsShown => Offered || HasRecords;

    public void Show(IReadOnlyList<CheckRecordView> records)
    {
        Records.Clear();
        foreach (var record in records)
            Records.Add(record);
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(IsShown));
    }
}

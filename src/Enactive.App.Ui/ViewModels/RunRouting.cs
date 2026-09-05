namespace Enactive.App.Ui.ViewModels;

using Enactive.App.Ui.Mvvm;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// Which model actually served each phase of a run - the worker's, the planner's, the reviewer's.
///
/// <para>It is read from the run's own Routed events rather than from the settings, and the
/// difference matters: the settings say what is bound, this says what was used. A phase that fell
/// back, or a step the router sent to the light model, shows up here and nowhere else.</para>
/// </summary>
internal sealed class RunRouting : ObservableObject
{
    private const string WorkerMarker = "-> model ";
    private const string PhaseMarker = "-> ";

    private string _worker = string.Empty;
    private string _plan = string.Empty;
    private string _review = string.Empty;

    public string Worker { get => _worker; private set => Set(ref _worker, value); }
    public string Plan { get => _plan; private set => Set(ref _plan, value); }
    public string Review { get => _review; private set => Set(ref _review, value); }

    public bool HasWorker => _worker.Length > 0;
    public bool HasPlan => _plan.Length > 0;
    public bool HasReview => _review.Length > 0;
    public bool HasAny => HasWorker || HasPlan || HasReview;

    public void Clear()
    {
        Worker = Plan = Review = string.Empty;
        Raise();
    }

    /// <summary>
    /// Reads one Routed summary. The orchestrator writes three shapes - "Worker 'X' -> model p/m",
    /// "Planner -> p/m" and "Reviewer -> p/m" - and this is the only place that knows them, so the
    /// live run and a replayed one cannot disagree about what they mean.
    /// </summary>
    public void Apply(string summary)
    {
        if (summary.StartsWith("Worker", StringComparison.Ordinal))
            Worker = After(summary, WorkerMarker);
        else if (summary.StartsWith("Planner", StringComparison.Ordinal))
            Plan = After(summary, PhaseMarker);
        else if (summary.StartsWith("Reviewer", StringComparison.Ordinal))
            Review = After(summary, PhaseMarker);
        else
            return;

        Raise();
    }

    public static RunRouting From(RunRecord record)
    {
        var routing = new RunRouting();
        foreach (var e in record.Events)
            if (e.Kind == nameof(EventKind.Routed))
                routing.Apply(e.Summary);
        return routing;
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasWorker));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(HasAny));
    }

    private static string After(string summary, string marker)
    {
        var at = summary.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? string.Empty : summary[(at + marker.Length)..].Trim();
    }
}

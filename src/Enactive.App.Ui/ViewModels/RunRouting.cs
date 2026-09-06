namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>One phase of a run and the model that served it.</summary>
internal sealed record RoutingRow(string Phase, string Model);

/// <summary>
/// Which model actually served each phase of a run - the worker's, the planner's, the reviewer's,
/// and one row per plan STEP.
///
/// <para>It is read from the run's own Routed events rather than from the settings, and the
/// difference matters: the settings say what is bound, this says what was used. A phase that fell
/// back, or a step the router sent to the light model, shows up here and nowhere else.</para>
///
/// <para>The step rows exist because the three binding rows alone could actively mislead: a run
/// bound to a local worker still sends every step it rated "complex" to the expensive model, and the
/// panel would show the local binding it never used. The step rows name the model that ran, and the
/// complexity that sent it there — the "why", without opening the log.</para>
/// </summary>
internal sealed class RunRouting : ObservableObject
{
    private const string WorkerMarker = "-> model ";
    private const string PhaseMarker = "-> ";

    private string _worker = string.Empty;
    private string _plan = string.Empty;
    private string _review = string.Empty;

    /// <summary>Step number -> what served it. Sorted on rebuild; last decision for a step wins.</summary>
    private readonly SortedDictionary<int, string> _steps = new();

    public string Worker { get => _worker; private set => Set(ref _worker, value); }
    public string Plan { get => _plan; private set => Set(ref _plan, value); }
    public string Review { get => _review; private set => Set(ref _review, value); }

    public bool HasAny => Rows.Count > 0;

    /// <summary>
    /// The phases that actually ran, each with the model that served it. A list rather than three
    /// properties in the view, so the phase and its model are written together in one place and a
    /// phase that never happened - no reviewer, no separate planner - simply is not a row.
    /// </summary>
    public ObservableCollection<RoutingRow> Rows { get; } = new();

    public void Clear()
    {
        Worker = Plan = Review = string.Empty;
        _steps.Clear();
        Rebuild();
    }

    /// <summary>
    /// Reads one Routed event.
    ///
    /// <para>The typed payload is the source of truth (see <see cref="WorkEventPayload.RoutePayload"/>):
    /// the summary is display text, and a panel that parses display text turns every reworded message
    /// into a silent behaviour change. The prose fallback below stays only for runs recorded before
    /// the payload existed.</para>
    /// </summary>
    public void Apply(string summary, string? payload = null)
    {
        if (ApplyPayload(payload))
        {
            Rebuild();
            return;
        }

        if (summary.StartsWith("Worker", StringComparison.Ordinal))
            Worker = After(summary, WorkerMarker);
        else if (summary.StartsWith("Planner", StringComparison.Ordinal))
            Plan = After(summary, PhaseMarker);
        else if (summary.StartsWith("Reviewer", StringComparison.Ordinal))
            Review = After(summary, PhaseMarker);
        else
            return;

        Rebuild();
    }

    /// <summary>Returns true when the event carried a routing payload and it was applied.</summary>
    private bool ApplyPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return false;

        // Only the payload matters to the readers, so a stand-in carrying it reads a stored row
        // through exactly the same code as a live one.
        var ev = new WorkEvent(
            Guid.Empty, Guid.Empty, Guid.Empty, DateTimeOffset.MinValue,
            EventKind.Routed, string.Empty, payload);

        if (ev.Route() is not { Length: > 0 } route)
            return false;
        if (ev.ProviderId() is not { Length: > 0 } provider || ev.ModelName() is not { Length: > 0 } model)
            return false;

        var served = $"{provider}/{model}";

        switch (route)
        {
            case "worker":
                Worker = served;
                return true;
            case "plan":
                Plan = served;
                return true;
            case "review":
                Review = served;
                return true;
            case "step" when ev.StepNo() is { } stepNo:
                // The complexity is the REASON this step went where it did, which is the whole
                // question when a local worker binding produced an all-cloud run.
                _steps[stepNo] = ev.RouteComplexity() is { Length: > 0 } complexity
                    ? $"{served} ({complexity})"
                    : served;
                return true;
            default:
                return false;
        }
    }

    public static RunRouting From(RunRecord record)
    {
        var routing = new RunRouting();
        foreach (var e in record.Events)
            if (e.Kind == nameof(EventKind.Routed))
                routing.Apply(e.Summary, e.Payload);
        return routing;
    }

    private void Rebuild()
    {
        Rows.Clear();
        Add("worker", _worker);
        Add("plan", _plan);
        Add("review", _review);
        foreach (var (stepNo, served) in _steps)
            Add($"step {stepNo}", served);
        OnPropertyChanged(nameof(HasAny));

        void Add(string phase, string model)
        {
            if (model.Length > 0)
                Rows.Add(new RoutingRow(phase, model));
        }
    }

    private static string After(string summary, string marker)
    {
        var at = summary.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? string.Empty : summary[(at + marker.Length)..].Trim();
    }
}

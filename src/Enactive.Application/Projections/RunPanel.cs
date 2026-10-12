namespace Enactive.Application.Projections;

using Enactive.Core.Chat;
using Enactive.Core.Events;

/// <summary>
/// The figures a run's status column shows - which model served each phase, the tokens and where they ran, the
/// artifacts - folded from the run's events in ONE place.
///
/// <para>The desktop window dispatched those events in a switch of its own, so a second host (the server's pages) would
/// have had to copy the switch to show the same figures, and the two would have drifted at the first new event kind. The
/// rules are in <see cref="RunRouting"/>, <see cref="ModelWorkSplit"/> and <see cref="ModelPerformance"/>; this is only the
/// dispatch.</para>
///
/// <para>Internal because the projections it exposes are: a host reaches it through InternalsVisibleTo.</para>
/// </summary>
internal sealed class RunPanel(Func<string?, ModelReach> reachOf)
{
    public RunRouting Routing { get; } = new();
    public ModelWorkSplit Work { get; } = new();
    public ModelPerformance Performance { get; } = new();

    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    /// <summary>
    /// Files the run produced, one per path, case-insensitive. Matches the window's artifact cards (one card per file,
    /// MainWindow.AddArtifact): counting every event made a step that wrote report.md twice read 2 here and 1 there.
    /// </summary>
    public int Artifacts => _artifacts.Count;

    /// <summary>
    /// The files counted in <see cref="Artifacts"/>, in the order first written - a host's Artifacts tab. With the count
    /// alone a second host could show the tile but not the list behind it, and would have folded the events again for it.
    /// </summary>
    public IReadOnlyList<string> ArtifactPaths => _artifactPaths;

    private readonly HashSet<string> _artifacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _artifactPaths = new();

    /// <summary>Folds one event in. True when a figure changed, so a host redraws only for events that matter.</summary>
    public bool Apply(WorkEvent ev)
    {
        switch (ev.Kind)
        {
            case EventKind.Routed:
                // RunRouting ignores a summary that names no phase and carries no payload; "changed" is read off its
                // rows, not assumed, or every such event would redraw a panel that looks the same.
                var before = Signature();
                Routing.Apply(ev.Summary, ev.PayloadJson);
                return Signature() != before;

            case EventKind.UsageReported when ev.Usage() is { } used:
                var provider = ev.ProviderId();
                PromptTokens += used.In;
                CompletionTokens += used.Out;
                Work.Add(provider, reachOf(provider), used.In, used.Out);
                return true;

            case EventKind.ArtifactProduced:
                // The path the window uses, with its fallback for a run recorded before the payload named one:
                // the text after the first ": " in the summary, else the summary itself.
                var path = ev.ArtifactPath()
                           ?? (ev.Summary.Contains(": ", StringComparison.Ordinal)
                               ? ev.Summary[(ev.Summary.IndexOf(": ", StringComparison.Ordinal) + 2)..]
                               : ev.Summary);
                // A repeated write to a known path says nothing new.
                if (!_artifacts.Add(path))
                    return false;
                _artifactPaths.Add(path);
                return true;

            default:
                return false;
        }
    }

    /// <summary>A completed provider call, for the per-model speed figures.</summary>
    public void Add(ModelCallMetrics m) => Performance.Add(m);

    private string Signature() => string.Join('\n', Routing.Rows.Select(r => $"{r.Phase}={r.Model}"));
}

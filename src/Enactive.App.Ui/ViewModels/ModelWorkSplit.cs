namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// How much of a run was done on this machine and how much was bought from a cloud provider.
///
/// The token tile answered "how much work was there" and nothing about WHO did it — which is the one
/// question the light/heavy routing exists to settle. A plan that routes trivial steps to a local
/// model is worth nothing if, in practice, every step still ends up at Claude, and until now there
/// was no way to see that short of reading the log.
///
/// Both kinds of provider report tokens (Ollama returns prompt_eval_count / eval_count), so the
/// comparison is like for like. Calls are counted alongside because the two say different things: a
/// single cloud turn carrying a large context can cost more than ten local ones.
/// </summary>
internal sealed class ModelWorkSplit : ObservableObject
{
    private readonly Dictionary<string, ProviderWork> _byProvider = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a provider runs. Unknown is its own answer, never quietly folded into either.</summary>
    public enum Reach { Local, Cloud, Unknown }

    private sealed record ProviderWork(Reach Where)
    {
        public int Prompt { get; set; }
        public int Completion { get; set; }
        public int Calls { get; set; }
    }

    /// <summary>One line per reach, in a fixed order so the panel does not reshuffle mid-run.</summary>
    public ObservableCollection<ModelWorkRow> Rows { get; } = new();

    public bool HasAny => Rows.Count > 0;

    /// <summary>
    /// Records one turn. <paramref name="where"/> is decided by the caller, which is the only place
    /// that knows a provider's address: the engine records the provider id and stops there.
    /// </summary>
    public void Add(string? providerId, Reach where, int promptTokens, int completionTokens)
    {
        var key = string.IsNullOrWhiteSpace(providerId) ? "(unknown)" : providerId;

        if (!_byProvider.TryGetValue(key, out var work))
            _byProvider[key] = work = new ProviderWork(where);

        work.Prompt += promptTokens;
        work.Completion += completionTokens;
        work.Calls++;

        Rebuild();
    }

    /// <summary>
    /// Replays a stored run. Reads the typed payload, never the summary text: a past run has to be
    /// readable the same way a live one is, or the answer depends on wording.
    /// </summary>
    public static ModelWorkSplit From(
        Enactive.Core.History.RunRecord record, Func<string?, Reach> classify)
    {
        var split = new ModelWorkSplit();

        foreach (var stored in record.Events)
        {
            if (stored.Kind != nameof(Enactive.Core.Events.EventKind.UsageReported))
                continue;

            // The extension methods want a WorkEvent; only the payload matters to them, so a
            // stand-in carrying it reads a stored row through exactly the same code as a live one.
            var ev = new Enactive.Core.Events.WorkEvent(
                Guid.Empty, record.TaskId, record.RunId, stored.At,
                Enactive.Core.Events.EventKind.UsageReported, stored.Summary, stored.Payload);

            if (ev.Usage() is not { } used)
                continue;

            var provider = ev.ProviderId();
            split.Add(provider, classify(provider), used.In, used.Out);
        }

        return split;
    }

    public void Clear()
    {
        _byProvider.Clear();
        Rows.Clear();
        OnPropertyChanged(nameof(HasAny));
    }

    private void Rebuild()
    {
        var total = _byProvider.Values.Sum(w => (long)w.Prompt + w.Completion);

        var groups = new[] { Reach.Local, Reach.Cloud, Reach.Unknown }
            .Select(reach => (Reach: reach, Work: _byProvider.Values.Where(w => w.Where == reach).ToArray()))
            .Where(g => g.Work.Length > 0)
            .ToArray();

        Rows.Clear();
        foreach (var (reach, work) in groups)
        {
            var tokens = work.Sum(w => (long)w.Prompt + w.Completion);
            var calls = work.Sum(w => w.Calls);

            Rows.Add(new ModelWorkRow(
                Label: reach switch
                {
                    Reach.Local => "local",
                    Reach.Cloud => "cloud",
                    _ => "unknown"
                },
                // Percent of TOKENS, not of calls: it is the closer proxy for how much thinking each
                // side actually did. The call count sits next to it for the case where they disagree.
                Detail: total > 0
                    ? $"{MainWindowViewModel.Compact((int)Math.Min(int.MaxValue, tokens))} · "
                      + $"{calls} call{(calls == 1 ? "" : "s")} · {tokens * 100 / total}%"
                    : $"{calls} call{(calls == 1 ? "" : "s")}",
                Edge: reach switch
                {
                    Reach.Local => Brand.Success,
                    Reach.Cloud => Brand.Info,
                    _ => Brand.TextFaint
                }));
        }

        OnPropertyChanged(nameof(HasAny));
    }
}

/// <summary>One line of the split — the same green/blue local/remote language the provider list uses.</summary>
internal sealed record ModelWorkRow(string Label, string Detail, IBrush Edge);

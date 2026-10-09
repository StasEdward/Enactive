namespace Enactive.App.Ui.ViewModels;
using System.Collections.ObjectModel;
using Enactive.Core.Chat;

/// <summary>
/// One run's totals, grouped by provider AND model. Only completed requests contribute.
///
/// <para>The run's, not the session's (2026-10-09): the panel summed every call since the window opened - background
/// runs, scheduled ones, every run before - and after a run nobody could read that run's speed off it. Each call says
/// which run it was for (ModelCallMetrics.RunId); the panel shows the run the window shows live, and starts again with the
/// next one. A call can arrive before the window has learnt its run's id from the run's first event, so the last runs'
/// calls are kept, and shown when their run is.</para>
/// </summary>
internal sealed class ModelPerformance
{
    /// <summary>How many runs' calls are kept for a run the window has not shown yet - a few, not the session.</summary>
    internal const int RunsKept = 8;

    public ObservableCollection<ModelPerformanceRow> Rows { get; } = new();
    private readonly Dictionary<(string, string), Totals> _totals = new();
    private readonly Dictionary<Guid, List<ModelCallMetrics>> _byRun = new();
    private readonly Queue<Guid> _runs = new();
    private Guid? _shown;

    /// <summary>The run whose calls are shown now, or null before any.</summary>
    public Guid? ShownRun => _shown;

    /// <summary>A completed call. Shown when it is the shown run's; kept for a while when it is another run's; a call made
    /// outside any run is nobody's to show.</summary>
    public void Add(ModelCallMetrics m)
    {
        if (m.RunId is not { } run) return;
        if (!_byRun.TryGetValue(run, out var calls))
        {
            _byRun[run] = calls = [];
            _runs.Enqueue(run);
            while (_runs.Count > RunsKept && _runs.Dequeue() is var gone)
                if (gone != _shown) _byRun.Remove(gone);
        }
        calls.Add(m);
        if (run == _shown) Apply(m);
    }

    /// <summary>Shows one run's calls, from nothing: what came before belongs to other runs.</summary>
    public void ShowRun(Guid run)
    {
        if (_shown == run) return;
        _shown = run;
        Rows.Clear();
        _totals.Clear();
        foreach (var m in _byRun.GetValueOrDefault(run) ?? [])
            Apply(m);
    }

    private void Apply(ModelCallMetrics m)
    {
        var key = (m.ProviderId, m.Model);
        if (!_totals.TryGetValue(key, out var t)) _totals[key] = t = new();
        t.Calls++; t.Request += m.RequestSeconds;
        if (m.PromptTokens is { } p) { t.Input += p; t.HasInput = true; }
        if (m.CompletionTokens is { } o) { t.Output += o; t.HasOutput = true; }
        // Match numerator and denominator: calls without token counts cannot contribute
        // only their duration, and zero/invalid durations cannot provide a rate.
        if (m.CompletionTokens is >= 0 and var measuredOutput
            && double.IsFinite(m.RequestSeconds) && m.RequestSeconds > 0)
        { t.RequestOutput += measuredOutput; t.MeasuredRequest += m.RequestSeconds; }
        if (m.CachedPromptTokens is { } c && m.PromptTokens is { } cp)
        { t.Cached += c; t.CacheInput += cp; }
        if (m.FirstDeltaSeconds is { } f) { t.First += f; t.FirstCalls++; }
        if (m.Timings?.PromptSeconds is { } ps) { t.Prompt += ps; t.PromptCalls++; }
        if (m.Timings?.LoadSeconds is { } ls) { t.Load += ls; t.LoadCalls++; }
        if (m.Timings?.GenerationSeconds is > 0 and var gs && (m.Timings.GenerationTokens ?? m.CompletionTokens) is { } n)
        { t.Generation += gs; t.Generated += n; t.TimedCalls++; }
        var row = new ModelPerformanceRow(m.ProviderId, m.Model,
            t.Generation > 0 ? $"{t.Generated / t.Generation:N1} T/s"
                : t.MeasuredRequest > 0 ? $"≈ {t.RequestOutput / t.MeasuredRequest:N1} T/s" : "—",
            t.Generation > 0 || t.MeasuredRequest == 0 ? "Generation avg" : "Whole request avg",
            $"Prompt Σ  {Seconds(t.PromptCalls > 0 ? t.Prompt : null)}   ·   Decode Σ  {Seconds(t.TimedCalls > 0 ? t.Generation : null)}",
            $"Tokens  {(t.HasInput ? t.Input.ToString("N0") : "—")} in / {(t.HasOutput ? t.Output.ToString("N0") : "—")} out",
            $"Cache  {(t.CacheInput > 0 ? (100.0 * t.Cached / t.CacheInput).ToString("N1") + "%" : "—")}   ·   Load Σ  {Seconds(t.LoadCalls > 0 ? t.Load : null)}",
            $"First delta avg  {Seconds(t.FirstCalls > 0 ? t.First / t.FirstCalls : null)}",
            $"Request avg  {Seconds(t.Request / t.Calls)}   ·   {t.Calls} calls",
            $"Server timings: prompt {t.PromptCalls}/{t.Calls}, decode {t.TimedCalls}/{t.Calls}. " +
            "Generation speed = total timed output tokens / total server decode seconds. " +
            "Without server decode timing, ≈ T/s = total reported output tokens / total duration of those requests, " +
            "including queue, prompt processing and network time; this is not server generation speed. " +
            "Prompt Σ (prompt_seconds_total) sums reported prompt time for completed calls this session; " +
            "not a server-wide counter. First delta includes queue, transport and prefill. Missing values are unknown.");
        var old = Rows.FirstOrDefault(x => x.Provider == m.ProviderId && x.Model == m.Model);
        if (old is null) Rows.Add(row); else Rows[Rows.IndexOf(old)] = row;
    }
    private static string Seconds(double? n) => n is { } v ? $"{v:N2} s" : "—";
    private sealed class Totals
    {
        public int Calls, FirstCalls, PromptCalls, TimedCalls, LoadCalls;
        public long Input, Output, Cached, CacheInput, Generated, RequestOutput;
        public bool HasInput, HasOutput;
        public double Request, First, Prompt, Generation, Load, MeasuredRequest;
    }
}
internal sealed record ModelPerformanceRow(string Provider, string Model, string Speed, string SpeedLabel,
    string Times, string Tokens, string Cache, string First, string Requests, string Explanation);

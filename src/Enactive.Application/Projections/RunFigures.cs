namespace Enactive.Application.Projections;

using System.Globalization;
using Enactive.Core.History;

/// <summary>
/// The words a run's figures are said in, wherever a run is shown: the TOKENS tile and its tooltip, the steps line, the
/// Artifacts tab's label, the Log tab's header. The window wrote each inside itself (MainWindowViewModel, MainWindow,
/// RunLogViewModel) and the past run's head (RunDigest) a second time; a third copy on the service would have been the
/// one that drifted. Written the same in every culture: the service says them in the server's, not the reader's.
/// </summary>
internal static class RunFigures
{
    /// <summary>
    /// A live run's counts so far as usage, or null when nothing was reported: a provider that does not count reports
    /// nothing, and the counts stay at zero - which the tile must not show as "0 tokens".
    /// </summary>
    public static RunUsage? Reported(int promptTokens, int completionTokens)
        => promptTokens + completionTokens == 0 ? null : new RunUsage(promptTokens, completionTokens);

    /// <summary>The TOKENS tile: the total, or an em dash where nothing was reported - a different fact from zero.</summary>
    public static string Tokens(RunUsage? usage) => usage is { } u ? TokenCounts.Compact(u.Total) : "—";

    /// <summary>The tile's tooltip: in and out, or that nothing was reported.</summary>
    public static string TokensDetail(RunUsage? usage)
        => usage is { } u ? $"{TokenCounts.Compact(u.PromptTokens)} in · {TokenCounts.Compact(u.CompletionTokens)} out" : "nothing reported";

    /// <summary>"2 / 5 steps"; an em dash before there is a plan - "0 / 0 steps" would report on work not decided on.</summary>
    public static string Steps(int done, int total)
        => total > 0 ? string.Create(CultureInfo.InvariantCulture, $"{done} / {total} steps") : "—";

    /// <summary>The tab carries its own count, so an empty run does not advertise "Artifacts (0)".</summary>
    public static string ArtifactsTab(int count)
        => count == 0 ? "Artifacts" : string.Create(CultureInfo.InvariantCulture, $"Artifacts ({count})");

    /// <summary>The Log tab's header: the run it is kept to, by the first six of its id - or none yet.</summary>
    public static string LogHeader(Guid? runId) => runId is { } id ? "Filtered to run " + id.ToString("N")[..6] : "No run yet.";
}

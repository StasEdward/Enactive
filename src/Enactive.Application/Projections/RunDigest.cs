namespace Enactive.Application.Projections;

using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Templates;

/// <summary>
/// A finished run in a few lines: what it was called and asked, when and on what, how it went, what it cost, what it
/// was allowed to do, which attempt at its task it was - the head of a past run's page.
///
/// <para>Worked out in the window's past-run view model until the service needed the same head on its run page; two
/// copies of "a run with no usage says nothing reported, not zero" are two places for it to stop being true.</para>
/// </summary>
internal sealed record RunDigest(
    string Title,
    string Subtitle,
    string Meta,
    string Status,
    string AttemptLabel,
    bool CanRetry,
    string TokensText,
    string TokensDetail,
    int ToolCalls,
    string ElapsedText,
    string StepsText,
    bool HasSettings,
    string AutonomyText,
    int? Autonomy,
    string WorkerText,
    string StagingText,
    string NoStepsReason,
    IReadOnlyList<string> Artifacts)
{
    /// <param name="attempts">Every attempt at the run's task, newest first; null when only the run itself is known.</param>
    public static RunDigest Of(RunRecord record, IReadOnlyList<IRunHeader>? attempts = null)
    {
        var title = RunTitle.For(record);

        var siblings = attempts is { Count: > 0 } ? attempts : [record];
        var attemptLabel = siblings.Count > 1
            ? $"attempt {RunHistory.AttemptNumber(siblings, record.RunId)} of {siblings.Count}"
            : string.Empty;

        var spec = ResolvedTaskSpec.Parse(record.Spec);
        var request = RunHistory.RequestOf(record);
        // Only when it says something the title does not: a typed request IS the title, and repeating it is noise.
        var asked = RunTitle.OneLine(request, 160);
        var subtitle = asked.StartsWith(title, StringComparison.Ordinal) ? string.Empty : asked;

        var model = string.IsNullOrWhiteSpace(record.Model) ? "unknown model" : record.Model;
        var cards = RunFeed.Replay(record).Cards;
        var done = cards.Count(c => c.Status is FeedCardStatus.Done or FeedCardStatus.Skipped);
        var settings = record.Settings;

        return new RunDigest(
            Title: title,
            Subtitle: subtitle,
            Meta: $"{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {model} · run {record.RunId:N}",
            Status: record.Status,
            AttemptLabel: attemptLabel,
            // Retry re-runs this specification as it was, or - for a typed run - the request, recorded as a value.
            CanRetry: spec is not null || !string.IsNullOrWhiteSpace(request),
            // Null usage: the run predates the counting, or the provider never reported - not zero, and not shown as it.
            TokensText: RunFigures.Tokens(record.Usage),
            TokensDetail: RunFigures.TokensDetail(record.Usage),
            ToolCalls: record.Events.Count(e => e.Kind == nameof(EventKind.ToolInvoked)),
            ElapsedText: Duration(record.FinishedAt - record.StartedAt),
            StepsText: RunFigures.Steps(done, cards.Count),
            // What it was ALLOWED to do, as recorded. A run from before this was kept says so, rather than showing
            // today's level and passing it off as history.
            HasSettings: settings is not null,
            AutonomyText: settings?.AutonomyName ?? string.Empty,
            Autonomy: settings?.Autonomy,
            WorkerText: settings is null ? string.Empty : string.IsNullOrWhiteSpace(settings.Worker) ? "default worker" : settings.Worker,
            StagingText: settings is null ? string.Empty : settings.Staged ? "changes were staged for review" : "changes were applied directly",
            NoStepsReason: RunReport.WhyNoSteps(record),
            Artifacts: record.Artifacts.Select(PathOf).ToList());
    }

    public static string Duration(TimeSpan span)
    {
        var seconds = Math.Max(0, span.TotalSeconds);
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }

    /// <summary>
    /// A recorded artifact is the path. Older records kept the whole sentence - "FileSet: path" - and only a KNOWN
    /// artifact kind is stripped off the front, because splitting on the first ": " would eat the start of any path
    /// that happens to contain one.
    /// </summary>
    public static string PathOf(string artifact)
    {
        var colon = artifact.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            return artifact;
        return Enum.TryParse<ArtifactKind>(artifact[..colon], ignoreCase: true, out _) ? artifact[(colon + 2)..] : artifact;
    }
}

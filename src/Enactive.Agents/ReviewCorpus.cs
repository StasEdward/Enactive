namespace Enactive.Agents;

using Enactive.Core.Execution;

/// <summary>
/// A combined review that validation refused, kept with exactly what it was checked against, so the
/// refusal can be replayed offline in a second instead of rediscovered by a four-minute run.
///
/// <para><b>Why the inputs and not the log.</b> The log holds the reviewer's answer, and the answer
/// alone decides only one kind of refusal - a required field missing. The others seen on 2026-09-27
/// and 28 depend on what the reviewer was SHOWN: "missing requirement-map assessment" on the
/// request's obligations, "quote is absent from the visible source" on the displayed evidence and
/// files. Those exist only in memory at review time and reach the log rendered, not as data. So they
/// are written down here, at the moment they are used.</para>
///
/// <para>Only refusals are kept. They are the blocker, they are rare - fifteen across those two
/// days - and a file per accepted review would grow the workspace for nothing anybody replays.</para>
/// </summary>
internal sealed record ReviewCorpusCase(
    DateTimeOffset RecordedAt,
    string Model,
    string Answer,
    RequestObligations Obligations,
    ReviewCorpusEvidence Evidence,
    IReadOnlyList<ReviewCorpusSource> Sources,
    IReadOnlyList<string> Errors);

/// <summary>An evidence view, as data. Restoring one is a test's job; see Enactive.Core.csproj.</summary>
internal sealed record ReviewCorpusEvidence(
    string Text,
    IReadOnlyList<ExecutedAction> Actions,
    IReadOnlyList<int> VisibleActionIds,
    bool OutputsTruncated,
    bool ArgumentsTruncated,
    bool HasPriorTranscript);

internal sealed record ReviewCorpusSource(string Id, string Kind, string Label, string VisibleText);

internal static class ReviewCorpus
{
    /// <summary>Under the workspace, beside the engine's other own data.</summary>
    internal const string Folder = ".enactive/review-corpus";

    internal const int Keep = RefusalCorpus.Keep;

    /// <summary>
    /// The structural validators, in the order the combined review applies them. ONE definition,
    /// used by the review itself and by the replay, so the replay cannot drift from what production
    /// checks: an instrument that measures a different pipeline than the one that ran would report
    /// numbers about nothing.
    /// </summary>
    internal static List<string> Validate(string answer, RequestObligations obligations, EvidenceView evidence,
        ReviewSources sources)
    {
        var errors = CombinedReviewValidation.Errors(answer, obligations, evidence).ToList();
        errors.AddRange(ReportCommandAudit.Errors(answer, evidence, sources, obligations));
        if (errors.Count == 0)
            errors.AddRange(SemanticReviewAudit.Errors(answer, evidence, sources, obligations));
        if (errors.Count == 0)
            errors.AddRange(ReviewRepairContract.Errors(answer, sources, obligations));
        return errors;
    }

    internal static ReviewCorpusCase Capture(string model, string answer, RequestObligations obligations,
        EvidenceView evidence, ReviewSources sources, IReadOnlyList<string> errors)
    {
        var view = evidence.ReplaySnapshot();
        return new(DateTimeOffset.UtcNow, model, answer, obligations,
            new(view.Text, view.Actions, view.VisibleActionIds,
                view.OutputsTruncated, view.ArgumentsTruncated, view.HasPriorTranscript),
            sources.All.Select(s => new ReviewCorpusSource(s.Id, s.Kind, s.Label, s.VisibleText)).ToArray(),
            errors.ToArray());
    }

    /// <summary>Best effort; see <see cref="RefusalCorpus.Record{T}"/>.</summary>
    internal static void Record(string? workspaceRoot, ReviewCorpusCase refusal)
        => RefusalCorpus.Record(workspaceRoot, Folder, refusal.RecordedAt, refusal);

    internal static ReviewCorpusCase Read(string path) => RefusalCorpus.Read<ReviewCorpusCase>(path);

    /// <summary>The sources, rebuilt by the same three calls that built them during the review.</summary>
    internal static ReviewSources RestoreSources(IReadOnlyList<ReviewCorpusSource> recorded)
    {
        if (recorded.Count == 0 || recorded[0].Kind != "worker-report")
            throw new InvalidDataException("A recorded review always begins with the worker's report.");
        var sources = new ReviewSources(recorded[0].VisibleText);
        foreach (var source in recorded.Skip(1))
        {
            if (source.Kind == "execution-evidence") sources.AddEvidence(source.VisibleText);
            else if (source.Kind == "saved-file") sources.AddFile(source.Label, source.VisibleText);
            else throw new InvalidDataException("Unknown review source kind: " + source.Kind);
        }
        return sources;
    }
}

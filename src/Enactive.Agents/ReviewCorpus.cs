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
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// What the model actually sent, before the engine took a JSON object out of it and decoded its
    /// references. <see cref="Answer"/> is what the validators saw, and on 2026-09-28 11:01 the two
    /// were different things: the extraction handed on a fragment of a broken answer, and a corpus
    /// holding only the fragment could not show that. For reading, not replayed: turning it into
    /// <see cref="Answer"/> needs the review's reference table. Null in cases recorded before it.
    /// </summary>
    public string? RawAnswer { get; init; }
}

/// <summary>What refuses a review answer, and what is only a gap in its repairs. See <see cref="ReviewCorpus.Check"/>.</summary>
internal sealed record ReviewValidation(IReadOnlyList<string> Refusals, IReadOnlyList<string> RepairGaps);

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
    /// What the engine does to a decoded answer before any validator sees it: deterministic
    /// bookkeeping, never a verdict (see <see cref="ReviewScopeNormalization"/>). Part of the one
    /// definition, and recorded answers are kept from BEFORE it, so a change here is measured by the
    /// corpus like a change to any validator.
    /// </summary>
    internal static string Prepare(string decoded, RequestObligations obligations)
        => ReviewScopeNormalization.Apply(decoded, obligations);

    /// <summary>
    /// The structural validators, in the order the combined review applies them. ONE definition,
    /// used by the review itself and by the replay, so the replay cannot drift from what production
    /// checks: an instrument that measures a different pipeline than the one that ran would report
    /// numbers about nothing.
    /// </summary>
    internal static List<string> Validate(string answer, RequestObligations obligations, EvidenceView evidence,
        ReviewSources sources)
        => Check(answer, obligations, evidence, sources).Refusals.ToList();

    /// <summary>
    /// What refuses the answer, and what is only missing from its list of repairs.
    ///
    /// <para><b>A fail is not refused for its bookkeeping.</b> On 2026-09-28 the final review found
    /// a source file changed against the request's explicit ban and 107 of 111 tests failing, said
    /// FAIL, twice - and was refused both times over which repair named which finding. The run
    /// ended "done, not verified", and the violation it had found was nowhere in the outcome. A
    /// strict repair contract exists so that a worker is not sent to fix something nobody
    /// identified; it was never meant to decide whether a failure the reviewer DID identify counts.
    /// So when everything else holds and the verdict is fail, the repair contract's complaints are
    /// gaps, not refusals: the verdict stands, the repairs that are sound go to the worker, and the
    /// next review sees whatever they did not fix. Failing is the safe direction - it cannot let
    /// bad work through. A pass is held to every rule, as before.</para>
    /// </summary>
    internal static ReviewValidation Check(string answer, RequestObligations obligations, EvidenceView evidence,
        ReviewSources sources)
    {
        var errors = CombinedReviewValidation.Errors(answer, obligations, evidence).ToList();
        errors.AddRange(ReportCommandAudit.Errors(answer, evidence, sources, obligations));
        if (errors.Count == 0)
            errors.AddRange(SemanticReviewAudit.Errors(answer, evidence, sources, obligations));
        if (errors.Count > 0)
            return new(errors, []);
        var repairs = ReviewRepairContract.Errors(answer, sources, obligations);
        return repairs.Count > 0 && ReviewRepairContract.FindsFailure(answer)
            ? new([], repairs)
            : new(repairs, []);
    }

    internal static ReviewCorpusCase Capture(string model, string answer, RequestObligations obligations,
        EvidenceView evidence, ReviewSources sources, IReadOnlyList<string> errors, string? rawAnswer = null)
    {
        var view = evidence.ReplaySnapshot();
        return new(DateTimeOffset.UtcNow, model, answer, obligations,
            new(view.Text, view.Actions, view.VisibleActionIds,
                view.OutputsTruncated, view.ArgumentsTruncated, view.HasPriorTranscript),
            sources.All.Select(s => new ReviewCorpusSource(s.Id, s.Kind, s.Label, s.VisibleText)).ToArray(),
            errors.ToArray()) { RawAnswer = rawAnswer };
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

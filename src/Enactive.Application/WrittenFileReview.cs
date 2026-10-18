namespace Enactive.Application;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Workspace;

/// <summary>
/// A file a run wrote, as a person reviews it: what the run changed in it, as a diff, when there is a trustworthy "how it
/// was before" to compare with - and otherwise the file as it is now, with why there is no diff.
///
/// <para><b>Where "before" comes from.</b> The run's own record (<see cref="DiskArtifactStore.BeforeRunOfAsync"/>): every
/// run writes through the store (RunComposer, for the window and the service alike), and before the store overwrites
/// or deletes a file it copies the bytes it displaces beside the run's journal, under the run's id - the copy putting
/// the run back restores. That is a base the engine itself trusts. Not git: the run's start tree is an object nothing
/// references (WorkspaceChanges), which <c>git gc</c> may prune, and HEAD says nothing of a file that was already
/// changed when the run began.</para>
///
/// <para><b>When there is none, no diff.</b> A diff against a wrong base reads as the run's work and is not: so a file
/// changed after the run (its changes could not be told from the run's), one the record holds no write to (a command
/// changed it, or the run is older than the copies kept), and one whose earlier bytes are lost or not text are shown as
/// they are now, with the reason said.</para>
/// </summary>
public static class WrittenFileReview
{
    /// <summary>What a review of a file shows: the diff and what it is against, or the text and why it is not a diff.</summary>
    public sealed record Review(string? Text, string? Problem, string? Diff, string Note);

    // What the diff says when the files are too long to compare line by line - here, the whole file is one switch away.
    private const string TooLargeAdvice = "Switch to the whole file to read it.";

    /// <summary>
    /// The review of <paramref name="relative"/> as <paramref name="runId"/> left it in <paramref name="root"/>. The
    /// caller decides who may read the workspace; this only reads what the run kept.
    /// </summary>
    public static async Task<Review> OfAsync(string root, Guid runId, string relative, CancellationToken ct)
    {
        var (text, problem) = WorkspaceFiles.Read(root, relative);
        if (text is null)
            return new Review(null, problem, null, "");
        if (runId == Guid.Empty)
            return new Review(text, null, null, NoRecord);

        var (before, asLeft) = await DiskArtifactStore.BeforeRunOfAsync(WorkspaceInfo.For(root), runId, relative, ct);
        switch (before.State)
        {
            case BeforeRunState.Absent when asLeft:
                return Compared(text, null, relative, "A new file: the run made it, so all of it is the run's.");
            case BeforeRunState.Kept when asLeft:
                var (was, wasProblem) = WorkspaceFiles.Decode(before.Content!);
                return was is null
                    ? new Review(text, null, null, $"How it was before the run is kept, but not shown: {Lower(wasProblem!)} Shown as it is now.")
                    : Compared(text, was, relative, "What the run changed, against the file as the run found it.");
            case BeforeRunState.Absent or BeforeRunState.Kept:
                return new Review(text, null, null,
                    "The file was changed after the run, so the run's changes cannot be told apart from what came later. Shown as it is now.");
            case BeforeRunState.Lost:
                return new Review(text, null, null, "How the file was before the run was not kept. Shown as it is now.");
            default:
                return new Review(text, null, null, NoRecord);
        }
    }

    /// <summary>
    /// The diff, or - when the files are too long to compare line by line (LineDiff refuses past its limit, and says so
    /// ending with our advice) - the whole file with that said: a page of "12000 lines -> 12010 lines" in place of a diff
    /// is no review, and the file itself is.
    /// </summary>
    private static Review Compared(string text, string? was, string relative, string note)
    {
        var diff = LineDiff.Unified(was, text, relative, TooLargeAdvice);
        return diff.TrimEnd().EndsWith(TooLargeAdvice, StringComparison.Ordinal)
            ? new Review(text, null, null, "Too long to compare line by line. Shown as it is now.")
            : new Review(text, null, diff, note);
    }

    private const string NoRecord =
        "No copy of how this file was before the run is kept - a command changed it, or the run is older than the copies " +
        "kept - so there is nothing to compare with. Shown as it is now.";

    private static string Lower(string sentence) => sentence.Length > 0 ? char.ToLowerInvariant(sentence[0]) + sentence[1..] : sentence;
}

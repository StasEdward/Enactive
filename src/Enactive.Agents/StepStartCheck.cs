namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Builds;
using Enactive.Core.Templates;

/// <summary>
/// Whether a blocking step check that failed after a step had failed on the workspace as the step found it - and, where
/// what it printed can be compared, whether the step added to that failure.
///
/// <para><b>Why.</b> Bench build-error, 2026-10-18: the workspace did not build before the run (a typo in a file the
/// request said to leave alone), and a manifest raised the implicit build to block each step. Step 1 added the method it
/// was asked for, correctly; the raised build failed it on the old error alone, judged by its exit code; told its step
/// had failed, the worker went after the old error in the file it was not to touch - refused by the write boundary, then
/// done anyway through a command. The engine's own "No new build errors", judged against the run's baseline, passed the
/// very same state.</para>
///
/// <para><b>What is decided, and why this way.</b> Of the two ways the bench left open - know the check's result on the
/// state the step started from and judge "failed before and after" as not the step's (a), or keep it blocking and only
/// tell the worker the failure was there before (b) - this is (a) where the check's output can be compared and the start
/// state's result is known, because only a comparison can tell an old failure from a new one on top of it: a build is
/// compared by its errors (BuildRegression.Compare), tests by which of them fail, a plain command by printing exactly
/// what it printed before. A step that added to the failure still blocks, told what is new. Where the start state's
/// result is known and failed but the output cannot be compared, it is (b) made honest: the step is not sent back for a
/// failure it may not have made, and is never confirmed by a check that said nothing about it (done, not confirmed) -
/// neither the bench's chase after an old error nor a step that made the failure worse passing silently. Where nothing
/// says how the check stood when the step began, today's behaviour stands, and the result says it was not known.</para>
///
/// <para><b>What "passes that check" means here.</b> The step passes the check in what decides the step - it is not sent
/// back, and its review is asked - but the check's own result stays FAILED with the note first in its detail: the build
/// does fail, and a person reading the check is owed that. So a step card can show a red check beside a step that
/// succeeded, with the note saying the failure was there before it. Nothing else reads it: a step-level check's result
/// decides the step only, never the run (the run's outcome reads the steps' outcomes, and the checks at waves and at the
/// run's end).</para>
/// </summary>
internal static class StepStartCheck
{
    /// <summary>
    /// How many earlier results of one check a run keeps. The one a step needs is the newest from before it began; the
    /// step's own attempts add one each (as many as its review and gate budgets allow), and those never match its start,
    /// since each changed what the check covers. Past this the oldest are the least likely to be the state any later step
    /// starts from, and comparing against each costs a measurement; a step with more attempts than this that fails the
    /// check again is not known, and keeps today's behaviour.
    /// </summary>
    internal const int KeptPerCheck = 8;

    /// <summary>One command a check ran as: an ecosystem's build or test of one target, or the check's own command.</summary>
    internal sealed record Part(IEcosystem? Ecosystem, string? Target, BaselineKind Kind, CriterionResult Result);

    /// <summary>A check's result at a step's gate, and the workspace it ran on - what a later step can start from.</summary>
    internal sealed record Known(string CheckId, WorkspaceSnapshot At, IReadOnlyList<Part> Parts);

    /// <summary>What a part was before the step: an ecosystem's baseline before the run, or the part's earlier result.</summary>
    internal sealed record Before(BuildBaseline? Baseline, CriterionResult? Result);

    internal enum Standing
    {
        /// <summary>It failed before the step too, and nothing in its failure is new: not the step's.</summary>
        Theirs,

        /// <summary>It passed before the step, or the step added to its failure: the step's, and it blocks.</summary>
        Ours,

        /// <summary>It failed before the step too, and what it printed cannot be compared: it says nothing about the step.</summary>
        Inconclusive,

        /// <summary>How it stood when the step began is not known: today's behaviour, said so.</summary>
        NotKnown
    }

    /// <param name="FailedBefore">Some part of it is known to have failed before the step - so there is an old failure the
    /// step is not to chase. False when it passed before, or how it stood is not known: a step sent back for a check that
    /// passed before is not told of an old failure there was none of.</param>
    internal sealed record Judgement(Standing Standing, string Note, bool FailedBefore = false);

    public static Judgement NotKnown(string why) => new(Standing.NotKnown, why);

    /// <summary>What a part that passed before the step says - the one judgement of a known start with no old failure in it.</summary>
    private const string PassedBefore = "it passed before this step";

    /// <summary>
    /// The check, as it ran after the step (<paramref name="after"/>), against how each of its parts stood before it. A
    /// part that passed now decides nothing; of those that failed, one that is the step's makes the whole check the step's,
    /// then one not known keeps today's behaviour, then one that cannot be compared says nothing about the step.
    /// </summary>
    public static Judgement Judge(IReadOnlyList<Part> after, Func<Part, Before?> before, string root)
    {
        var judged = after.Where(p => p.Result.Outcome == CriterionOutcome.Failed).Select(p => Of(p, before(p), root)).ToArray();
        if (judged.Length == 0) return NotKnown("no part of it failed that could be compared");
        foreach (var standing in new[] { Standing.Ours, Standing.NotKnown, Standing.Inconclusive, Standing.Theirs })
            if (judged.Where(j => j.Standing == standing).Select(j => j.Note).ToArray() is { Length: > 0 } notes)
                return new(standing, string.Join("; ", notes), judged.Any(j => j.FailedBefore));
        return NotKnown("no part of it failed that could be compared");
    }

    /// <summary>The part of an earlier result that is the same command as <paramref name="part"/> - the same ecosystem's
    /// build or test of the same target, or the check's own command.</summary>
    public static Before? Matching(IReadOnlyList<Part> earlier, Part part)
        => earlier.FirstOrDefault(e => e.Kind == part.Kind && e.Result.Command == part.Result.Command
                                       && string.Equals(e.Ecosystem?.Name, part.Ecosystem?.Name, StringComparison.Ordinal)
                                       && string.Equals(e.Target, part.Target, StringComparison.Ordinal)) is { } found
            ? new Before(null, found.Result)
            : null;

    /// <summary>The run's baseline entry for an ecosystem part - the same ecosystem, target and kind - or null.</summary>
    public static Before? FromBaseline(IReadOnlyList<BuildBaseline> builds, Part part)
        => part.Ecosystem is { } ecosystem
           && builds.FirstOrDefault(b => b.Kind == part.Kind && b.Ecosystem.Name == ecosystem.Name && b.Target == part.Target) is { } found
            ? new Before(found, null)
            : null;

    private static Judgement Of(Part after, Before? before, string root)
    {
        var what = after.Target is { } target ? $"{after.Result.Command} ({target})" : after.Result.Command;
        Judgement Said(Standing standing, string note)
            => new(standing, $"{what}: {note}", standing != Standing.NotKnown && note != PassedBefore);
        if (before is null) return Said(Standing.NotKnown, "it has no earlier result to compare with");

        if (after.Ecosystem is not { } ecosystem)
        {
            if (before.Result is not { } was) return Said(Standing.NotKnown, "it has no earlier result to compare with");
            return was.Outcome switch
            {
                CriterionOutcome.Passed => Said(Standing.Ours, PassedBefore),
                CriterionOutcome.Failed when was.ExitCode == after.Result.ExitCode && Same(was.Output, after.Result.Output)
                    => Said(Standing.Theirs, $"it failed before this step too (exit {was.ExitCode}), and printed exactly what it printed then"),
                // A command's own output has no reading the engine knows: a list of findings with one more, and the same
                // list with a time stamp in it, differ alike. Nothing here says which this is.
                CriterionOutcome.Failed => Said(Standing.Inconclusive, "it failed before this step too, and what it prints now is not "
                    + "what it printed then - a command's own output cannot be read for what is new in it"),
                _ => Said(Standing.NotKnown, "it could not be run before this step" + (string.IsNullOrWhiteSpace(was.Detail) ? "" : ": " + was.Detail!.Trim()))
            };
        }

        var baseline = before.Baseline
                       ?? (before.Result is { } earlier ? BuildRegression.Baseline(ecosystem, after.Target ?? "", earlier, root, after.Kind) : null);
        if (baseline is null) return Said(Standing.NotKnown, "it has no earlier result to compare with");
        if (!baseline.Taken) return Said(Standing.NotKnown, "it could not be run before this step: " + baseline.NotTaken);
        if (baseline.ExitCode == 0) return Said(Standing.Ours, PassedBefore);

        return after.Kind == BaselineKind.Test ? Tests(baseline, after, Said) : Build(baseline, after, root, Said);
    }

    private static Judgement Build(BuildBaseline before, Part after, string root, Func<Standing, string, Judgement> said)
    {
        // Errors on both sides, or there is nothing to compare: a build that fails without one error the ecosystem can read
        // has not said what is wrong, and "no new error" of it would be a pass of a failure nobody read.
        // Blind spot, as BuildRegression's: a compiler that stops after an earlier phase fails (Go type-checks no package
        // depending on one that does not compile) can hide a new error behind the old one; the step still goes to review.
        var now = DiagnosticSet.Of(before.Ecosystem.ParseDiagnostics(after.Result.Output ?? "", root));
        if (before.Diagnostics!.Total(DiagnosticSeverity.Error) == 0 || now.Total(DiagnosticSeverity.Error) == 0)
            return said(Standing.Inconclusive, "it failed before this step too, and what it printed does not read as errors that "
                + "could be compared with the ones before it");
        var compared = BuildRegression.Compare(before, after.Result, root, "this step");
        return compared.Outcome switch
        {
            CriterionOutcome.Passed => said(Standing.Theirs, "it failed before this step too, and " + compared.Detail),
            CriterionOutcome.Failed => said(Standing.Ours, compared.Detail!),
            _ => said(Standing.Inconclusive, "it failed before this step too, and " + compared.Detail)
        };
    }

    private static Judgement Tests(BuildBaseline before, Part after, Func<Standing, string, Judgement> said)
    {
        var was = before.Tests!;
        if (before.Ecosystem.ParseTests(after.Result.Output ?? "") is not { } now)
            return said(Standing.Inconclusive, "they failed before this step too, and nothing they printed now reads as a test result "
                + "to compare with then");
        var totals = $"before this step: {was.Describe()}; now: {now.Describe()}";
        // More failing by the run's own totals is the step's, whatever the names say: a run that printed only totals, or
        // was cut before it named a test, names nothing to compare - and 2 failed then, 7 now, would otherwise read as
        // "nothing new" and pass the step silently (code review of Task 18).
        if (was.Summary is { } then && now.Summary is { } counted && counted.Failed > then.Failed)
            return said(Standing.Ours, $"{counted.Failed - then.Failed} more test(s) fail now than before this step ({totals})");
        // Names on both sides, or there is nothing to compare: the absence of names is not the absence of tests
        // (TestRunReport), and "no test fails now that did not then" of a run that named none would be a pass nobody read -
        // as a build with no readable error is not compared either (Build, above).
        if (was.Cases.Count == 0 || now.Cases.Count == 0)
            return said(Standing.Inconclusive, "they failed before this step too, and the run does not name its tests on both "
                + $"sides, so which fail cannot be compared ({totals})");
        // Not BuildRegression's comparison: there a test that is new and failing is a note, never counted - it may be what
        // the work was asked to add. Here it is the step's: a blocking test check fails on it, and it was not failing before.
        var worse = now.Regressions(was).Concat(now.FailingAndNew(was)).Concat(now.Disappeared(was))
            .Distinct(StringComparer.Ordinal).ToArray();
        return worse.Length > 0
            ? said(Standing.Ours, $"{worse.Length} test(s) fail now, or are gone, that did not fail before this step: "
                + string.Join(", ", worse.Take(5)) + (worse.Length > 5 ? $", and {worse.Length - 5} more" : "") + $" ({totals})")
            : said(Standing.Theirs, $"they failed before this step too, and no test fails now that did not fail then ({totals})");
    }

    /// <summary>The same output, line endings and the space around it aside - a check run twice on the same files.</summary>
    private static bool Same(string? before, string? after)
        => string.Equals(Normal(before), Normal(after), StringComparison.Ordinal);

    private static string Normal(string? text) => (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}

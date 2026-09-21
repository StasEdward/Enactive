namespace Enactive.Core.Templates;

using System.Text;
using Enactive.Core.Events;

/// <summary>
/// Who said this was what finished work looks like.
///
/// <para>It decides ONE thing: what "we could not run the check" is allowed to mean. Everything
/// else about a criterion is the same whoever wrote it — same tool, same permission gate, same
/// exit code deciding, same repair attempt when it fails.</para>
/// </summary>
public enum CriterionOrigin
{
    /// <summary>
    /// A person wrote it, in a template. If it cannot be run, the run cannot be called finished:
    /// somebody said this is how you know, and nobody found out.
    /// </summary>
    Declared,

    /// <summary>
    /// The planner proposed it from the request, before any of the work was done.
    ///
    /// <para>It can hold a run back by FAILING — a command that ran and said no is evidence
    /// whoever wrote it. It cannot hold one back by being unrunnable: nobody asked for this check,
    /// so "the policy would not allow it" or "that program is not here" is a fact about a guess,
    /// not about the work. See <see cref="CriterionResult.Blocking"/>.</para>
    /// </summary>
    Proposed
}

/// <summary>How one criterion came out.</summary>
public enum CriterionOutcome
{
    /// <summary>It ran and gave the expected exit code.</summary>
    Passed,

    /// <summary>It ran and did not.</summary>
    Failed,

    /// <summary>
    /// It could not be evaluated at all — the policy refused the command, the tool is not
    /// available, the process would not start.
    ///
    /// <para>Deliberately NOT a pass. The reviewer has fails-closed for the same reason: one that
    /// cannot answer has approved nothing, and a check that could not run has verified nothing. It
    /// is also not a Failure, because the work may well be fine — what failed is our ability to
    /// say so, and a run report that confuses the two teaches people to distrust both.</para>
    /// </summary>
    Unknown
}

/// <summary>What one criterion did, kept whether it passed or not.</summary>
public sealed record CriterionResult(
    string Name,
    string Command,
    bool Required,
    CriterionOutcome Outcome,
    int? ExitCode,
    string? Detail,
    CriterionOrigin Origin = CriterionOrigin.Declared)
{
    /// <summary>
    /// Whether this result holds the run back.
    ///
    /// <para>A required criterion that FAILED always does: it ran, and it said no. An
    /// <see cref="CriterionOutcome.Unknown"/> one does only when a person
    /// <see cref="CriterionOrigin.Declared"/> it — then "we could not find out" is exactly the
    /// thing they need told. A check the planner merely proposed cannot fail a run by being
    /// unrunnable, because nobody asked for it; that is what makes a proposed check safe to arm
    /// by default, and it is the only asymmetry between the two.</para>
    /// </summary>
    public bool Blocking => Required && Outcome switch
    {
        CriterionOutcome.Passed => false,
        CriterionOutcome.Failed => true,
        _ => Origin == CriterionOrigin.Declared
    };

    /// <summary>One line for a report or a prompt: what was asked, and what came back.</summary>
    public string Describe()
    {
        var head = Outcome switch
        {
            CriterionOutcome.Passed => "PASS",
            CriterionOutcome.Failed => "FAIL",
            _ => "NOT CHECKED"
        };
        var code = ExitCode is { } c ? $" (exit {c})" : "";
        var why = string.IsNullOrWhiteSpace(Detail) ? "" : $"\n    {Detail!.Trim()}";
        return $"{head}{code} — {Name}: {Command}{(Required ? "" : "  [optional]")}{why}";
    }
}

/// <summary>
/// What the checks said about a finished run.
///
/// <para>This exists so that "done" stops being the model's opinion. Everything the engine had
/// until now to decide whether work was finished went through a language model: the worker's own
/// final message, and a reviewer judging free text — which on 2026-09-07 failed a correct run over
/// a defect it had invented, complete with a line number. An exit code is not an opinion, does not
/// confabulate, and does not need to be prompted carefully.</para>
/// </summary>
public sealed record SuccessReport(IReadOnlyList<CriterionResult> Results)
{
    public static readonly SuccessReport NothingToCheck = new(Array.Empty<CriterionResult>());

    public bool Any => Results.Count > 0;

    /// <summary>The required criteria that did not pass — the reason a run is held back, if any.</summary>
    public IReadOnlyList<CriterionResult> Blocking
        => Results.Where(r => r.Blocking).ToArray();

    /// <summary>
    /// The run's outcome after the checks have had their say. Only ever equal to or worse than what
    /// it was: criteria can hold a run back, never promote one. A run that already failed is left
    /// alone — its outcome was decided by something that actually went wrong, and burying that under
    /// a build result would lose it.
    /// </summary>
    public RunOutcomeKind Apply(RunOutcomeKind outcome)
    {
        if (outcome != RunOutcomeKind.Completed)
            return outcome;

        var blocking = Blocking;
        if (blocking.Count == 0)
            return outcome;

        // A check that FAILED says the work is wrong. A check that could not be evaluated says only
        // that we do not know - which is not a success, and not the same accusation.
        return blocking.Any(r => r.Outcome == CriterionOutcome.Failed)
            ? RunOutcomeKind.Failed
            : RunOutcomeKind.Incomplete;
    }

    /// <summary>Why the run was held back, in the terminal event's own words.</summary>
    public string? Explain()
    {
        var blocking = Blocking;
        if (blocking.Count == 0)
            return null;

        var failed = blocking.Count(r => r.Outcome == CriterionOutcome.Failed);
        var unknown = blocking.Count - failed;

        var parts = new List<string>();
        if (failed > 0)
            parts.Add($"{failed} success criterion(s) failed: "
                    + string.Join(", ", blocking.Where(r => r.Outcome == CriterionOutcome.Failed)
                                                .Select(r => r.Name)));
        if (unknown > 0)
            parts.Add($"{unknown} success criterion(s) could not be checked: "
                    + string.Join(", ", blocking.Where(r => r.Outcome == CriterionOutcome.Unknown)
                                                .Select(r => r.Name)));

        return string.Join("; ", parts);
    }

    /// <summary>The whole verdict, for a run report or as evidence in a retry.</summary>
    public string Describe()
    {
        if (Results.Count == 0)
            return "(this run had no success criteria)";

        var sb = new StringBuilder();
        foreach (var result in Results)
            sb.AppendLine(result.Describe());
        return sb.ToString().TrimEnd();
    }
}

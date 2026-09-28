namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Builds;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>What one ecosystem's build of one target reported before any work, or why it was not taken.</summary>
internal sealed record BuildBaseline(IEcosystem Ecosystem, string Target, int? ExitCode, DiagnosticSet? Diagnostics,
    string? NotTaken)
{
    public bool Taken => NotTaken is null && ExitCode is not null && Diagnostics is not null;

    /// <summary>One line for the run's log: what the build said before the work.</summary>
    public string Describe() => Taken
        ? $"Build before the work ({Ecosystem.Name}, {Target}): exit {ExitCode}, "
          + $"{Diagnostics!.Total(DiagnosticSeverity.Error)} error(s), {Diagnostics.Total(DiagnosticSeverity.Warning)} warning(s)."
        : $"Build before the work ({Ecosystem.Name}, {Target}) was not taken: {NotTaken}";
}

/// <summary>
/// "The work did not break the build" - checked by the engine itself, against the build as it was
/// BEFORE the work, for whatever kind of project the workspace is.
///
/// <para><b>Why against a baseline.</b> "The build is green" is not what a step can be asked for
/// when it was not green to begin with: a workspace with four failing tests, or an error the task
/// is not about, would hold every run back. What a run CAN be asked is that it added no error of
/// its own. So the build is run before any work and again after, and the diagnostics are compared by
/// identity - file, code, message, not line, so an error that has only moved is not new
/// (<see cref="DiagnosticSet"/>).</para>
///
/// <para><b>Generic.</b> Nothing here knows what a build is. Each <see cref="IEcosystem"/> says
/// whether the workspace is one of its kind, what to build, which files a change to can change the
/// build, and how to read what its build printed. A workspace no ecosystem recognises gets no build
/// check and pays nothing: a wiki, a disk report, a batch of emails.</para>
///
/// <para><b>What it will not do yet.</b> Its result is <see cref="CriterionOrigin.System"/>: a
/// report, never a block. A new error found at the end is not yet proof that the run made it -
/// something outside the run can change the workspace while it works (run 3fe4f8, 2026-09-28) - and
/// whether it should hold a run back is to be decided on data, not assumed. It does not ask for
/// permission either: a check nobody asked for must not interrupt anybody, so where running a command
/// would need an approval, it is not taken, and the run says so.</para>
/// </summary>
internal static class BuildRegression
{
    internal const string Name = "No new build errors";

    /// <summary>How many new errors the result names; the rest are counted.</summary>
    private const int MaxNamed = 5;

    /// <summary>The engine's own build of one target, as a criterion the ordinary evaluator runs.</summary>
    internal static SuccessCriterionDefinition Criterion(IEcosystem ecosystem, string target)
        => new($"{Name} ({ecosystem.Name}: {target})", ecosystem.BuildCommand(target), 0,
            Required: false, Origin: CriterionOrigin.System);

    /// <summary>
    /// Whether the engine may run a command of its own without asking anybody. The success-check
    /// command tool, allowed outright by the policy; anything short of that is a no.
    /// </summary>
    internal static string? WhyNotAllowed(IToolRegistry tools, IPermissionEngine permissions, PermissionPolicy policy)
    {
        var candidates = tools.Definitions.Where(d => d.Kind == ToolKind.Command && d.RunsSuccessChecks).ToArray();
        if (candidates.Length != 1) return "no single command tool runs checks in this workspace";
        try
        {
            return permissions.Evaluate(policy, candidates[0].Name, tools.RequiredLevelOf(candidates[0].Name)) == PermissionDecision.Allow
                ? null
                : "running it would need an approval, and the engine does not interrupt anybody for a check nobody asked for";
        }
        catch (Exception ex) { return "the command tool is not available: " + ex.Message; }
    }

    internal static BuildBaseline Baseline(IEcosystem ecosystem, string target, CriterionResult result, string root)
        => result.ExitCode is { } exit
            ? new(ecosystem, target, exit, DiagnosticSet.Of(ecosystem.ParseDiagnostics(result.Output ?? "", root)), null)
            : new(ecosystem, target, null, null, string.IsNullOrWhiteSpace(result.Detail) ? "it did not run" : result.Detail);

    /// <summary>
    /// Whether the run can have changed what this ecosystem's build reports: it wrote a file the
    /// ecosystem owns, or it ran a call whose writes are not recorded by file - a command can change
    /// anything. A run that only wrote documentation is not rebuilt, as the plan says.
    /// </summary>
    internal static bool Touched(IEcosystem ecosystem, IEnumerable<ArtifactRef> produced, IEnumerable<ExecutedAction> actions)
        => produced.Any(a => ecosystem.Owns(a.RelativePath))
           || actions.Any(a => a.Outcome != ActionOutcome.Refused && a.WorkspaceEffect != WorkspaceEffect.None
                               && a.ChangedPaths is not { Count: > 0 });

    /// <summary>The build after the work, against the build before it.</summary>
    internal static CriterionResult Compare(BuildBaseline before, CriterionResult after, string root)
    {
        CriterionResult Result(CriterionOutcome outcome, string detail)
            => new(after.Name, after.Command, Required: false, outcome, after.ExitCode, detail, CriterionOrigin.System);

        if (!before.Taken)
            return Result(CriterionOutcome.Unknown, "the build could not be run before the work, so there is nothing "
                + "to compare it with: " + before.NotTaken);
        if (after.ExitCode is not { } exit)
            return Result(CriterionOutcome.Unknown, "the build could not be run after the work: "
                + (string.IsNullOrWhiteSpace(after.Detail) ? "it did not run" : after.Detail));

        var now = DiagnosticSet.Of(before.Ecosystem.ParseDiagnostics(after.Output ?? "", root));
        var was = before.Diagnostics!;
        var totals = $"before the work: exit {before.ExitCode}, {was.Total(DiagnosticSeverity.Error)} error(s); "
                   + $"now: exit {exit}, {now.Total(DiagnosticSeverity.Error)} error(s)";

        var added = now.NewSince(was);
        if (added.Count > 0)
        {
            var named = added.Take(MaxNamed).Select(r =>
                $"{r.Identity.Code} in {r.Identity.Path ?? "(no file)"}"
                + (r.Example.Line is { } line ? $" line {line}" : "")
                + $": {r.Identity.Message}" + (r.Added > 1 ? $" (x{r.Added})" : ""));
            return Result(CriterionOutcome.Failed, $"{added.Sum(r => r.Added)} error(s) not in the build before the work - "
                + string.Join("; ", named) + (added.Count > MaxNamed ? $"; and {added.Count - MaxNamed} more" : "")
                + $". ({totals})");
        }

        // Passed before and fails now, with nothing in the output the ecosystem can read as the
        // reason. That is still a build broken by the time the run ended, and is said as one.
        if (before.ExitCode == 0 && exit != 0)
            return Result(CriterionOutcome.Failed, "the build passed before the work and fails now, and nothing it "
                + $"printed reads as a diagnostic that explains it. ({totals})");

        return Result(CriterionOutcome.Passed, $"no error that was not there before the work ({totals}).");
    }
}

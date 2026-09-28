namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Builds;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>Which half of the workspace baseline an entry is: what the build said, or what the tests said.</summary>
internal enum BaselineKind { Build, Test }

/// <summary>
/// What one ecosystem's build - or test run - of one target reported before any work, or why it was
/// not taken. Phase 1.6's WorkspaceBaseline is the list of these: ecosystems, targets, build status
/// with its diagnostics, test status with its tests.
/// </summary>
internal sealed record BuildBaseline(IEcosystem Ecosystem, string Target, int? ExitCode, DiagnosticSet? Diagnostics,
    string? NotTaken)
{
    public BaselineKind Kind { get; init; } = BaselineKind.Build;

    /// <summary>The tests the run named, for a <see cref="BaselineKind.Test"/> entry.</summary>
    public TestRunReport? Tests { get; init; }

    public bool Taken => NotTaken is null && ExitCode is not null
                         && (Kind == BaselineKind.Build ? Diagnostics is not null : Tests is not null);

    private string What => Kind == BaselineKind.Build ? "Build" : "Tests";

    /// <summary>One line for the run's log: what the build or the tests said before the work.</summary>
    public string Describe() => !Taken
        ? $"{What} before the work ({Ecosystem.Name}, {Target}) was not taken: {NotTaken}"
        : Kind == BaselineKind.Build
            ? $"Build before the work ({Ecosystem.Name}, {Target}): exit {ExitCode}, "
              + $"{Diagnostics!.Total(DiagnosticSeverity.Error)} error(s), {Diagnostics.Total(DiagnosticSeverity.Warning)} warning(s)."
            : $"Tests before the work ({Ecosystem.Name}, {Target}): exit {ExitCode}, {Tests!.Describe()}.";

    /// <summary>As data, for the checkpoint and the task's own file.</summary>
    public BaselineSnapshot ToSnapshot()
        => new(Ecosystem.Name, Kind.ToString(), Target, ExitCode, NotTaken, Diagnostics?.All, Tests);

    /// <summary>
    /// Back from data, against the ecosystems this engine has now. An ecosystem that is gone cannot
    /// read what its build prints, so its entry comes back as not taken, and says why.
    /// </summary>
    public static BuildBaseline? From(BaselineSnapshot snapshot, IReadOnlyList<IEcosystem> ecosystems)
    {
        if (ecosystems.FirstOrDefault(e => e.Name == snapshot.Ecosystem) is not { } ecosystem) return null;
        var kind = Enum.TryParse<BaselineKind>(snapshot.Kind, out var parsed) ? parsed : BaselineKind.Build;
        return new(ecosystem, snapshot.Target, snapshot.ExitCode,
            snapshot.Diagnostics is { } diagnostics ? DiagnosticSet.Of(diagnostics) : null, snapshot.NotTaken)
        {
            Kind = kind,
            Tests = snapshot.Tests
        };
    }
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
    internal const string TestName = "No test that passed now fails";

    /// <summary>How many new errors the result names; the rest are counted.</summary>
    private const int MaxNamed = 5;

    /// <summary>The engine's own build - or test run - of one target, as a criterion the ordinary evaluator runs.</summary>
    internal static SuccessCriterionDefinition Criterion(IEcosystem ecosystem, string target,
        BaselineKind kind = BaselineKind.Build)
        => kind == BaselineKind.Build
            ? new($"{Name} ({ecosystem.Name}: {target})", ecosystem.BuildCommand(target), 0,
                Required: false, Origin: CriterionOrigin.System)
            : new($"{TestName} ({ecosystem.Name}: {target})", ecosystem.TestCommand(target), 0,
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

    internal static BuildBaseline Baseline(IEcosystem ecosystem, string target, CriterionResult result, string root,
        BaselineKind kind = BaselineKind.Build)
    {
        if (result.ExitCode is not { } exit)
            return new(ecosystem, target, null, null, string.IsNullOrWhiteSpace(result.Detail) ? "it did not run" : result.Detail)
                { Kind = kind };
        if (kind == BaselineKind.Build)
            return new(ecosystem, target, exit, DiagnosticSet.Of(ecosystem.ParseDiagnostics(result.Output ?? "", root)), null);
        return ecosystem.ParseTests(result.Output ?? "") is { } tests
            ? new(ecosystem, target, exit, null, null) { Kind = kind, Tests = tests }
            : new(ecosystem, target, exit, null, "nothing its test run printed reads as a test result") { Kind = kind };
    }

    /// <summary>
    /// Whether the run can have changed what this ecosystem's build reports: it wrote a file the
    /// ecosystem owns, or it ran a call whose writes are not recorded by file - a command can change
    /// anything. A run that only wrote documentation is not rebuilt, as the plan says.
    /// </summary>
    internal static bool Touched(IEcosystem ecosystem, IEnumerable<ArtifactRef> produced, IEnumerable<ExecutedAction> actions)
        => produced.Any(a => ecosystem.Owns(a.RelativePath))
           || actions.Any(a => a.Outcome != ActionOutcome.Refused && a.WorkspaceEffect != WorkspaceEffect.None
                               && a.ChangedPaths is not { Count: > 0 });

    /// <summary>
    /// Whether a result is worth a second run before it is believed: tests that passed and fail now can be
    /// flaky. A build is not run twice - what it prints is the same both times.
    /// </summary>
    internal static bool WorthRunningAgain(BuildBaseline before, CriterionResult compared)
        => before.Kind == BaselineKind.Test && compared.Outcome == CriterionOutcome.Failed;

    /// <summary>The build - or the tests - after the work, against the same before it.</summary>
    internal static CriterionResult Compare(BuildBaseline before, CriterionResult after, string root, string span = "the work",
        CriterionResult? again = null)
    {
        CriterionResult Result(CriterionOutcome outcome, string detail)
            => new(after.Name, after.Command, Required: false, outcome, after.ExitCode, detail, CriterionOrigin.System);

        if (before.Kind == BaselineKind.Test)
            return CompareTests(before, after, Result, span, again);

        if (!before.Taken)
            return Result(CriterionOutcome.Unknown, $"the build could not be run before {span}, so there is nothing "
                + "to compare it with: " + before.NotTaken);
        if (after.ExitCode is not { } exit)
            return Result(CriterionOutcome.Unknown, $"the build could not be run after {span}: "
                + (string.IsNullOrWhiteSpace(after.Detail) ? "it did not run" : after.Detail));

        var now = DiagnosticSet.Of(before.Ecosystem.ParseDiagnostics(after.Output ?? "", root));
        var was = before.Diagnostics!;
        var totals = $"before {span}: exit {before.ExitCode}, {was.Total(DiagnosticSeverity.Error)} error(s); "
                   + $"now: exit {exit}, {now.Total(DiagnosticSeverity.Error)} error(s)";

        var all = now.NewSince(was);
        // What the machine did is not the work's regression: a build that could not replace files another
        // process holds has not been compared at all, and says so rather than failing the work.
        var environmental = all.Where(r => before.Ecosystem.IsEnvironmental(r.Identity)).ToArray();
        var added = all.Except(environmental).ToArray();
        var aside = environmental.Length == 0 ? ""
            : $" {environmental.Sum(r => r.Added)} other new error(s) are the machine's, not the code's ("
              + string.Join(", ", environmental.Select(r => r.Identity.Code).Distinct()) + ": a file in use by another process).";
        if (added.Length == 0 && environmental.Length > 0)
            return Result(CriterionOutcome.Unknown, "the build could not replace files that another process holds, so what the "
                + $"work did to the code was not compared: {environmental.Sum(r => r.Added)} error(s) of "
                + string.Join(", ", environmental.Select(r => r.Identity.Code).Distinct())
                + $", e.g. {environmental[0].Identity.Message} Close what holds them and build again. ({totals})");
        if (added.Length > 0)
        {
            var named = added.Take(MaxNamed).Select(r =>
                $"{r.Identity.Code} in {r.Identity.Path ?? "(no file)"}"
                + (r.Example.Line is { } line ? $" line {line}" : "")
                + $": {r.Identity.Message}" + (r.Added > 1 ? $" (x{r.Added})" : ""));
            return Result(CriterionOutcome.Failed, $"{added.Sum(r => r.Added)} error(s) not in the build before {span} - "
                + string.Join("; ", named) + (added.Length > MaxNamed ? $"; and {added.Length - MaxNamed} more" : "")
                + $".{aside} ({totals})");
        }

        // Passed before and fails now, with nothing in the output the ecosystem can read as the
        // reason. That is still a build broken by the time the run ended, and is said as one.
        if (before.ExitCode == 0 && exit != 0)
            return Result(CriterionOutcome.Failed, $"the build passed before {span} and fails now, and nothing it "
                + $"printed reads as a diagnostic that explains it. ({totals})");

        return Result(CriterionOutcome.Passed, $"no error that was not there before {span} ({totals}).");
    }

    /// <summary>
    /// The tests after the work, against the tests before it: a test that passed and fails now is
    /// the work's regression; a test failing now that the run before did not name at all is
    /// reported and not counted - it may be exactly what the work was asked to add.
    /// </summary>
    private static string Named(IReadOnlyCollection<string> tests)
        => string.Join(", ", tests.Take(MaxNamed)) + (tests.Count > MaxNamed ? $", and {tests.Count - MaxNamed} more" : "");

    private static CriterionResult CompareTests(BuildBaseline before, CriterionResult after,
        Func<CriterionOutcome, string, CriterionResult> result, string span,
        CriterionResult? again)
    {
        if (!before.Taken)
            return result(CriterionOutcome.Unknown, $"the tests could not be run before {span}, so there is nothing "
                + "to compare them with: " + before.NotTaken);
        if (after.ExitCode is not { } exit)
            return result(CriterionOutcome.Unknown, $"the tests could not be run after {span}: "
                + (string.IsNullOrWhiteSpace(after.Detail) ? "they did not run" : after.Detail));

        var was = before.Tests!;
        if (before.Ecosystem.ParseTests(after.Output ?? "") is not { } now)
            // Ran before, and nothing now reads as a test result: the tests did not get as far as
            // running - a broken build stops them. That is the work's, whatever the build check says.
            return before.ExitCode == 0
                ? result(CriterionOutcome.Failed, $"the tests ran before {span} and did not run after it (exit {exit}).")
                : result(CriterionOutcome.Unknown, $"nothing the tests printed after {span} reads as a test result (exit {exit}).");

        var totals = $"before {span}: {was.Describe()}; now: {now.Describe()}";
        var added = now.FailingAndNew(was);
        var newNote = added.Count == 0 ? ""
            : $" {added.Count} test(s) the run before {span} did not have fail now - not counted against it, it may be "
              + $"what {span} was asked to add: " + string.Join(", ", added.Take(MaxNamed))
              + (added.Count > MaxNamed ? $", and {added.Count - MaxNamed} more" : "") + ".";

        var broken = now.Regressions(was);
        // Run again (deferred item B): a test that fails once and passes the next time is flaky, not
        // broken by the work - on this machine one test suite gave 41, 42, 43 and 46 failures on the
        // same code. Only what failed both times is counted, and only what PASSED the second time is
        // flaky: a test the second run skipped or did not name, or a second run that did not happen,
        // has confirmed nothing either way.
        var flakyNote = "";
        var unconfirmed = Array.Empty<string>();
        if (broken.Count > 0 && again is not null)
        {
            var second = again.ExitCode is null ? null : before.Ecosystem.ParseTests(again.Output ?? "");
            var flaky = second is null ? [] : broken.Where(t => second.VerdictOf(t) == TestVerdict.Passed).ToArray();
            unconfirmed = broken.Where(t => second?.VerdictOf(t) is not (TestVerdict.Passed or TestVerdict.Failed)).ToArray();
            broken = second is null ? [] : broken.Where(t => second.VerdictOf(t) == TestVerdict.Failed).ToArray();
            if (flaky.Length > 0)
                flakyNote = $" {flaky.Length} test(s) failed and then passed when run again - flaky, not counted: "
                    + Named(flaky) + ".";
            if (unconfirmed.Length > 0)
                flakyNote += $" {unconfirmed.Length} test(s) failed and were not confirmed by a second run ("
                    + (second is null
                        ? again.ExitCode is null
                            ? "it did not run" + (string.IsNullOrWhiteSpace(again.Detail) ? "" : ": " + again.Detail)
                            : "nothing it printed reads as a test result"
                        : "it skipped them or did not name them")
                    + "): " + Named(unconfirmed) + ".";
        }
        if (broken.Count > 0)
            return result(CriterionOutcome.Failed, $"{broken.Count} test(s) passed before {span} and fail now: "
                + Named(broken) + (again is null ? "" : ", both times it ran") + $". ({totals}){newNote}{flakyNote}");
        if (unconfirmed.Length > 0)
            return result(CriterionOutcome.Unknown, $"no test that passed before {span} failed twice, and not every failure "
                + $"could be checked again ({totals}).{newNote}{flakyNote}");

        return result(CriterionOutcome.Passed, $"no test that passed before {span} fails now ({totals}).{newNote}{flakyNote}");
    }
}

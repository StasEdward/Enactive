namespace Enactive.Agents;

using Enactive.Core.Builds;
using Enactive.Core.Checks;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>When the declared checks owed for what a step changed run (Plan Phase 3.2).</summary>
internal enum StepCheckTiming
{
    /// <summary>
    /// At the step's own gate, before any review: steps run one at a time (or this one runs alone) and the run's writes
    /// are on disk, so what a check finds is this step's.
    /// </summary>
    AtGate,

    /// <summary>
    /// Blocking ones at the next stable boundary - the end of the wave, once the steps running beside this one have
    /// finished, or the run's end where waves are not kept. Steps share one workspace while they run side by side: a check
    /// run at one step's gate would read a sibling's half-made change, and its failure could be put on the wrong step.
    /// Advisory ones still run at the gate - they decide nothing, and the reviewer is told the workspace was shared.
    /// </summary>
    Deferred,

    /// <summary>
    /// None against the disk: the run's writes are staged proposals, not on disk, and every command and file check reads
    /// the disk - a check run now would judge the files as they were before the run. Owed when the files are applied.
    /// </summary>
    Staged
}

/// <summary>What a step changed, for the checks owed for it.</summary>
/// <param name="Paths">Workspace-relative, with '/' between folders.</param>
/// <param name="Unmeasured">Why what it changed is not all known - it ran commands whose effects were not measured - or null.</param>
internal sealed record StepChanges(IReadOnlyCollection<string> Paths, string? Unmeasured);

/// <summary>
/// The declared workspace checks (Plan Phase 3) as the engine runs them: which fire, which a change is owed, and each as
/// the success criteria the ordinary evaluator runs - the same tool, the same permission gate, the same exit code
/// deciding and the same "ran no tests". There is no second runner: a check is a criterion a person wrote down once for
/// every run of this workspace.
/// </summary>
internal static class WorkspaceChecks
{
    /// <summary>What a check that needs the applied files is said to be in a staged run (the plan's wording, decided).</summary>
    internal const string Staged = "staged: needs the applied files";

    internal const string StagedDetail = Staged + " - the run's writes are proposals until a person applies them, and the "
        + "check would read the files as they were before the run, so it verified nothing";

    /// <summary>A check the policy would have had to ask about: said, never skipped silently (Plan Phase 3.2).</summary>
    internal const string NeedsPermission = "not run: needs permission";

    /// <summary>Why a check is not run on a question: a step's gate, a wave and a run's end are no time to stop and ask.</summary>
    internal const string NotAsked = "the policy does not let the engine run the check command without asking, and a "
        + "check does not stop a run to ask; allow the command for this workspace to have it run";

    internal const string Unmeasured = "the step ran commands whose changes were not measured, so every step check runs";

    /// <summary>
    /// The checks that fire on <paramref name="fire"/> and can run: enabled, and with a command or an ecosystem to run
    /// through. Not the implicit build and test nobody raised: they ARE the engine's own build and test checks
    /// (BuildRegression), which already run at the run's end against the build before the work - run here as well, every
    /// .NET run would build twice and say so twice. Raised by a manifest, they are the person's, and run as such.
    /// </summary>
    public static IReadOnlyList<ResolvedCheck> Firing(CheckSnapshot? snapshot, CheckFire fire)
        => snapshot?.Checks.Where(c => c.Definition.Enabled && c.Definition.FireOn == fire
                                       && !(c.Origin == CheckOrigin.Implicit && c.Shadows is null)
                                       && (c.RunsThroughEcosystem || !string.IsNullOrWhiteSpace(c.Definition.Command)))
               .ToArray()
           ?? [];

    /// <summary>Whether a change to any of <paramref name="paths"/> is owed this check - by its globs, or for an ecosystem's
    /// check without any, by the paths that ecosystem owns (IEcosystem.Owns).</summary>
    public static bool Covers(ResolvedCheck check, IEnumerable<string> paths, IReadOnlyList<IEcosystem> ecosystems)
    {
        var matches = check.Definition.Matches ?? [];
        if (matches.Count > 0)
            return paths.Any(p => matches.Any(g => CheckGlob.IsMatch(g, p)));
        if (!check.RunsThroughEcosystem) return false;
        var owners = Owners(check, ecosystems);
        return paths.Any(p => owners.Any(e => e.Owns(p)));
    }

    /// <summary>The ecosystems an ecosystem check is the build or test of, among those the engine knows.</summary>
    public static IReadOnlyList<IEcosystem> Owners(ResolvedCheck check, IReadOnlyList<IEcosystem> ecosystems)
        => ecosystems.Where(e => check.Ecosystems?.Contains(e.Name, StringComparer.OrdinalIgnoreCase) == true).ToArray();

    /// <summary>
    /// What a step changed: what the workspace measurement found since the step began, and the paths its own calls recorded
    /// (writes, edits, deletions, renames) and its store holds - the measurement alone misses a file git ignores, the
    /// record alone misses what a command did. Where nothing was measured and the step ran a command that recorded no
    /// paths, what it changed is not known: that is said, and every step check is owed - unknown attribution never means
    /// "matches nothing", which would let a command change anything unchecked.
    /// </summary>
    /// <param name="measured">What the measurement found, or null when nothing was measured.</param>
    /// <param name="isCommand">Whether a tool runs commands (ToolKind.Command) - whose effects only a measurement shows.</param>
    public static StepChanges Of(IEnumerable<string>? measured, IEnumerable<ExecutedAction> actions, IEnumerable<string> touched,
        string root, Func<string, bool> isCommand)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (WaveCapture.Relative(path, root) is { Length: > 0 } relative) paths.Add(relative);
        }
        foreach (var path in measured ?? []) Add(path);
        var own = actions.Where(a => a.Origin != ToolCallOrigin.Engine && a.Outcome != ActionOutcome.Refused).ToArray();
        foreach (var path in own.SelectMany(a => a.ChangedPaths ?? [])) Add(path);
        foreach (var path in touched) Add(path);
        var unmeasured = measured is null
                         && own.Any(a => a.WorkspaceEffect != WorkspaceEffect.None && a.ChangedPaths is not { Count: > 0 } && isCommand(a.Tool))
            ? Unmeasured : null;
        return new(paths, unmeasured);
    }

    /// <summary>A check's result by its id and where it was owed - the name a criterion event and the run's reason carry.</summary>
    public static string Label(string id, string at) => $"{id} ({at})";

    /// <summary>
    /// The criteria a check runs as: its command - or, for a check that runs through an ecosystem, that ecosystem's build
    /// (or test) of each target there is now, as the engine's own build check runs it. Empty with <paramref name="none"/>
    /// saying why, when an ecosystem check finds nothing to run in the workspace as it is now.
    /// </summary>
    public static IReadOnlyList<SuccessCriterionDefinition> Criteria(ResolvedCheck check, string name,
        IReadOnlyList<IEcosystem> ecosystems, string root, out string? none)
    {
        none = null;
        var required = check.Definition.Severity == CheckSeverity.Blocking;
        SuccessCriterionDefinition Of(string command) => new(name, command, 0, Required: required, Origin: CriterionOrigin.Declared);
        if (!check.RunsThroughEcosystem)
            return [Of(check.Definition.Command!.Trim())];

        var tests = string.Equals(check.Id, ImplicitCheckIds.Test, StringComparison.OrdinalIgnoreCase);
        var criteria = new List<SuccessCriterionDefinition>();
        foreach (var ecosystem in Owners(check, ecosystems))
        {
            EcosystemTargets? targets;
            try { targets = ecosystem.Detect(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { targets = null; }
            // As many targets as the engine's own build baseline takes (Orchestrator.MaxBuildTargets): a workspace of loose
            // projects is built a few at most.
            foreach (var target in (tests ? targets?.Tests : targets?.Build)?.Take(MaxTargets) ?? [])
                criteria.Add(Of(tests ? ecosystem.TestCommand(target) : ecosystem.BuildCommand(target)));
        }
        if (criteria.Count == 0)
            none = $"there is no {(tests ? "test project" : "build target")} of {string.Join(", ", check.Ecosystems ?? [])} "
                   + "in the workspace now, so there was nothing to run";
        return criteria;
    }

    private const int MaxTargets = 4;

    /// <summary>What a check runs, for a person: its command, or the commands its ecosystem gave it.</summary>
    public static string CommandOf(ResolvedCheck check, IReadOnlyList<SuccessCriterionDefinition> criteria)
        => !check.RunsThroughEcosystem ? check.Definition.Command!.Trim()
            : criteria.Count > 0 ? string.Join(" ; ", criteria.Select(c => c.Command))
            : $"the {check.Id} of {string.Join(", ", check.Ecosystems ?? [])}";

    /// <summary>
    /// One result for a check that ran as several commands (an ecosystem's targets): failed when any failed, passed when
    /// every one passed, not known otherwise - as "the tests pass" is decided over every test project.
    /// </summary>
    public static CriterionResult Combine(string name, string command, bool required, IReadOnlyList<CriterionResult> results)
    {
        if (results.Count == 1)
            return results[0] with { Name = name, Command = command, Required = required, Origin = CriterionOrigin.Declared };
        var outcome = results.Any(r => r.Outcome == CriterionOutcome.Failed) ? CriterionOutcome.Failed
            : results.Count > 0 && results.All(r => r.Outcome == CriterionOutcome.Passed) ? CriterionOutcome.Passed
            : CriterionOutcome.Unknown;
        var deciding = results.FirstOrDefault(r => r.Outcome == CriterionOutcome.Failed) ?? results.LastOrDefault();
        var said = string.Join("\n", results.Select(r => $"{r.Command}: "
            + (r.Outcome switch { CriterionOutcome.Passed => "passed", CriterionOutcome.Failed => "FAILED", _ => "not checked" })
            + (string.IsNullOrWhiteSpace(r.Detail) ? "" : " - " + r.Detail!.Trim())));
        return new CriterionResult(name, command, required, outcome, deciding?.ExitCode, said, CriterionOrigin.Declared)
        {
            Output = string.Join("\n", results.Select(r => r.Output).Where(o => !string.IsNullOrEmpty(o)))
        };
    }

    /// <summary>
    /// What a step a blocking check failed is told to repair: the check's own output, its start and its end (as every
    /// criterion's detail is cut, SuccessEvaluator.Trim) - the totals of a test run and the error count of a build are
    /// what a program prints last.
    /// </summary>
    public static string RepairAdvice(string id, CriterionResult failed)
        => $"The engine's check '{id}' ({failed.Command}) failed"
           + (failed.ExitCode is { } exit ? $" with exit code {exit}" : "")
           + ". It is a command a person set to block this kind of change, run by the engine on what this step changed - not "
           + "a reviewer's judgement. What it printed:\n"
           + (SuccessEvaluator.Trim(failed.Output ?? failed.Detail) is { Length: > 0 } printed ? printed : "(nothing)");

    /// <summary>A check's result as one line for the reviewer.</summary>
    public static string EvidenceLine(ResolvedCheck check, CriterionResult result)
        => $"- {check.Id} ({(check.Definition.Severity == CheckSeverity.Blocking ? "blocking" : "advisory")}, {result.Command}): "
           + (result.Outcome switch { CriterionOutcome.Passed => "PASS", CriterionOutcome.Failed => "FAIL", _ => "NOT CHECKED" })
           + (result.ExitCode is { } exit ? $" (exit {exit})" : "")
           + (string.IsNullOrWhiteSpace(result.Detail) ? "" : $" - {result.Detail!.Trim()}");
}

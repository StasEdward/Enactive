namespace Enactive.Settings;

using Enactive.Agents;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Storage;
using Enactive.Tools;
using Enactive.Workspace;

/// <summary>What a press of "Run checks now" came to: the record kept, or why nothing was run.</summary>
public sealed record ChecksNowResult(CheckRecord? Record, string? Problem);

/// <summary>
/// "Run checks now" for a staged run (Plan Phase 3.2, staged runs, first version), the same for every host that shows a
/// staged run's proposals - the service's pages and the window.
///
/// <para><b>Why after the run, by a person.</b> A staged run's checks cannot run while it runs: its writes are proposals,
/// and every check reads the disk (they are "staged: needs the applied files"). Applying is not a boundary that could run
/// them either: a person applies or rejects each file on its own, after the run has ended, and the workspace is then
/// whatever mix they composed. So a person asks, when they have decided - and what they get is a record of THAT state at
/// THAT time, beside the run, never a change to the run's recorded outcome and never a roll-back.</para>
///
/// <para><b>What runs.</b> The run's snapshotted checks (RunRecord.CheckSnapshot) - the definitions it was started with,
/// not the manifests as they are since - through the one runner every declared check goes through
/// (Orchestrator.RunChecksNowAsync), under the permissions of the moment: the workspace's level as it is now, through the
/// same permission engine. A check the policy would ask about is not run and not asked about: "not run: needs permission".
/// </para>
/// </summary>
public static class ChecksNow
{
    /// <summary>Why <paramref name="run"/> cannot have its checks run now, or null when it can.</summary>
    public static string? Refusal(RunRecord? run)
    {
        if (run is null)
            return "There is no such run in this workspace.";
        // Only a staged run: a run that wrote to the disk had its checks run where they were owed, as it ran.
        if (run.Settings?.Staged != true)
            return "Run checks now is for a staged run; this run wrote to the files directly and its checks ran with it.";
        if (Runnable(run.CheckSnapshot).Count == 0)
            return "This run was started with no checks that run a command, so there is nothing to run now.";
        return null;
    }

    /// <summary>
    /// The checks of a snapshot that can be run: enabled, with a command or an ecosystem to run through. The implicit build
    /// and test nobody raised are among them, unlike at a step's gate (WorkspaceChecks.Firing): there they are left to the
    /// engine's own build check, which ran with the run - here nothing else runs them, and in a staged run that build check
    /// read the files as they were before the run, so it verified nothing.
    /// </summary>
    public static IReadOnlyList<ResolvedCheck> Runnable(CheckSnapshot? snapshot)
        => snapshot?.Checks.Where(c => c.Definition.Enabled && (c.RunsThroughEcosystem || !string.IsNullOrWhiteSpace(c.Definition.Command)))
               .ToArray()
           ?? [];

    /// <summary>
    /// Runs the staged run's snapshotted checks against the workspace as it is now and keeps the record in the run's
    /// history (IRunStore.AddCheckRecordAsync). The host checks who may (the service: the roles that may apply proposals)
    /// and audits it; this checks the run.
    /// </summary>
    /// <param name="engine">The host's engine as it is now - its settings decide the shell rule, as for a run.</param>
    /// <param name="who">Who pressed it, for the record.</param>
    /// <param name="autonomy">The workspace's level now - the permissions the checks run under.</param>
    /// <param name="store">The run's history; null for the workspace's own (RunStoreFactory). Tests pass one.</param>
    public static async Task<ChecksNowResult> RunAsync(ComposedEngine engine, WorkspaceInfo workspace, Guid runId, string who,
        int autonomy, CancellationToken ct, IRunStore? store = null)
    {
        store ??= RunStoreFactory.Create(workspace);
        var run = await store.LoadAsync(runId, ct);
        if (Refusal(run) is { } refused)
            return new(null, refused);
        var checks = Runnable(run!.CheckSnapshot);

        // One press at a time in a workspace, and never beside a run of this process (WorkspaceActivity): two people on the
        // service pressing at once ran the same commands side by side, and a run started meanwhile edited the files being
        // checked. Held to the end - the record kept - and a run is refused while it is.
        using var activity = WorkspaceActivity.TryChecks(workspace.RootPath, out var busy);
        if (activity is null)
            return new(null, busy);

        // Commands run in the workspace, so it is held as a run holds it (WorkspaceLease): another process's run would be
        // editing the very files being checked, and what a check found would be neither state.
        ProcessLease lease;
        try { lease = WorkspaceLease.Take(workspace.RootPath); }
        catch (WorkspaceInUseException ex) { return new(null, ex.Message); }
        using (lease)
        {
            var orchestrator = new Orchestrator(new RunEngineResources
            {
                Providers = engine.Providers,
                Models = engine.Models,
                Workers = engine.Workers,
                Router = engine.Router,
                // The built-in tools only: a check runs the command tool, and nothing here needs a tool server started.
                Tools = new LoggingToolRegistry(engine.BuiltInTools, engine.Log),
                Artifacts = new DiskArtifactStore(workspace),
                Workspace = workspace,
                WorkspaceChanges = new WorkspaceChangesFactory(),
                Planner = engine.Planner,
                Permissions = engine.Permissions,
                // Never asked: a check the policy would ask about is not run (BuildRegression.WhyNotAllowed) - this only
                // stands behind that, refusing should anything ever reach it.
                Decisions = new UnattendedDecisionHandler(),
                Policy = PolicyNow(engine, run, autonomy),
                Ecosystems = KnownEcosystems.All,
                Checks = run.CheckSnapshot,
                // No run settings, so not staged: the checks run against the disk - which is the point of running them now.
                Settings = null
            }, engine.EngineOptions);

            var (proposed, matches, ecosystems) = FingerprintInputs(run, checks);
            var before = await StateFingerprints.TakeAsync(workspace.RootPath, proposed, matches, ecosystems, ct);
            var at = DateTimeOffset.Now;
            var results = await orchestrator.RunChecksNowAsync(run.RunId, run.TaskId, checks,
                $"now-{at:yyyyMMdd-HHmmss}", new WorkContext(workspace.Id, workspace.Name, null, null, null, [], []), ct);
            // Taken again after: a check that writes (a formatter, a build into a folder git does not ignore) leaves a
            // different state than it found, and a record fingerprinted as it found it would read outdated at once. The
            // record is of the state as the checks left it, and says when that is not the state they found.
            var after = await StateFingerprints.TakeAsync(workspace.RootPath, proposed, matches, ecosystems, ct);

            var record = new CheckRecord(Guid.NewGuid(), run.RunId, who, at,
                checks.Select(c => c.Definition).ToArray(),
                checks.Zip(results, (check, result) => new CheckResultLine(check.Id, result.Outcome,
                    check.Definition.Severity == CheckSeverity.Blocking, result.Command, result.ExitCode, result.Detail)
                {
                    DetailPath = result.Check?.DetailPath
                }).ToArray(),
                after)
            {
                Note = before.Kind != after.Kind || before.Digest != after.Digest
                    ? "The workspace changed while the checks ran - by the checks themselves or by something else - so this "
                      + "record is of the workspace as they left it, not as they found it."
                    : null
            };
            await store.AddCheckRecordAsync(record, ct);
            return new(record, null);
        }
    }

    /// <summary>
    /// The permissions the checks run under: the workspace's level now, narrowed by the run's own ceiling when it was a
    /// template's (its specification keeps the template's narrowed policy) - the lower level, and its asks and denials
    /// added. Without it a command the template denied its run - "this task never runs a shell" - ran here at the slider's
    /// level, wider than the run it checks was ever allowed.
    /// </summary>
    internal static Core.Permissions.PermissionPolicy PolicyNow(ComposedEngine engine, RunRecord run, int autonomy)
    {
        var now = EngineComposition.PolicyFor(engine.Settings, AutonomyTiers.Clamp(autonomy));
        return Core.Templates.ResolvedTaskSpec.Parse(run.Spec) is { Permissions: { } ran }
            ? Core.Templates.TemplateResolution.Narrow(now, new Core.Templates.PermissionCeiling(ran.Level, ran.AskBefore, ran.Deny))
            : now;
    }

    /// <summary>
    /// Whether the workspace is still in the state <paramref name="record"/> was taken of - its fingerprint taken again and
    /// compared (StateFingerprints.MatchesAsync). A match is not proof nothing else changed (StateFingerprint.Caveat).
    /// </summary>
    public static Task<bool> IsCurrentAsync(string root, CheckRecord record, CancellationToken ct)
        => StateFingerprints.MatchesAsync(root, record.Fingerprint, KnownEcosystems.All, ct);

    /// <summary>
    /// What a fingerprint outside git covers: the paths the run proposed (its record's artifacts), the checks' matches, and -
    /// for a check that runs through an ecosystem with no matches of its own - the files that ecosystem owns.
    /// </summary>
    private static (IReadOnlyList<string> Proposed, IReadOnlyList<string> Matches, IReadOnlyList<Core.Builds.IEcosystem> Ecosystems)
        FingerprintInputs(RunRecord run, IReadOnlyList<ResolvedCheck> checks)
    {
        var owners = checks.Where(c => c.RunsThroughEcosystem && c.Definition.Matches is not { Count: > 0 })
            .SelectMany(c => c.Ecosystems ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (run.Artifacts,
            checks.SelectMany(c => c.Definition.Matches ?? []).ToArray(),
            KnownEcosystems.All.Where(e => owners.Contains(e.Name)).ToArray());
    }
}

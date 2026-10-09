namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Execution;
using Enactive.Core.Templates;

/// <summary>
/// What became of each file the run produced, checked by the engine itself when the run ends - for
/// any task that writes files, whatever the task is: a report, a wiki page, a script, source code.
///
/// <para><b>Why.</b> A run that ran no command reported "CHECKS: none - nothing verified this run
/// beyond the model's own report", and that is every wiki task, every report, every piece of
/// writing. Meanwhile its FILES line listed what the run had WRITTEN, not what was there: the list
/// is added to on every write and cleared only on a revert, so a file a later step deleted was still
/// named as a result. Both lines were saying less than the engine could see for itself.</para>
///
/// <para><b>What it will not do.</b> Its results are <see cref="CriterionOrigin.System"/> and not
/// required, so they never hold a run back and never prove one finished - a file being where the run
/// left it says nothing about whether it is right. An empty file is not a failure either: an empty
/// <c>__init__.py</c> is correct, and this engine is not only for one kind of project. And it does
/// not guess why a file is gone. It names the last call that changed it and whether that call was a
/// deletion, and leaves the reading to whoever reads it.</para>
/// </summary>
internal static class ProducedFiles
{
    internal const string Name = "Produced file";

    /// <param name="madeByRun">Files that were not in the workspace before the run - see <see cref="ADraftOfOneStep"/>.</param>
    internal static IReadOnlyList<CriterionResult> Check(
        IReadOnlyList<ArtifactRef> produced,
        IReadOnlyCollection<string> pending,
        string workspaceRoot,
        IReadOnlyList<ExecutedAction> actions,
        IReadOnlySet<string>? madeByRun = null)
    {
        var staged = pending.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<CriterionResult>();

        // A file written five times is one file.
        foreach (var path in produced.Select(a => a.RelativePath)
                     .DistinctBy(Normalize, StringComparer.OrdinalIgnoreCase))
        {
            var key = Normalize(path);
            if (staged.Contains(key))
            {
                results.Add(Result(path, CriterionOutcome.Passed,
                    "staged - held for you to apply, not written to the workspace yet"));
                continue;
            }

            string full;
            try { full = Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path); }
            catch (ArgumentException ex)
            {
                results.Add(Result(path, CriterionOutcome.Unknown, "could not be located: " + ex.Message));
                continue;
            }

            if (File.Exists(full))
            {
                var bytes = new FileInfo(full).Length;
                results.Add(Result(path, CriterionOutcome.Passed,
                    bytes == 0 ? "on disk, empty" : $"on disk, {bytes:N0} bytes"));
                continue;
            }

            if (madeByRun?.Contains(key) == true && ADraftOfOneStep(key, actions))
                continue;
            results.Add(Result(path, CriterionOutcome.Failed, WhyAbsent(key, actions)));
        }
        return results;
    }

    /// <summary>
    /// A file the run made and the same step then took away: the step's own draft, not a result anybody lost. Run 1549ce,
    /// 2026-10-09: a step wrote two test files, found they could not run, deleted them - and the run's report listed both
    /// as FAIL, "removed by delete_file in step 3", beside the work that stood. A file one step made and ANOTHER step took
    /// away is still said: that is the case this check is for - a result removed after it was made.
    /// </summary>
    private static bool ADraftOfOneStep(string key, IReadOnlyList<ExecutedAction> actions)
    {
        var changes = actions
            .Where(a => a.Outcome == ActionOutcome.Succeeded
                        && a.ChangedPaths?.Any(p => string.Equals(Normalize(p), key, StringComparison.OrdinalIgnoreCase)) == true)
            .OrderBy(a => a.At)
            .ToArray();
        // A quick action has no step number: it is one step, and its calls all say so.
        return changes.Length > 0 && changes[^1].FileDeletion && changes.All(a => a.Step == changes[0].Step);
    }

    /// <summary>The last successful call that changed this path, stated as what it was - not a verdict on it.</summary>
    private static string WhyAbsent(string key, IReadOnlyList<ExecutedAction> actions)
    {
        var last = actions
            .Where(a => a.Outcome == ActionOutcome.Succeeded
                        && a.ChangedPaths?.Any(p => string.Equals(Normalize(p), key, StringComparison.OrdinalIgnoreCase)) == true)
            .OrderBy(a => a.At)
            .LastOrDefault();

        if (last is null)
            return "not in the workspace now, and no recorded call changed it";
        var where = last.Step is { } step ? $" in step {step}" : "";
        return last.FileDeletion
            ? $"removed by {last.Tool}{where}, a recorded deletion"
            : $"not in the workspace now; the last call to change it was {last.Tool}{where}, which is not a deletion";
    }

    private static CriterionResult Result(string path, CriterionOutcome outcome, string detail)
        => new(Name, path, Required: false, outcome, ExitCode: null, detail, CriterionOrigin.System);

    internal static string Normalize(string path)
    {
        var slashed = path.Replace('\\', '/');
        return slashed.StartsWith("./", StringComparison.Ordinal) ? slashed[2..] : slashed;
    }
}

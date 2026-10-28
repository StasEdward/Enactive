namespace Enactive.Bench;

/// <summary>
/// Whether this machine can judge a scenario at all, asked before its live run. The truth is checked on the fixture with
/// the reference solution laid over it; where it turns that down, it would turn down a right run alike, and the verdict
/// would be the machine's, not the engine's. On 2026-10-28 the C# scenarios' tests could not start for want of the .NET 8
/// runtime: the truth failed on the solution as on the run, and the bench scored a FALSE PASS - after a live run's tokens
/// were spent on it. Such a scenario is not run, and the summary says why.
/// </summary>
internal static class Readiness
{
    /// <summary>The verdict of a scenario this machine cannot judge. Neither "right" nor "FALSE": no run was made.</summary>
    public const string NotRun = "NOT RUN";

    /// <summary>
    /// The result to record instead of a run where the truth turns down the scenario's reference solution, in a copy
    /// under <paramref name="scratch"/>; null where it holds there, or where the scenario has no solution to try it on.
    /// </summary>
    public static async Task<ScenarioResult?> NotRunAsync(Scenario scenario, string scratch, CancellationToken ct)
    {
        if (await SolutionChecksAsync(scenario, scratch, ct) is not { } checks || Truth.Holds(checks)) return null;
        return new ScenarioResult(scenario.Name, "not run", scenario.Expect.Outcome, false, NotRun, null, 0,
            "the truth turns down the reference solution on this machine, so it cannot judge a run here - "
            + "a tool or runtime the checks need is missing or broken (the checks below are the solution's)",
            null, checks, "");
    }

    /// <summary>The truth on the fixture with the reference solution laid over it; null where there is no solution.</summary>
    public static async Task<IReadOnlyList<CheckResult>?> SolutionChecksAsync(Scenario scenario, string scratch, CancellationToken ct)
    {
        var solution = Path.Combine(scenario.Folder, "solution");
        if (!Directory.Exists(solution)) return null;
        var copy = Path.Combine(scratch, scenario.Name);
        CopyTree(scenario.FixtureFolder, copy);
        CopyTree(solution, copy, over: true);
        return await Truth.CheckAsync(scenario, copy, ct);
    }

    // A fixture's build output and the engine's own state stay behind: a run starts from the sources alone.
    internal static void CopyTree(string from, string to, bool over = false)
    {
        string[] skipped = ["bin", "obj", "TestResults", ".enactive", ".git"];
        bool Kept(string path) => !Path.GetRelativePath(from, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => skipped.Contains(part, StringComparer.OrdinalIgnoreCase));
        if (!over && Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories).Where(Kept))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).Where(Kept))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: over);
    }
}

namespace Enactive.Server.Runs;

using Enactive.Agents;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// What a run in a workspace goes with, as the command bar shows it: the level, the worker and staging saved for the
/// folder (the window's slider, worker box and Stage changes).
///
/// <para>A run reads them from the folder's record as it is composed, not from the page. Another Operator can save a
/// higher level a moment after a page read the old one, and a person who pressed Run under "Suggest" got a run at
/// "Autonomous". The page says what it showed, and a start is refused when that is no longer what is saved
/// (<see cref="ChangedSince"/>).</para>
/// </summary>
internal sealed record BarSettings(int Autonomy, string? WorkerId, bool StageChanges)
{
    /// <summary>
    /// The settings of a folder as the bar shows them: its saved level (none saved, the middle one, as the window's
    /// slider starts), its worker by the rule a run resolves it by - none saved, or one gone, is the first listed, as the
    /// window's box is - and its staging.
    /// </summary>
    public static BarSettings Of(WorkspaceEntry? saved, IReadOnlyList<(string Id, string Name)> workers)
        => new(AutonomyTiers.Clamp(saved?.Autonomy ?? 2), Worker(workers, saved?.WorkerId), saved?.StageChanges ?? false);

    /// <summary>
    /// Why a run started from a page that showed <paramref name="shown"/> must not go with these settings, or null when it
    /// showed them. In words that say what is saved now; the page reads the settings again and shows them.
    /// </summary>
    public string? ChangedSince(BarSettings shown, IReadOnlyList<(string Id, string Name)> workers)
    {
        const string Again = " since this page read it. The bar shows it now; run again to run with it.";
        if (AutonomyTiers.Clamp(shown.Autonomy) != Autonomy)
            return $"The level was changed to “{AutonomyTiers.Describe(Autonomy)}”" + Again;
        // Both sides by the same rule: a page that showed "no worker picked" showed the first one.
        if (!string.Equals(Worker(workers, shown.WorkerId), WorkerId, StringComparison.OrdinalIgnoreCase))
            return $"The worker was changed to “{workers.FirstOrDefault(w => w.Id == WorkerId).Name ?? WorkerId ?? "the default"}”" + Again;
        if (shown.StageChanges != StageChanges)
            return $"Stage changes was turned {(StageChanges ? "on" : "off")}" + Again;
        return null;
    }

    /// <summary>Why <paramref name="shown"/> is not what is saved for <paramref name="root"/> on this engine now, or null.</summary>
    public static string? ChangedFor(ServerEngine engine, string root, BarSettings shown)
    {
        var workers = engine.Workers();
        return Of(engine.Workspaces.Find(root), workers).ChangedSince(shown, workers);
    }

    private static string? Worker(IReadOnlyList<(string Id, string Name)> workers, string? saved)
        => WorkerNames.Resolve(workers, saved) ?? workers.FirstOrDefault().Id;
}

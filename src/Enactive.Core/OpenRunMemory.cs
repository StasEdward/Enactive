namespace Enactive.Core.History;

/// <summary>
/// Which run each workspace was last left showing, for as long as the app is open.
///
/// <para><b>The defect.</b> Switching workspace re-scoped the left column - runs, memory, artifacts,
/// the inbox - because every store is built from the workspace it is asked about. The middle and
/// right columns were built from neither: they held whatever the previous workspace had been
/// showing, and went on holding it. A person looked at one project's execution feed under another
/// project's name, with that project's Retry button live underneath it.</para>
///
/// <para><b>The rule.</b> The middle shows what you left open IN THIS workspace, and nothing else.
/// Nothing was left open - a fresh start, a workspace you have not looked at today, or one you
/// pressed Back in - and the middle is empty. So the app opens empty rather than opening onto last
/// Tuesday's finished run with its Retry buttons armed, and coming back to a project you were
/// reading returns you to what you were reading.</para>
///
/// <para><b>Why not "always show the newest run".</b> The middle column is where work is STARTED.
/// Filling it on every switch puts a record where the tool goes, and the record is one the app
/// chose rather than one anybody asked for; a past run is drawn in the same frame as a live one on
/// purpose, so an automatic one reads as "something just ran". It would also mean a card in the run
/// list is always selected, which takes the meaning out of selecting one.</para>
///
/// <para><b>Why a library and not the window.</b> App.Ui is a WinExe that no test project
/// references. "Nothing from another workspace can be on screen" is not a display detail - it is
/// the whole of what was wrong - and a rule kept in a view model is a rule nothing can check.</para>
///
/// <para><b>Why it does not survive the process.</b> Remembering across restarts would bring back
/// exactly the state this fixes: the app opening onto old work. In-session only, on purpose.</para>
/// </summary>
public sealed class OpenRunMemory
{
    // Paths, and Windows does not distinguish their case. Two spellings of one folder are one
    // workspace everywhere else in the app, and would be two entries here - which is how a person
    // gets an empty middle after switching to a workspace they were just in.
    private readonly Dictionary<string, Guid> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records what a workspace is being left showing. <paramref name="openRun"/> is null when the
    /// middle is on the live run - nothing was open, and nothing is what it must come back to.
    ///
    /// <para>Called at the one moment the answer is known for certain, which is why there is no
    /// Opened/Closed pair to keep in step with the window: a person who opened a run and then
    /// pressed Back has nothing open, and asking at the switch cannot get that wrong.</para>
    /// </summary>
    public void Leaving(string workspaceId, Guid? openRun)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return;

        if (openRun is { } run)
            _open[workspaceId] = run;
        else
            _open.Remove(workspaceId);
    }

    /// <summary>
    /// What this workspace should come back showing, or null for an empty middle.
    ///
    /// <para>A workspace nobody has opened a run in answers null, which is the state the app starts
    /// in and the reason startup needs no special case.</para>
    /// </summary>
    public Guid? Entering(string workspaceId)
        => !string.IsNullOrWhiteSpace(workspaceId) && _open.TryGetValue(workspaceId, out var run)
            ? run
            : null;

    /// <summary>
    /// Forgets a run wherever it was remembered. A run deleted from the history is not something to
    /// return to, and a workspace pointed at a run that no longer exists would come back empty
    /// anyway - after a read of the store that can only fail.
    /// </summary>
    public void Forget(Guid run)
    {
        foreach (var workspace in _open.Where(e => e.Value == run).Select(e => e.Key).ToArray())
            _open.Remove(workspace);
    }
}

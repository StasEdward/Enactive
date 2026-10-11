namespace Enactive.Application;

using Enactive.Core.History;
using Enactive.Workspace;

/// <summary>
/// Runs in flight in this host, and the workspace each belongs to.
///
/// <para>Kept because nothing else knows: a run is written to the store when it ENDS, so between start and end this
/// list is the only record that it is happening at all. The workspace is carried alongside because a background run
/// keeps going after the window has switched to another folder, and it must not appear in another project's column -
/// and the service shows every person the runs of the folder THEY have open.</para>
///
/// <para>Locked, because the service starts and ends runs from more than one thread; the window happens to touch it
/// only from its dispatcher. Which rows a column then shows against the history is <see cref="RunColumn.Live"/>'s.</para>
/// </summary>
internal sealed class LiveRuns
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (LiveRun Run, string Workspace)> _rows = new();

    /// <summary>
    /// Starts a row and returns its handle. The run id is not known yet - the orchestrator mints it and it arrives
    /// with the first event - so the row is identified by its own key until then. See <see cref="LiveRun"/>.
    /// </summary>
    public Guid Begin(string title, string workspace, bool headless)
    {
        var row = Guid.NewGuid();
        lock (_gate)
            _rows[row] = (new LiveRun(row, Guid.Empty, title, headless, 0, 0, DateTimeOffset.Now), WorkspaceRegistry.Normalise(workspace));
        return row;
    }

    /// <summary>
    /// Changes a row, if it is still there, and says whether it was. Silent when it is not: a run that has ended
    /// while an event was in flight is not an error to report.
    /// </summary>
    public bool Update(Guid row, Func<LiveRun, LiveRun> change)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(row, out var held))
                return false;
            _rows[row] = (change(held.Run), held.Workspace);
            return true;
        }
    }

    /// <summary>Removes a row; true when there was one.</summary>
    public bool End(Guid row)
    {
        lock (_gate)
            return _rows.Remove(row);
    }

    /// <summary>The rows of one workspace, however its path was written.</summary>
    public IReadOnlyList<LiveRun> In(string workspace)
    {
        var here = WorkspaceRegistry.Normalise(workspace);
        lock (_gate)
            return _rows.Values
                .Where(e => string.Equals(e.Workspace, here, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Run)
                .ToList();
    }

    public int Count
    {
        get { lock (_gate) return _rows.Count; }
    }
}

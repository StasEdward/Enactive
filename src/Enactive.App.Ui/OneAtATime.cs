namespace Enactive.App.Ui;

/// <summary>
/// A window there may be only one of: asking for it again brings the open one forward.
///
/// <para>Written for Settings. Each settings window edits its own copy of ALL the settings and Save
/// puts that copy in place of the file and of the live ones, so with two open the second to save
/// wrote back everything it had been showing since it opened - and undid, without a word, what the
/// first had just saved. One window at a time means one copy, always taken from the current
/// settings.</para>
///
/// <para>The decision is here, without a window in it, so it can be tested; opening and bringing
/// forward are handed in by the main window, which has the controls. UI thread only.</para>
/// </summary>
internal sealed class OneAtATime<T> where T : class
{
    private T? _open;

    /// <summary>
    /// Opens one with <paramref name="open"/>, or hands the one already open to
    /// <paramref name="bringForward"/>.
    /// </summary>
    public void Request(Func<T> open, Action<T> bringForward)
    {
        if (_open is { } already)
        {
            bringForward(already);
            return;
        }

        // Held only once it exists: a window that threw while being built is not one that is open,
        // and holding on to nothing would refuse every request after it.
        _open = open();
    }

    /// <summary>
    /// Told when a window closes, so the next request opens a fresh one. Only the one being held
    /// counts: a close reported late, for a window already replaced, must not let a second one open.
    /// </summary>
    public void Closed(T window)
    {
        if (ReferenceEquals(_open, window))
            _open = null;
    }
}

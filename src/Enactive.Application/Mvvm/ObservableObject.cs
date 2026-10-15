namespace Enactive.Application.Mvvm;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// The entire MVVM framework this app has: change notification, and nothing else. Hand-written
/// rather than taken from a toolkit on purpose - a view model here stays a plain object with
/// properties and commands, and the application layer takes no UI package (ApplicationLayerTests).
///
/// <para>The notification is raised on the thread that changed the value, and nothing here moves it
/// anywhere else: getting it onto a UI thread is the host's job. The window changes these objects on
/// its dispatcher already; a host that feeds them from the engine's thread must marshal first - a
/// marshalling step in here would tie the shared layer to one host's notion of a UI thread.</para>
/// </summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Assigns and notifies only when the value actually changed, and reports whether it did - so a
    /// setter can chain further work (revalidate, refresh a command) without re-checking.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

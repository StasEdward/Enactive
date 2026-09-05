namespace Enactive.App.Ui.Mvvm;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// The entire MVVM framework this app has: change notification, and nothing else. Hand-written
/// rather than taken from a toolkit on purpose - App.Ui's package list is Avalonia plus what the
/// engine needs, and a view model here stays a plain object with properties and commands.
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

namespace Enactive.Server.Components.Shell;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

/// <summary>
/// The workspace drawer of a narrow screen, shared by the top bar that opens it and the screen that has one (the Runs
/// screen, whose workspace column it is). The layout holds one per page and hands it down as a cascading value: the
/// button and the column are in different components, and each needs to know whether the other is there and open.
///
/// <para>On a wide screen nothing reads it: the button is hidden there (theme.css) and the column stands in its place.</para>
/// </summary>
public sealed class DrawerState : IDisposable
{
    /// <summary>The drawer's element id, which the top bar's button names in aria-controls.</summary>
    public const string Id = "workspace-drawer";

    private readonly NavigationManager _navigation;

    public DrawerState(NavigationManager navigation)
    {
        _navigation = navigation;
        _navigation.LocationChanged += OnLocationChanged;
    }

    /// <summary>A screen with a drawer is shown; the top bar offers its button only then.</summary>
    public bool Offered { get; private set; }

    public bool IsOpen { get; private set; }

    /// <summary>Raised when either changes, for the button and the drawer to draw again.</summary>
    public event Action? Changed;

    /// <summary>Said by the screen that has a drawer, when it is shown and when it goes.</summary>
    public void Offer(bool offered)
    {
        if (Offered == offered)
            return;
        Offered = offered;
        // A drawer left open by a screen that has gone held the page inert with nothing on it to close.
        if (!offered)
            IsOpen = false;
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (!Offered)
            return;
        IsOpen = !IsOpen;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        Changed?.Invoke();
    }

    // Choosing a workspace or a run in the drawer is a link: it closes there, as a phone's drawer does once its choice is
    // made. Open over the screen it led to, the drawer hid the run just chosen.
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => Close();

    public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;
}

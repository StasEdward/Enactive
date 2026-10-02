namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using Enactive.App.Ui.Mvvm;
using Enactive.Remote.Host;

/// <summary>
/// The trusted devices part of the Remote access pane: the list, and Add a device.
///
/// <para>Its own view model, apart from the rest of the settings, so whether Add a device can be pressed
/// is proven without a window: it can only while the computer is connected past Hello, where an
/// invitation can be registered - pressed otherwise, it opened a window that could only say it failed.</para>
///
/// <para>Read only: taking a device's trust away means a new key for every other device, which is its own
/// piece of work.</para>
/// </summary>
internal sealed class RemoteDevicesViewModel : ObservableObject
{
    private bool _paneBusy;
    private bool _canAddDevice;
    private string _problem = string.Empty;

    public RemoteDevicesViewModel() => AddDeviceCommand = new RelayCommand(() => _ = AddDeviceAsync());

    /// <summary>
    /// Whether an invitation can be made now: remote access is connected, past Hello. Set by the window,
    /// because the connection belongs to the running service, not to this pane.
    /// </summary>
    public Func<bool>? CanInvite { get; set; }

    /// <summary>Reads the devices this computer has trusted, revoked ones included. Set by the window.</summary>
    public Func<Task<IReadOnlyList<TrustedDevice>>>? Trusted { get; set; }

    /// <summary>Asks the window to open Add a device. It answers once that window has closed.</summary>
    public event Func<Task>? AddDeviceRequested;

    public RelayCommand AddDeviceCommand { get; }

    public ObservableCollection<TrustedDeviceRow> Devices { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    /// <summary>The list could not be read, in a sentence.</summary>
    public string Problem
    {
        get => _problem;
        private set
        {
            if (Set(ref _problem, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>Something else of the pane - a connect, a check - is running. Set by the settings view model.</summary>
    public bool PaneBusy
    {
        get => _paneBusy;
        set
        {
            if (Set(ref _paneBusy, value))
                RefreshConnection();
        }
    }

    /// <summary>Add a device is pressable: connected, and nothing else of the pane running.</summary>
    public bool CanAddDevice
    {
        get => _canAddDevice;
        private set => Set(ref _canAddDevice, value);
    }

    /// <summary>
    /// Re-reads whether an invitation can be made. Called when the pane is shown and whenever the service
    /// says something new - it connects and drops while the pane is open.
    /// </summary>
    public void RefreshConnection() => CanAddDevice = !PaneBusy && CanInvite?.Invoke() == true;

    /// <summary>Re-reads the trusted devices: when the pane is shown, and after Add a device has closed.</summary>
    public async Task RefreshAsync()
    {
        if (Trusted is not { } trusted)
            return;

        IReadOnlyList<TrustedDevice> devices;
        try
        {
            devices = await trusted();
        }
        catch (Exception failure)
        {
            // A list that cannot be read is said, not shown empty: empty would read as "nothing is
            // trusted", which is the one thing about this list a person must never be misled on.
            Problem = "The trusted devices could not be read: " + failure.Message;
            return;
        }

        Problem = string.Empty;
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(TrustedDeviceRow.From(device));
        }
        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>Opens Add a device, and reads the list again once it has closed: it may have added one.</summary>
    private async Task AddDeviceAsync()
    {
        if (AddDeviceRequested is not { } open)
            return;

        await open();
        await RefreshAsync();
    }
}

/// <summary>One trusted device, as the remote pane lists it.</summary>
internal sealed record TrustedDeviceRow(string Label, string Added, string State, bool IsRevoked)
{
    public static TrustedDeviceRow From(TrustedDevice device) => new(
        device.Label,
        $"added by {device.AddedBy}, {When(device.AddedAt)}",
        device.RevokedAt is { } revoked ? $"revoked {When(revoked)}" : "trusted",
        device.RevokedAt is not null);

    private static string When(DateTimeOffset at)
        => at.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
}

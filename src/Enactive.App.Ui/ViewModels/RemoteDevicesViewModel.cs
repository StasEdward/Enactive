namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using Enactive.App.Ui.Mvvm;
using Enactive.Remote.Host;

/// <summary>
/// The trusted devices part of the Remote access pane: the list, Add a device, and Remove on each device
/// still trusted.
///
/// <para>Its own view model, apart from the rest of the settings, so whether Add a device can be pressed
/// is proven without a window: it can only while the computer is connected past Hello, where an
/// invitation can be registered - pressed otherwise, it opened a window that could only say it failed.</para>
///
/// <para>Removing is asked first, in words that say what it costs: the device reads nothing new, and every
/// other device is sent a new key. A list button pressed by accident must not do that unasked.</para>
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

    /// <summary>Asks the person a yes-or-no question, and answers yes as true. Set by the window.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Removes the device with this id, or throws with a sentence saying why not. Set by the window.</summary>
    public Func<string, Task>? Remove { get; set; }

    /// <summary>What removing a device asks before it does anything.</summary>
    public static string RemoveQuestion(string label)
        => $"{label} will not read anything new. Every other device gets a new key. Continue?";

    /// <summary>Asks the window to open Add a device. It answers once that window has closed.</summary>
    public event Func<Task>? AddDeviceRequested;

    public RelayCommand AddDeviceCommand { get; }

    public ObservableCollection<TrustedDeviceRow> Devices { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    /// <summary>The list could not be read, or a device could not be removed, in a sentence.</summary>
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
            TrustedDeviceRow? row = null;
            row = TrustedDeviceRow.From(device, () => _ = RemoveAsync(row!));
            Devices.Add(row);
        }
        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>
    /// Removes one device, once the person has said yes, and reads the list again so its row says revoked. A
    /// removal that could not be made is said under the list: thrown, it went nowhere from a button.
    /// </summary>
    public async Task RemoveAsync(TrustedDeviceRow row)
    {
        if (!row.CanRemove || Confirm is not { } confirm || Remove is not { } remove)
            return;

        if (!await confirm(RemoveQuestion(row.Label)))
            return;

        string? failed = null;
        try
        {
            await remove(row.DeviceId);
        }
        catch (Exception failure)
        {
            failed = $"{row.Label} could not be removed: {failure.Message}";
        }

        // Read again either way: a removal that failed telling the gateway was still made on this computer.
        await RefreshAsync();
        if (failed is not null)
            Problem = failed;
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

/// <summary>One trusted device, as the remote pane lists it. Only a device still trusted can be removed.</summary>
internal sealed record TrustedDeviceRow(string DeviceId, string Label, string Added, string State, bool IsRevoked)
{
    public bool CanRemove => !IsRevoked;

    public RelayCommand? RemoveCommand { get; private init; }

    public static TrustedDeviceRow From(TrustedDevice device, Action? remove = null) => new(
        device.DeviceId,
        device.Label,
        $"added by {device.AddedBy}, {When(device.AddedAt)}",
        device.RevokedAt is { } revoked ? $"revoked {When(revoked)}" : "trusted",
        device.RevokedAt is not null)
    {
        RemoveCommand = remove is null ? null : new RelayCommand(remove)
    };

    private static string When(DateTimeOffset at)
        => at.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
}

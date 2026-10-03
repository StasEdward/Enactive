using System.ComponentModel;
using Avalonia.Controls;
// SetTextAsync and TryGetTextAsync are extensions in Avalonia 12, not members of IClipboard.
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Enactive.App.Ui.ViewModels;
using Net.Codecrete.QrCodeGenerator;

namespace Enactive.App.Ui;

/// <summary>
/// Add a device (spec §5.3). What it shows and when it settles is <see cref="AddDeviceViewModel"/>'s; what
/// stays here needs the window itself - the QR code drawn from the link, the clipboard, the timer, and
/// closing.
///
/// <para>It makes no gateway call of its own. The invitation is made and answered by the running service,
/// through <see cref="RemoteDevices"/>; this window shows the link and listens.</para>
/// </summary>
internal sealed partial class AddDeviceWindow : Window
{
    /// <summary>How long "was added" stays on screen before the window closes itself.</summary>
    private static readonly TimeSpan ShowSuccessFor = TimeSpan.FromSeconds(2);

    private readonly AddDeviceViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// Taken when the window opens: by the time it has closed, it has no top level to reach the clipboard
    /// through, and a link copied from it may still be there.
    /// </summary>
    private IClipboard? _clipboard;

    public AddDeviceWindow(IDeviceInvitations devices, TimeProvider? clock = null)
    {
        _viewModel = new AddDeviceViewModel(devices, clock ?? TimeProvider.System);
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.PropertyChanged += OnChanged;
        _viewModel.LinkTakenDown += link => _ = ForgetCopiedAsync(link);
        _viewModel.Admitted += () => DispatcherTimer.RunOnce(() =>
        {
            if (IsVisible) Close();
        }, ShowSuccessFor);

        CopyButton.Click += async (_, _) =>
        {
            if (_viewModel.IsLinkShown && _clipboard is not null)
                await _clipboard.SetTextAsync(_viewModel.Link);
        };
        CancelButton.Click += (_, _) => Close();
        _countdown.Tick += (_, _) => _viewModel.Tick();

        Opened += (_, _) =>
        {
            _clipboard = GetTopLevel(this)?.Clipboard;
            _countdown.Start();
            // Not awaited: StartAsync says its own failures in the window and never throws.
            _ = _viewModel.StartAsync(_closing.Token);
        };
        Closed += (_, _) =>
        {
            _countdown.Stop();
            _closing.Cancel();
            _viewModel.Closed();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddDeviceViewModel.Link))
        {
            // Drawn as a vector path, not a bitmap: the library hands over the outline of the dark modules
            // with one unit per module, and Stretch scales it to the box without blurring an edge.
            QrPath.Data = _viewModel.IsLinkShown
                ? Geometry.Parse(QrCode.EncodeText(_viewModel.Link, QrCode.Ecc.Medium).ToGraphicsPath(0))
                : null;
        }
        else if (e.PropertyName == nameof(AddDeviceViewModel.IsSettled) && _viewModel.IsSettled)
        {
            _countdown.Stop();
        }
    }

    /// <summary>
    /// Clears the clipboard if it still holds this link. Only then: whatever the person copied since is
    /// theirs, and a clipboard that cannot be read is left as it is.
    /// </summary>
    private async Task ForgetCopiedAsync(string link)
    {
        if (_clipboard is not { } clipboard) return;

        try
        {
            if (await clipboard.TryGetTextAsync() == link)
                await clipboard.ClearAsync();
        }
        catch (Exception)
        {
            // Another application holding the clipboard open; the link is dead either way.
        }
    }
}

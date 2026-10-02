using System.Security.Cryptography;
using Avalonia.Controls;
// SetTextAsync is an extension in Avalonia 12, not a member of IClipboard.
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;
using Net.Codecrete.QrCodeGenerator;

namespace Enactive.App.Ui;

/// <summary>
/// Add a device (spec §5.3): a one-time invitation link, as text and as a QR code, counting down its ten
/// minutes, and what became of it - the device that answered was added, someone else answered, or nobody
/// did in time.
///
/// <para>It makes no gateway call of its own. The invitation is made and answered by the running service,
/// through <see cref="RemoteDevices"/>; this window shows the link and listens.</para>
///
/// <para><b>The link holds the pairing secret.</b> Whoever has it in the next ten minutes can enroll a
/// device that this computer grants every key to, so it is shown here and nowhere else - not logged, not
/// kept - and its bytes are wiped when the window closes. An invitation the window closes without an
/// answer to is withdrawn: left open, the link would go on counting after the person gave up on it.</para>
/// </summary>
internal sealed partial class AddDeviceWindow : Window
{
    /// <summary>How long "was added" stays on screen before the window closes itself.</summary>
    private static readonly TimeSpan ShowSuccessFor = TimeSpan.FromSeconds(2);

    private readonly RemoteDevices _devices;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    private InviteLink? _link;
    private string _shown = string.Empty;
    private DateTimeOffset _expiresAt;

    /// <summary>The invitation was answered or refused, or never made: there is nothing left to withdraw.</summary>
    private bool _settled;

    public AddDeviceWindow(RemoteDevices devices, TimeProvider? clock = null)
    {
        _devices = devices;
        _clock = clock ?? TimeProvider.System;
        InitializeComponent();

        CopyButton.Click += async (_, _) =>
        {
            if (_shown.Length > 0 && GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(_shown);
        };
        CancelButton.Click += (_, _) => Close();
        _countdown.Tick += (_, _) => Tick();

        devices.Admitted += OnAdmitted;
        devices.Noticed += OnNoticed;
        Opened += async (_, _) => await InviteAsync();
        Closed += (_, _) => OnClosed();
    }

    private async Task InviteAsync()
    {
        StatusText.Text = "Making a link…";

        // Counted from before the request, so the countdown never shows more time than the invitation
        // has: the computer starts its ten minutes when it makes the invitation, not when this hears back.
        _expiresAt = _clock.GetUtcNow() + HostKeyStore.InviteLifetime;

        InviteLink link;
        try
        {
            link = await _devices.InviteAsync(_closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception failure)
        {
            _settled = true;
            StatusText.Text = "No link was made: " + failure.Message;
            CancelButton.Content = "Close";
            return;
        }

        // Closed while the invitation was being made: withdrawn at once, and its secret wiped.
        if (_closing.IsCancellationRequested)
        {
            _ = _devices.WithdrawAsync(link.InviteId);
            CryptographicOperations.ZeroMemory(link.PairingSecret);
            return;
        }

        _link = link;
        _shown = link.Format();
        LinkBox.Text = _shown;
        QrPath.Data = Geometry.Parse(QrCode.EncodeText(_shown, QrCode.Ecc.Medium).ToGraphicsPath(0));
        LinkRow.IsVisible = true;
        QrFrame.IsVisible = true;
        StatusText.Text = "Waiting for the device…";

        Tick();
        _countdown.Start();
    }

    private void Tick()
    {
        var left = _expiresAt - _clock.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            // Ran out: nothing was shared, and the invitation is withdrawn as the window closes.
            Close();
            return;
        }

        CountdownText.Text = $"The link stops working in {(int)left.TotalMinutes}:{left.Seconds:00}.";
    }

    private void OnAdmitted(string label)
    {
        if (_link is null || _settled) return;

        Settle($"{label} was added. It gets this computer's keys in a moment.");
        DispatcherTimer.RunOnce(Close, ShowSuccessFor);
    }

    private void OnNoticed(AdmissionNotice notice)
    {
        if (_link is null || _settled || notice.InviteId != _link.InviteId || !notice.Refused) return;

        // The invitation is closed already; the link is taken off the screen so nobody tries it again.
        Settle(string.Empty);
        ProblemText.Text = notice.Detail;
    }

    /// <summary>The invitation is done with: stop counting, take the link down, say what happened.</summary>
    private void Settle(string status)
    {
        _settled = true;
        _countdown.Stop();
        CountdownText.Text = string.Empty;
        LinkRow.IsVisible = false;
        QrFrame.IsVisible = false;
        StatusText.Text = status;
        CancelButton.Content = "Close";
    }

    private void OnClosed()
    {
        _countdown.Stop();
        _closing.Cancel();
        _devices.Admitted -= OnAdmitted;
        _devices.Noticed -= OnNoticed;

        if (_link is { } link)
        {
            if (!_settled)
                _ = _devices.WithdrawAsync(link.InviteId);

            CryptographicOperations.ZeroMemory(link.PairingSecret);
        }
    }
}

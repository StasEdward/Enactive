namespace Enactive.App.Ui.ViewModels;

using System.Security.Cryptography;
using Enactive.App.Ui.Mvvm;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

/// <summary>
/// The invitations as the Add a device window needs them: made, withdrawn, and told about when answered.
/// An interface so what the window does with an answer can be proven without a window or a gateway.
/// </summary>
internal interface IDeviceInvitations
{
    /// <summary>A device answered an invitation and was admitted. On the UI thread.</summary>
    event Action<AdmittedDevice>? Admitted;

    /// <summary>An answer to an invitation was refused, or set aside. On the UI thread.</summary>
    event Action<AdmissionNotice>? Noticed;

    /// <summary>Opens an invitation. The caller wipes the link's pairing secret once it is no longer shown.</summary>
    Task<InviteLink> InviteAsync(CancellationToken ct);

    /// <summary>Closes an invitation nobody answered. Never throws.</summary>
    Task WithdrawAsync(string inviteId);
}

/// <summary>
/// What the Add a device window shows (spec §5.3): a one-time invitation link counting down its ten
/// minutes, and what became of it - the device that answered was added, someone else answered, or nobody
/// did in time.
///
/// <para><b>Only this window's own invitation settles it.</b> Answers arrive for every invitation this
/// computer has open; settled by another one's, the window took its link down without withdrawing it, and
/// the link stayed answerable for the rest of its ten minutes.</para>
///
/// <para><b>The link holds the pairing secret.</b> Whoever has it in the next ten minutes can enroll a
/// device that this computer grants every key to, so it is shown here and nowhere else, and its bytes are
/// wiped when the window closes. An invitation the window closes without an answer to is withdrawn: left
/// open, the link would go on counting after the person gave up on it.</para>
/// </summary>
internal sealed class AddDeviceViewModel : ObservableObject
{
    public const string Making = "Making a link…";

    public const string Waiting = "Waiting for the device…";

    public const string Expired =
        "Nobody answered the link in time, so nothing was shared. Make a new one to try again.";

    private readonly IDeviceInvitations _devices;
    private readonly TimeProvider _clock;

    private InviteLink? _link;
    private DateTimeOffset _expiresAt;
    private bool _closed;

    private string _status = string.Empty;
    private string _problem = string.Empty;
    private string _shown = string.Empty;
    private string _countdown = string.Empty;
    private bool _settled;

    public AddDeviceViewModel(IDeviceInvitations devices, TimeProvider clock)
    {
        _devices = devices;
        _clock = clock;
        _devices.Admitted += OnAdmitted;
        _devices.Noticed += OnNoticed;
    }

    /// <summary>This window's invitation answered by its device: the window closes after a moment.</summary>
    public event Action? Admitted;

    /// <summary>
    /// The link left the screen. The window clears the clipboard if it still holds it: a link copied and
    /// then not pasted would otherwise sit there after its invitation was settled or withdrawn.
    /// </summary>
    public event Action<string>? LinkTakenDown;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>A refusal, in the window's danger colour.</summary>
    public string Problem
    {
        get => _problem;
        private set => Set(ref _problem, value);
    }

    /// <summary>The link while it is shown; empty before and after.</summary>
    public string Link
    {
        get => _shown;
        private set
        {
            if (Set(ref _shown, value))
                OnPropertyChanged(nameof(IsLinkShown));
        }
    }

    public bool IsLinkShown => Link.Length > 0;

    public string Countdown
    {
        get => _countdown;
        private set => Set(ref _countdown, value);
    }

    /// <summary>The invitation was answered, refused or ran out, or never made: nothing is left to withdraw.</summary>
    public bool IsSettled
    {
        get => _settled;
        private set
        {
            if (Set(ref _settled, value))
                OnPropertyChanged(nameof(CloseLabel));
        }
    }

    public string CloseLabel => IsSettled ? "Close" : "Cancel";

    /// <summary>
    /// Makes the invitation and shows its link. Never throws: it runs from the window's Opened event, where
    /// an exception has nobody to reach but the dispatcher - a link that could not be made is said here.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Status = Making;

        // Counted from before the request, so the countdown never shows more time than the invitation
        // has: the computer starts its ten minutes when it makes the invitation, not when this hears back.
        _expiresAt = _clock.GetUtcNow() + HostKeyStore.InviteLifetime;

        InviteLink link;
        try
        {
            link = await _devices.InviteAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception failure)
        {
            IsSettled = true;
            Status = "No link was made: " + failure.Message;
            return;
        }

        // Closed while the invitation was being made: withdrawn at once, and its secret wiped.
        if (_closed)
        {
            _ = _devices.WithdrawAsync(link.InviteId);
            CryptographicOperations.ZeroMemory(link.PairingSecret);
            return;
        }

        try
        {
            _link = link;
            Link = link.Format();
            Status = Waiting;
            Tick();
        }
        catch (Exception failure)
        {
            // A link that cannot be written out - an address the settings hold that no link may carry -
            // is withdrawn rather than left open with nothing on the screen.
            Settle("No link was made: " + failure.Message);
            _ = _devices.WithdrawAsync(link.InviteId);
        }
    }

    /// <summary>Called every second by the window's timer.</summary>
    public void Tick()
    {
        if (_link is null || IsSettled) return;

        var left = _expiresAt - _clock.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            Settle(Expired);
            _ = _devices.WithdrawAsync(_link.InviteId);
            return;
        }

        Countdown = $"The link stops working in {(int)left.TotalMinutes}:{left.Seconds:00}.";
    }

    /// <summary>The window closed: an unanswered invitation is withdrawn, and its secret wiped.</summary>
    public void Closed()
    {
        _closed = true;
        _devices.Admitted -= OnAdmitted;
        _devices.Noticed -= OnNoticed;

        if (_link is not { } link) return;

        if (!IsSettled)
            _ = _devices.WithdrawAsync(link.InviteId);

        TakeDown();
        CryptographicOperations.ZeroMemory(link.PairingSecret);
    }

    private void OnAdmitted(AdmittedDevice device)
    {
        if (!IsOurs(device.InviteId)) return;

        Settle($"{device.Label} was added. It gets this computer's keys in a moment.");
        Admitted?.Invoke();
    }

    private void OnNoticed(AdmissionNotice notice)
    {
        if (!notice.Refused || !IsOurs(notice.InviteId)) return;

        // The invitation is closed already; the link is taken off the screen so nobody tries it again.
        Settle(string.Empty);
        Problem = notice.Detail;
    }

    private bool IsOurs(string inviteId) => _link is not null && !IsSettled && inviteId == _link.InviteId;

    /// <summary>The invitation is done with: stop counting, take the link down, say what happened.</summary>
    private void Settle(string status)
    {
        IsSettled = true;
        Countdown = string.Empty;
        Status = status;
        TakeDown();
    }

    private void TakeDown()
    {
        if (!IsLinkShown) return;

        var link = Link;
        Link = string.Empty;
        LinkTakenDown?.Invoke(link);
    }
}

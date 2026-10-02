using Avalonia.Threading;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

namespace Enactive.App.Ui;

/// <summary>
/// The settings pane's and the Add a device window's way to the devices this computer trusts and to its
/// invitations - through whichever remote access service is running now.
///
/// <para>Whichever is running now, because the main window replaces the service when the settings
/// change or a code is applied; a window that held the service it opened with would make an invitation
/// on one that had been disposed. Everything that touches the service goes through the main window's
/// gate, the one a restart and a code take, so an invitation is never made while the key store under it
/// is being reset.</para>
///
/// <para>Its events arrive on the UI thread: the service raises them from its own loop, and every
/// listener here is a window.</para>
/// </summary>
internal sealed class RemoteDevices(Func<RemoteAccessService?> current, SemaphoreSlim gate)
{
    /// <summary>A device answered an invitation and was admitted; its label.</summary>
    public event Action<string>? Admitted;

    /// <summary>An answer to an invitation was refused, or set aside.</summary>
    public event Action<AdmissionNotice>? Noticed;

    /// <summary>The service changed what it says, or was replaced: whether an invitation can be made may have too.</summary>
    public event Action? Changed;

    /// <summary>Whether an invitation can be made now: the service is connected, past Hello.</summary>
    public bool CanInvite => current()?.KeyAdministration is not null;

    /// <summary>Every device this computer has trusted, revoked ones included.</summary>
    public async Task<IReadOnlyList<TrustedDevice>> TrustedAsync()
    {
        await gate.WaitAsync();
        try
        {
            return current()?.TrustedDevices() ?? [];
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Opens an invitation. The caller wipes the link's pairing secret once it is no longer shown.</summary>
    public async Task<InviteLink> InviteAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var service = current() ?? throw new InvalidOperationException(RemoteAccessService.NotConnectedForInvite);
            return await service.InviteAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Closes an invitation nobody answered. Never throws: it is called as a window closes, with nobody
    /// to tell, and an invitation that could not be withdrawn stops counting at its ten minutes anyway.
    /// </summary>
    public async Task WithdrawAsync(string inviteId)
    {
        await gate.WaitAsync();
        try
        {
            current()?.WithdrawInvite(inviteId);
        }
        catch (Exception)
        {
            // The service was being replaced, and its store closed under the call: see above.
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Listens to a service the main window just started. A replaced one is disposed and says nothing more.</summary>
    public void Attach(RemoteAccessService service)
    {
        service.DeviceAdmitted += label => Dispatcher.UIThread.Post(() => Admitted?.Invoke(label));
        service.InvitationNoticed += notice => Dispatcher.UIThread.Post(() => Noticed?.Invoke(notice));
        service.Changed += () => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }
}

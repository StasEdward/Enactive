namespace Enactive.Remote.Host;

using System.Security.Cryptography;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// Something an answer to an invitation came to that a person should hear about. <paramref name="Refused"/>
/// is an answer that failed its check - someone other than the invited device - and closed the invitation;
/// otherwise the answer was for an invitation this computer no longer holds, and was set aside.
/// </summary>
public sealed record AdmissionNotice(string InviteId, bool Refused, string Detail);

/// <summary>
/// Adding a browser device from this computer (spec §5.3): an invitation link with a pairing secret in it,
/// and - once the device answers through the gateway - the answer checked with the pair key, the device
/// trusted, and a grant of every key this computer holds queued for it.
///
/// <para>One per connection: it speaks to the gateway through the connection it was made with. The
/// invitations themselves live in the key store, so one made on a connection that dropped is answered on
/// the next.</para>
///
/// <para><b>The answer's MAC is the whole of the check.</b> The gateway relays the device's id and public
/// key, and public keys are public: a gateway that put its own key there would receive every key this
/// computer holds, wrapped for it. Only the device that opened the link has the pairing secret, so only it
/// can make a MAC over its key that verifies here.</para>
/// </summary>
/// <param name="watchSecrets">
/// Shown every secret array answering reads or derives - the invitation's pairing secret and the pair key
/// made from it - as it is made. Only a test passes it, to hold the arrays and check they were wiped.
/// </param>
public sealed class KeyAdministration(
    HostKeyStore keys, IGatewayConnection gateway, TimeProvider clock, Action<byte[]>? watchSecrets = null)
{
    /// <summary>Who decided to trust a device that answered an invitation, as the trusted list records it.</summary>
    public const string AddedBy = "invitation";

    /// <summary>
    /// What a person is told when an answer fails its check. The invitation is closed rather than left
    /// open for another try: an answer with a MAC that does not verify means someone other than the
    /// device holds the link or is changing what it sent, and a second answer would come from them too.
    /// </summary>
    public const string Tampered =
        "Someone other than your new device answered this invitation, so nothing was shared. "
        + "Make a new link and open it only on the device you want to add.";

    /// <summary>What the trusted list calls a device that gave no label.</summary>
    public const string UnnamedDevice = "A device added by invitation";

    /// <summary>
    /// The longest label kept. The label is whatever the device sent, relayed by the gateway; unbounded,
    /// one answer could fill the trusted list and the settings pane with a page of text.
    /// </summary>
    private const int LabelLength = 80;

    /// <summary>A raw uncompressed P-256 point: 0x04, then X and Y.</summary>
    private const int PublicKeyLength = 65;

    /// <summary>Raised for an answer refused or set aside. On the caller's thread.</summary>
    public event Action<AdmissionNotice>? Noticed;

    /// <summary>
    /// Opens an invitation: a new id and pairing secret here, the id registered with the gateway.
    ///
    /// <para>An invitation the gateway refuses - too many open, say - is forgotten before the refusal is
    /// rethrown. Kept, it would be a secret stored for ten minutes that no device could ever answer.</para>
    ///
    /// <para>The returned link holds the pairing secret, and it is the only copy outside the protected
    /// store: the caller shows it, and wipes <see cref="InviteLink.PairingSecret"/> once it is no longer
    /// shown.</para>
    /// </summary>
    public async Task<InviteLink> InviteAsync(Uri gatewayAddress, CancellationToken ct)
    {
        var invite = keys.CreateInvite();
        try
        {
            await gateway.CreateInviteAsync(invite.Id, ct);
        }
        catch
        {
            keys.ForgetInvite(invite.Id);
            CryptographicOperations.ZeroMemory(invite.Secret);
            throw;
        }

        return new InviteLink(gatewayAddress, invite.Id, invite.Secret);
    }

    /// <summary>
    /// Handles every answer the gateway holds for this computer's invitations, and returns the labels of
    /// the devices admitted.
    ///
    /// <para>Each answer closes its invitation, whatever became of it: forgotten here first and then said
    /// answered to the gateway, so a gateway call that fails leaves nothing open to answer again - the
    /// gateway hands the answer over once more, and it is then set aside as one for an invitation this
    /// computer does not hold.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> AnswerEnrollmentsAsync(CancellationToken ct)
    {
        var admitted = new List<string>();

        foreach (var enrollment in await gateway.EnrollmentsAsync(ct))
        {
            if (keys.Invite(enrollment.InviteId) is not { } invite)
            {
                // Expired, withdrawn, already answered, or never this computer's. The secret is gone,
                // so nothing could be checked - and an answer after ten minutes must be useless anyway.
                Notice(new AdmissionNotice(enrollment.InviteId, Refused: false,
                    $"An answer to invitation {enrollment.InviteId} arrived after it had closed, so it was ignored."));
                await gateway.AnsweredInviteAsync(enrollment.InviteId, ct);
                continue;
            }

            var label = Admit(enrollment, invite);
            keys.ForgetInvite(invite.Id);

            if (label is null)
                Notice(new AdmissionNotice(invite.Id, Refused: true, Tampered));
            else
                admitted.Add(label);

            await gateway.AnsweredInviteAsync(invite.Id, ct);
        }

        return admitted;
    }

    /// <summary>
    /// Checks one answer against its invitation and, when it holds, trusts the device and queues a grant
    /// of every epoch to it. The device's label, or null when nothing was admitted.
    /// </summary>
    private string? Admit(EnrollmentView enrollment, PendingInvite invite)
    {
        watchSecrets?.Invoke(invite.Secret);
        var pairKey = RemoteKdf.Derive(invite.Secret, RemoteKdf.Pair);
        watchSecrets?.Invoke(pairKey);

        try
        {
            if (DevicePublic(enrollment.DevicePublic) is not { } devicePublic
                || !Enrollment.Verify(pairKey, invite.Id, enrollment.DeviceId, devicePublic, enrollment.Mac))
                return null;

            if (!Trust(enrollment, devicePublic))
                return null;

            // Every epoch, not only the current one: what this computer sealed before the device was added
            // is the person's to read on it too, and a device that comes back after a revocation needs
            // the epochs made since.
            var signingPublic = keys.SigningPublic;
            foreach (var key in keys.All)
            {
                keys.EnqueueGrant(Grants.CreatePaired(
                    keys.HostId, enrollment.DeviceId, devicePublic, key, invite.Id, pairKey, signingPublic));
            }

            return Label(enrollment.Label);
        }
        finally
        {
            // Either one makes grants the new device accepts as this computer's, and nothing needs them
            // once the grants are made: wiped, rather than left in memory until the collector runs.
            CryptographicOperations.ZeroMemory(pairKey);
            CryptographicOperations.ZeroMemory(invite.Secret);
        }
    }

    /// <summary>
    /// Trusts the answering device, or brings it back with the key it had. False when its id is known here
    /// with another key: a device id is never rebound, so the device that has that id keeps it.
    /// </summary>
    private bool Trust(EnrollmentView enrollment, byte[] devicePublic)
    {
        var known = keys.Trusted.FirstOrDefault(d => d.DeviceId == enrollment.DeviceId);

        if (known is not null && !CryptographicOperations.FixedTimeEquals(known.PublicKey, devicePublic))
            return false;

        if (known is { RevokedAt: not null })
        {
            // The person made the link on this computer, so this is an admission - not an endorsement
            // sealed under an epoch key, which may never bring a revoked device back.
            keys.Retrust(enrollment.DeviceId, devicePublic, AddedBy);
        }
        else if (known is null)
        {
            keys.Trust(new TrustedDevice(enrollment.DeviceId, devicePublic, Label(enrollment.Label), AddedBy,
                clock.GetUtcNow(), RevokedAt: null));
        }

        return true;
    }

    /// <summary>
    /// The device's public key as a P-256 point, or null when it is not one. Refused as an answer that
    /// failed its check: the gateway relayed it, and a key that cannot be granted to is no device's.
    /// </summary>
    private static byte[]? DevicePublic(string encoded)
    {
        try
        {
            var raw = B64.FromUrl(encoded);
            if (raw.Length != PublicKeyLength) return null;
            using (P256.ImportPublic(raw)) { }
            return raw;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The label as the trusted list shows it: on one line, trimmed and bounded. It is text the device
    /// chose, relayed by the gateway, and a line break in it would make one device read as two.
    /// </summary>
    private static string Label(string? label)
    {
        var line = new string([.. (label ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        if (line.Length > LabelLength)
        {
            // Not through the middle of a character outside the BMP: half a surrogate pair shows as a box.
            var cut = char.IsHighSurrogate(line[LabelLength - 1]) ? LabelLength - 1 : LabelLength;
            line = line[..cut].TrimEnd();
        }
        return line.Length == 0 ? UnnamedDevice : line;
    }

    private void Notice(AdmissionNotice notice) => Noticed?.Invoke(notice);
}

namespace Enactive.Remote.Host;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// Something an answer to an invitation came to that a person should hear about. <paramref name="Refused"/>
/// is an answer that failed its check - someone other than the invited device - or that could not be
/// admitted, and closed the invitation; otherwise the answer was for an invitation this computer no longer
/// holds, and was set aside.
/// </summary>
public sealed record AdmissionNotice(string InviteId, bool Refused, string Detail);

/// <summary>
/// A device that answered an invitation and was admitted. The invitation id says which link it answered:
/// a window showing one link must not take another invitation's answer for its own.
/// </summary>
public sealed record AdmittedDevice(string InviteId, string DeviceId, string Label);

/// <summary>
/// A device was removed and a new epoch made: which device, the label it was listed under, the epoch
/// everything is sealed under from now on, and how it was removed - on this computer, or from a browser.
/// </summary>
public sealed record KeyRotated(string DeviceId, string Label, uint Epoch, string Reason);

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
///
/// <para>And keeping the trusted list as the person changes it (spec §5.4): a device removed - here, or from
/// a browser by a sealed command - is distrusted and every remaining device is granted a new epoch, signed
/// with the computer's signing key; a device a trusted browser admitted by its own invitation is trusted
/// here when that browser endorses it.</para>
/// </summary>
public sealed partial class KeyAdministration(HostKeyStore keys, IGatewayConnection gateway, TimeProvider clock)
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

    /// <summary>
    /// What a person is told when admitting an answer failed on this computer's side. Fixed words, not the
    /// failure's own: those can carry the device id, which is text of the gateway's choosing.
    /// </summary>
    public const string CouldNotAdmit =
        "This computer could not add the device that answered this invitation, so nothing was shared. "
        + "Make a new link and try again.";

    /// <summary>Who decided to trust a device a trusted browser endorsed, as the trusted list records it.</summary>
    public const string EndorsedBy = "a trusted browser";

    /// <summary>How a device removed with the desktop's own list was removed.</summary>
    public const string RemovedHere = "removed on this computer";

    /// <summary>How a device removed by a browser's sealed command was removed.</summary>
    public const string RemovedFromBrowser = "removed from the browser";

    /// <summary>Why a device command is refused on a computer with no trusted list to change.</summary>
    public const string CannotManageDevices = "this computer cannot manage devices";

    /// <summary>Why a removal naming a device this computer never trusted changes nothing.</summary>
    public const string UnknownDevice = "it names a device this computer has never trusted";

    /// <summary>
    /// Why an endorsement of a removed device is refused. An endorsement is sealed under an epoch key, and
    /// the removed device may hold one: it could vouch for itself and be granted every epoch made since.
    /// Bringing a device back is an admission, from this computer, with a secret the gateway never sees.
    /// </summary>
    public const string RemovedCannotBeEndorsed =
        "a removed device cannot be endorsed back; add it again from this computer";

    /// <summary>
    /// Why an endorsement naming a trusted id with another key is refused: bound to the new key, the id's
    /// next grant would go to whoever holds that key, under the name of the device the person trusted.
    /// </summary>
    private const string AnotherKey = "it names a device this computer trusts with another key";

    private const string NotADeviceKey = "the key it names is not a device's";

    private const string NoDevice = "it names no device";

    /// <summary>
    /// One removal or endorsement at a time, for the whole process: there is one key store. A removal from
    /// the desktop and one from a browser at once each read the trusted list and rotated, and an endorsement
    /// trusted between a removal's reading of the list and its grants missed the new epoch - a device
    /// trusted here that could read nothing sealed from then on, until the next removal.
    /// </summary>
    private static readonly Lock TrustedListGate = new();

    /// <summary>What the trusted list calls a device that gave no label.</summary>
    public const string UnnamedDevice = "A device added by invitation";

    /// <summary>
    /// The longest label kept. The label is whatever the device sent, relayed by the gateway; unbounded,
    /// one answer could fill the trusted list and the settings pane with a page of text.
    /// </summary>
    private const int LabelLength = 80;

    /// <summary>A raw uncompressed P-256 point: 0x04, then X and Y.</summary>
    private const int PublicKeyLength = 65;

    private readonly Action<byte[]>? _watchSecrets;

    /// <param name="watchSecrets">
    /// Shown every secret array answering reads or derives - the invitation's pairing secret and the pair
    /// key made from it - as it is made. Only a test passes it, to hold the arrays and check they were wiped.
    /// </param>
    internal KeyAdministration(
        HostKeyStore keys, IGatewayConnection gateway, TimeProvider clock, Action<byte[]> watchSecrets)
        : this(keys, gateway, clock)
        => _watchSecrets = watchSecrets;

    /// <summary>Raised for an answer refused or set aside. On the caller's thread.</summary>
    public event Action<AdmissionNotice>? Noticed;

    /// <summary>Raised once a device was removed and the new epoch granted to the rest. On the caller's thread.</summary>
    public event Action<KeyRotated>? Rotated;

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
    /// Handles every answer the gateway holds for this computer's invitations, and returns the devices
    /// admitted.
    ///
    /// <para>Each answer closes its invitation, whatever became of it: forgotten here first and then said
    /// answered to the gateway, so a gateway call that fails leaves nothing open to answer again - the
    /// gateway hands the answer over once more, and it is then set aside as one for an invitation this
    /// computer does not hold.</para>
    ///
    /// <para>Anything that goes wrong admitting one answer refuses that answer only. Thrown out of the
    /// batch, it took the connection down with the invitation still open, and was met again every turn for
    /// ten minutes - a device revoked between the trusted list being read and its grants being queued did
    /// exactly that, since a grant to a revoked device is refused.</para>
    /// </summary>
    public async Task<IReadOnlyList<AdmittedDevice>> AnswerEnrollmentsAsync(CancellationToken ct)
    {
        var admitted = new List<AdmittedDevice>();

        foreach (var enrollment in await gateway.EnrollmentsAsync(ct))
        {
            if (!IsInviteId(enrollment.InviteId))
            {
                // Not an id this computer could have made, so not one of its invitations - and text of the
                // gateway's choosing, line breaks and all, so it is neither logged nor sent back.
                Notice(new AdmissionNotice(string.Empty, Refused: false,
                    "An answer to invitation <not an id> was ignored."));
                continue;
            }

            if (keys.Invite(enrollment.InviteId) is not { } invite)
            {
                // Expired, withdrawn, already answered, or never this computer's. The secret is gone,
                // so nothing could be checked - and an answer after ten minutes must be useless anyway.
                Notice(new AdmissionNotice(enrollment.InviteId, Refused: false,
                    $"An answer to invitation {enrollment.InviteId} arrived after it had closed, so it was ignored."));
                await gateway.AnsweredInviteAsync(enrollment.InviteId, ct);
                continue;
            }

            string? refusal;
            try
            {
                var device = Admit(enrollment, invite);
                if (device is not null) admitted.Add(device);
                refusal = device is null ? Tampered : null;
            }
            catch (Exception)
            {
                refusal = CouldNotAdmit;
            }

            keys.ForgetInvite(invite.Id);
            if (refusal is not null)
                Notice(new AdmissionNotice(invite.Id, Refused: true, refusal));

            await gateway.AnsweredInviteAsync(invite.Id, ct);
        }

        return admitted;
    }

    /// <summary>
    /// Takes a device's trust away (spec §5.4): it is distrusted - its grants still queued go with it - a new
    /// epoch is made, and the new key is granted to every device that remains, signed with the computer's
    /// signing key. Everything sealed from then on is under the new epoch, which the removed device never
    /// receives; what it already read it keeps.
    ///
    /// <para>The grants are signed, not authenticated with any epoch key: the removed device holds every one
    /// of those, and with the gateway's help it could otherwise hand the others a next key of its choosing.
    /// More than the gateway takes in one call go out in several: the delivery loop splits them.</para>
    ///
    /// <para>A device already removed is left as it is - removed by a second browser, or here after the
    /// browser - with no second epoch: nothing more is kept from it, and every device would be sent a grant
    /// for nothing.</para>
    /// </summary>
    /// <exception cref="CommandRefusedException">This computer never trusted the device.</exception>
    public Task RevokeAsync(string deviceId, string reason, CancellationToken ct)
    {
        KeyRotated rotated;
        lock (TrustedListGate)
        {
            var device = keys.Trusted.FirstOrDefault(d => d.DeviceId == deviceId)
                ?? throw new CommandRefusedException(UnknownDevice);

            if (device.RevokedAt is not null)
                return Task.CompletedTask;

            // Distrusted before the new key exists: a key made first and then lost to a crash before the
            // device was distrusted would be granted to it, as a device still trusted, at the next removal.
            keys.Distrust(deviceId);
            var next = keys.Rotate();

            foreach (var remaining in keys.Live)
            {
                keys.EnqueueGrant(Grants.CreateSigned(
                    keys.HostId, remaining.DeviceId, remaining.PublicKey, next, keys.Signer));
            }

            rotated = new KeyRotated(deviceId, device.Label, next.Epoch, reason);
        }

        Rotated?.Invoke(rotated);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A removal made with this computer's own list: the removal above, and then the gateway asked to stop
    /// serving the device - its calls and the grants it holds there. This computer's part comes first: with
    /// the gateway out of reach, the device already reads nothing new. A removal a browser sent does not
    /// come here, since that browser removed the device at the gateway before it sent the command.
    /// </summary>
    public async Task RevokeHereAsync(string deviceId, CancellationToken ct)
    {
        await RevokeAsync(deviceId, RemovedHere, ct);
        await gateway.RevokeDeviceAsync(deviceId, ct);
    }

    /// <summary>
    /// Trusts a device a trusted browser admitted by its own invitation and vouched for (spec §5.3, step 3),
    /// so this computer's next removal grants it the new key. Nothing is granted to it now: the browser that
    /// admitted it granted it every key that browser holds.
    ///
    /// <para>The seal is the whole of the endorsement's authenticity - only a holder of this computer's
    /// current key can make one - and a holder may be a device about to be removed. So this only ever adds
    /// a device never seen here: a device trusted with that very key is left as it is, one trusted with
    /// another key is refused, and so is one this computer removed. <see cref="HostKeyStore.Retrust"/> is
    /// never called from here.</para>
    /// </summary>
    /// <exception cref="CommandRefusedException">The endorsement would rebind or bring back a device, or names none.</exception>
    public Task EndorseAsync(DeviceEndorsement endorsement, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(endorsement.DeviceId))
            throw new CommandRefusedException(NoDevice);

        var devicePublic = DevicePublic(endorsement.DevicePublic)
            ?? throw new CommandRefusedException(NotADeviceKey);

        lock (TrustedListGate)
        {
            var known = keys.Trusted.FirstOrDefault(d => d.DeviceId == endorsement.DeviceId);

            if (known is { RevokedAt: not null })
                throw new CommandRefusedException(RemovedCannotBeEndorsed);

            if (known is not null)
            {
                return CryptographicOperations.FixedTimeEquals(known.PublicKey, devicePublic)
                    ? Task.CompletedTask
                    : throw new CommandRefusedException(AnotherKey);
            }

            keys.Trust(new TrustedDevice(endorsement.DeviceId, devicePublic, CleanLabel(endorsement.Label),
                EndorsedBy, clock.GetUtcNow(), RevokedAt: null));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks one answer against its invitation and, when it holds, trusts the device and queues a grant
    /// of every epoch to it. The device, or null when the answer failed its check.
    /// </summary>
    private AdmittedDevice? Admit(EnrollmentView enrollment, PendingInvite invite)
    {
        byte[]? pairKey = null;
        try
        {
            _watchSecrets?.Invoke(invite.Secret);
            pairKey = RemoteKdf.Derive(invite.Secret, RemoteKdf.Pair);
            _watchSecrets?.Invoke(pairKey);

            if (string.IsNullOrEmpty(enrollment.DeviceId)
                || DevicePublic(enrollment.DevicePublic) is not { } devicePublic
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

            return new AdmittedDevice(invite.Id, enrollment.DeviceId, CleanLabel(enrollment.Label));
        }
        finally
        {
            // Either one makes grants the new device accepts as this computer's, and nothing needs them
            // once the grants are made: wiped, rather than left in memory until the collector runs.
            if (pairKey is not null) CryptographicOperations.ZeroMemory(pairKey);
            CryptographicOperations.ZeroMemory(invite.Secret);
        }
    }

    /// <summary>
    /// Trusts the answering device, or brings it back with the key it had. False when its id is known here
    /// with another key: a device id is never rebound, so the device that has that id keeps it. A device
    /// live with this very key is left as it is - the computer coming back after a crash between trusting
    /// it and closing the invitation - and is granted again.
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
            keys.Trust(new TrustedDevice(enrollment.DeviceId, devicePublic, CleanLabel(enrollment.Label), AddedBy,
                clock.GetUtcNow(), RevokedAt: null));
        }

        return true;
    }

    /// <summary>
    /// The device's public key as a P-256 point, or null when it is not one. Refused as an answer that
    /// failed its check: the gateway relayed it, and a key that cannot be granted to is no device's.
    /// </summary>
    private static byte[]? DevicePublic(string? encoded)
    {
        try
        {
            var raw = B64.FromUrl(encoded ?? string.Empty);
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
    /// The label as the trusted list, the Add a device window and the log show it: one plain line, trimmed,
    /// at most 80 characters. It is text the device chose, relayed by the gateway, so anything that would
    /// break the line (control characters; U+2028 and U+2029, which Avalonia lays out as line breaks),
    /// reorder it (bidirectional overrides such as U+202E, which turn a name around) or hide in it
    /// (zero-width and other format characters, half of a surrogate pair) is replaced with a space.
    /// </summary>
    public static string CleanLabel(string? label)
    {
        var text = label ?? string.Empty;
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                clean.Append(c).Append(text[++i]);
                continue;
            }

            clean.Append(Unsafe(c) ? ' ' : c);
        }

        var line = clean.ToString().Trim();
        if (line.Length > LabelLength)
        {
            // Not through the middle of a character outside the BMP: half a surrogate pair shows as a box.
            var cut = char.IsHighSurrogate(line[LabelLength - 1]) ? LabelLength - 1 : LabelLength;
            line = line[..cut].TrimEnd();
        }
        return line.Length == 0 ? UnnamedDevice : line;
    }

    // Surrogate here means a half on its own: whole pairs are kept before this is asked.
    private static bool Unsafe(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate;

    /// <summary>An invitation id as this computer makes them: 32 lowercase hex characters.</summary>
    private static bool IsInviteId(string? id) => id is not null && InviteIdShape().IsMatch(id);

    // \z, not $: $ also matches before a final line break, and "<id>\n" is not an id.
    [GeneratedRegex(@"^[0-9a-f]{32}\z")]
    private static partial Regex InviteIdShape();

    private void Notice(AdmissionNotice notice) => Noticed?.Invoke(notice);
}

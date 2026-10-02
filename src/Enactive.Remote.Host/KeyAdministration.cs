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

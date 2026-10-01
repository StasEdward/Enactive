namespace Enactive.Remote.Contracts.Crypto;

using System.Globalization;
using System.Security.Cryptography;

/// <summary>A host key wrapped for one device (spec §4). Every field is base64url or plain text.</summary>
public sealed record KeyGrant(
    string HostId, string DeviceId, uint Epoch, string EphemeralPublic, string Nonce, string Ciphertext,
    string AuthBy, string Mac);

/// <summary>
/// The wrap is ECDH with a fresh ephemeral key to the device's public key, HKDF to a key-encryption key,
/// AES-GCM over the 32-byte host key. That alone is not enough: the device's public key is public, so
/// anyone - the gateway included - can wrap a key of their own to it. What makes a grant trustworthy is
/// the MAC, with a key the gateway never has: the pairing secret from an out-of-band code, or the
/// previous epoch's grant-auth key on rotation. The MAC is checked before anything is decrypted.
/// </summary>
public static class Grants
{
    private const string WrapVersion = "enactive-grant-v1";
    private const string MacVersion = "enactive-grant-mac-v1";
    private const int NonceLength = 12;
    private const int SecretLength = 32;
    private const int TagLength = 16;

    public static string AuthByPairing(string pairingId) => $"pair:{pairingId}";
    public static string AuthByEpoch(uint previous) => $"epoch:{previous.ToString(CultureInfo.InvariantCulture)}";

    public static KeyGrant Create(string hostId, string deviceId, ReadOnlySpan<byte> devicePublic, HostKey key,
        string authBy, ReadOnlySpan<byte> authKey, ECDiffieHellman? ephemeral = null, ReadOnlySpan<byte> nonce = default)
    {
        var owned = ephemeral is null;
        var e = ephemeral ?? P256.Generate();
        try
        {
            var ePub = B64.Url(P256.PublicRaw(e));
            var dPub = B64.Url(devicePublic);
            var info = Canonical.Bytes(WrapVersion, hostId, deviceId, Epoch(key.Epoch), ePub, dPub);
            var kek = RemoteKdf.Derive(P256.Agree(e, devicePublic), info);
            var n = nonce.IsEmpty ? RandomNumberGenerator.GetBytes(NonceLength) : nonce.ToArray();
            var ct = new byte[SecretLength + TagLength];
            using (var aes = new AesGcm(kek, TagLength))
                aes.Encrypt(n, key.Secret.Span, ct.AsSpan(0, SecretLength), ct.AsSpan(SecretLength, TagLength), info);
            var nonceText = B64.Url(n);
            var ctText = B64.Url(ct);
            var mac = B64.Url(HMACSHA256.HashData(authKey,
                Canonical.Bytes(MacVersion, hostId, deviceId, Epoch(key.Epoch), ePub, dPub, nonceText, ctText, authBy)));
            return new KeyGrant(hostId, deviceId, key.Epoch, ePub, nonceText, ctText, authBy, mac);
        }
        finally
        {
            if (owned) e.Dispose();
        }
    }

    /// <summary>
    /// Every way a grant can be wrong - not authenticated, not for this device, a field that is not
    /// base64url, a length the cipher does not take, a key that is not a point on the curve - ends as an
    /// EnvelopeException. The device code reads that as "this grant is not for me / not trusted"; any
    /// other exception type escaping from here would be taken for a bug in the device, not a bad grant.
    /// </summary>
    public static HostKey Open(KeyGrant grant, ECDiffieHellman device, ReadOnlySpan<byte> authKey)
    {
        try
        {
            var dPub = B64.Url(P256.PublicRaw(device));
            var expected = HMACSHA256.HashData(authKey, Canonical.Bytes(MacVersion, grant.HostId, grant.DeviceId,
                Epoch(grant.Epoch), grant.EphemeralPublic, dPub, grant.Nonce, grant.Ciphertext, grant.AuthBy));
            if (!CryptographicOperations.FixedTimeEquals(expected, Decode(grant.Mac)))
                throw new EnvelopeException("The grant is not authenticated by a key this device trusts.");
            var info = Canonical.Bytes(WrapVersion, grant.HostId, grant.DeviceId, Epoch(grant.Epoch), grant.EphemeralPublic, dPub);
            var kek = RemoteKdf.Derive(P256.Agree(device, Decode(grant.EphemeralPublic)), info);
            var ct = Decode(grant.Ciphertext);
            if (ct.Length != SecretLength + TagLength) throw new EnvelopeException("A grant wraps 32 bytes.");
            var nonce = Decode(grant.Nonce);
            // AesGcm throws ArgumentException for any other nonce length, and a holder of the pairing key can sign one.
            if (nonce.Length != NonceLength) throw new EnvelopeException("A grant nonce is 12 bytes.");
            var secret = new byte[SecretLength];
            using var aes = new AesGcm(kek, TagLength);
            aes.Decrypt(nonce, ct.AsSpan(0, SecretLength), ct.AsSpan(SecretLength, TagLength), secret, info);
            return HostKey.From(grant.Epoch, secret);
        }
        catch (EnvelopeException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            // Includes a failed GCM tag (the grant was made for another device) and an ephemeral key that does not import.
            throw new EnvelopeException("The grant was not made for this device or is malformed: " + ex.Message);
        }
    }

    /// <summary>A field missing from a grant read off the wire is as untrustworthy as one that is not base64url.</summary>
    private static byte[] Decode(string? text)
        => text is null ? throw new EnvelopeException("A grant field is missing.") : B64.FromUrl(text);

    private static string Epoch(uint epoch) => epoch.ToString(CultureInfo.InvariantCulture);
}

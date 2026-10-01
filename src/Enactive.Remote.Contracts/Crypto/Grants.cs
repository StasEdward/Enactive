namespace Enactive.Remote.Contracts.Crypto;

using System.Globalization;
using System.Security.Cryptography;

/// <summary>
/// A host key wrapped for one device (spec §4). Every field is base64url or plain text. HostSigningPublic is
/// the computer's ECDSA public key, carried by every grant so a device learns it from its first one; Mac is
/// an HMAC for a paired grant and an ECDSA signature for one the computer signed, as AuthBy says.
/// </summary>
public sealed record KeyGrant(
    string HostId, string DeviceId, uint Epoch, string EphemeralPublic, string Nonce, string Ciphertext,
    string AuthBy, string HostSigningPublic, string Mac);

/// <summary>
/// The wrap is ECDH with a fresh ephemeral key to the device's public key, HKDF to a key-encryption key,
/// AES-GCM over the 32-byte host key. That alone is not enough: the device's public key is public, so
/// anyone - the gateway included - can wrap a key of their own to it. What makes a grant trustworthy is
/// its authentication, by something the gateway never has: the pair key from an out-of-band code (pairing,
/// invitation), or the computer's signing key (rotation). Rotation grants were once authenticated with a key
/// derived from the previous epoch key, but the device being revoked holds that key, so with the gateway's
/// help it could hand everyone else a next key of its own choosing; no epoch key can authenticate a grant now.
/// The MAC or signature is checked before anything is decrypted.
/// </summary>
public static class Grants
{
    private const string WrapVersion = "enactive-grant-v1";
    private const string MacVersion = "enactive-grant-mac-v1";
    private const string SignatureVersion = "enactive-grant-sig-v1";
    private const string PairingPrefix = "pair:";
    private const int NonceLength = 12;
    private const int SecretLength = 32;
    private const int TagLength = 16;
    // ECDSA over P-256 in IEEE P1363 form, r and s of 32 bytes each: the form .NET's SignData and WebCrypto both produce.
    private const int SignatureLength = 64;

    public const string AuthByHost = "host";

    public static string AuthByPairing(string pairingId) => PairingPrefix + pairingId;

    /// <summary>A grant answering a pairing or an invitation, authenticated with the pair key both ends derived from the out-of-band secret.</summary>
    public static KeyGrant CreatePaired(string hostId, string deviceId, ReadOnlySpan<byte> devicePublic, HostKey key,
        string pairingId, ReadOnlySpan<byte> pairKey, ReadOnlySpan<byte> hostSigningPublic,
        ECDiffieHellman? ephemeral = null, ReadOnlySpan<byte> nonce = default)
    {
        var grant = Wrap(hostId, deviceId, devicePublic, key, AuthByPairing(pairingId), hostSigningPublic, ephemeral, nonce);
        var mac = HMACSHA256.HashData(pairKey, AuthenticatedText(MacVersion, grant, B64.Url(devicePublic)));
        return grant with { Mac = B64.Url(mac) };
    }

    /// <summary>A rotation grant, made only by the computer and signed with its signing key.</summary>
    public static KeyGrant CreateSigned(string hostId, string deviceId, ReadOnlySpan<byte> devicePublic, HostKey key,
        ECDsa hostSigner, ECDiffieHellman? ephemeral = null, ReadOnlySpan<byte> nonce = default)
    {
        var grant = Wrap(hostId, deviceId, devicePublic, key, AuthByHost, P256.SigningPublicRaw(hostSigner), ephemeral, nonce);
        var signature = hostSigner.SignData(AuthenticatedText(SignatureVersion, grant, B64.Url(devicePublic)), HashAlgorithmName.SHA256);
        // SignData's default format is P1363; a DER signature (another default, another runtime) would not verify in a browser.
        if (signature.Length != SignatureLength) throw new CryptographicException("A grant signature is 64 bytes (IEEE P1363).");
        return grant with { Mac = B64.Url(signature) };
    }

    /// <summary>
    /// Opens a grant made for this device and returns the host key with the computer's signing public key.
    /// A paired grant is checked with <paramref name="pairKey"/>; a grant signed by the computer with
    /// <paramref name="pinnedHostSigningPublic"/>, the key this device pinned for that computer, and is refused
    /// when nothing is pinned - the only other key to check it with is the one in the grant, which anyone can
    /// put there. When a key is pinned, a grant that carries another one is refused in both modes.
    ///
    /// Pinning is the caller's job: it stores the returned key for the grant's computer at its first verified
    /// grant and passes it from then on. So are the checks that the grant's HostId, DeviceId and Epoch are the
    /// ones it asked for: this method proves who made the grant and for which device key, not that it answers
    /// the caller's question.
    ///
    /// Every way a grant can be wrong - not authenticated, not for this device, a field that is not
    /// base64url, a length the cipher does not take, a key that is not a point on the curve - ends as an
    /// EnvelopeException. The device code reads that as "this grant is not for me / not trusted"; any
    /// other exception type escaping from here would be taken for a bug in the device, not a bad grant.
    /// </summary>
    public static (HostKey Key, byte[] HostSigningPublic) Open(KeyGrant grant, ECDiffieHellman device,
        ReadOnlySpan<byte> pairKey, byte[]? pinnedHostSigningPublic)
    {
        try
        {
            var dPub = B64.Url(P256.PublicRaw(device));
            var authBy = grant.AuthBy ?? throw new EnvelopeException("A grant field is missing.");
            var carried = grant.HostSigningPublic ?? throw new EnvelopeException("A grant field is missing.");
            // Base64url has one spelling per byte string, so comparing the text compares the keys without decoding an unauthenticated field.
            if (pinnedHostSigningPublic is not null && !string.Equals(B64.Url(pinnedHostSigningPublic), carried, StringComparison.Ordinal))
                throw new EnvelopeException("The grant carries another signing key than the one pinned for this computer.");

            if (authBy == AuthByHost)
            {
                if (pinnedHostSigningPublic is null)
                    throw new EnvelopeException("A grant signed by the computer cannot be checked before the computer's signing key is pinned.");
                var signature = Decode(grant.Mac);
                if (signature.Length != SignatureLength) throw new EnvelopeException("A grant signature is 64 bytes.");
                using var verifier = P256.ImportSigningPublic(pinnedHostSigningPublic);
                if (!verifier.VerifyData(AuthenticatedText(SignatureVersion, grant, dPub), signature, HashAlgorithmName.SHA256))
                    throw new EnvelopeException("The grant is not signed by the computer this device pinned.");
            }
            else if (authBy.StartsWith(PairingPrefix, StringComparison.Ordinal))
            {
                // An empty key would still compute an HMAC; a device with no pairing in progress has nothing to check a paired grant with.
                if (pairKey.IsEmpty) throw new EnvelopeException("A paired grant arrived but this device has no pairing in progress.");
                var expected = HMACSHA256.HashData(pairKey, AuthenticatedText(MacVersion, grant, dPub));
                if (!CryptographicOperations.FixedTimeEquals(expected, Decode(grant.Mac)))
                    throw new EnvelopeException("The grant is not authenticated by a key this device trusts.");
                // The caller pins this key; one that is not a point would make every later rotation grant unverifiable.
                using (P256.ImportSigningPublic(Decode(carried))) { }
            }
            else
            {
                throw new EnvelopeException("The grant names an authentication this device does not know.");
            }
            var hostSigningPublic = Decode(carried);

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
            return (HostKey.From(grant.Epoch, secret), hostSigningPublic);
        }
        catch (EnvelopeException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            // Includes a failed GCM tag (the grant was made for another device) and a key that does not import.
            throw new EnvelopeException("The grant was not made for this device or is malformed: " + ex.Message);
        }
    }

    private static KeyGrant Wrap(string hostId, string deviceId, ReadOnlySpan<byte> devicePublic, HostKey key, string authBy,
        ReadOnlySpan<byte> hostSigningPublic, ECDiffieHellman? ephemeral, ReadOnlySpan<byte> nonce)
    {
        // Refused here rather than discovered by the device: a grant naming a key that is not a point could never be pinned.
        using (P256.ImportSigningPublic(hostSigningPublic)) { }
        var owned = ephemeral is null;
        var e = ephemeral ?? P256.Generate();
        try
        {
            var ePub = B64.Url(P256.PublicRaw(e));
            var info = Canonical.Bytes(WrapVersion, hostId, deviceId, Epoch(key.Epoch), ePub, B64.Url(devicePublic));
            var kek = RemoteKdf.Derive(P256.Agree(e, devicePublic), info);
            var n = nonce.IsEmpty ? RandomNumberGenerator.GetBytes(NonceLength) : nonce.ToArray();
            var ct = new byte[SecretLength + TagLength];
            using (var aes = new AesGcm(kek, TagLength))
                aes.Encrypt(n, key.Secret.Span, ct.AsSpan(0, SecretLength), ct.AsSpan(SecretLength, TagLength), info);
            return new KeyGrant(hostId, deviceId, key.Epoch, ePub, B64.Url(n), B64.Url(ct), authBy, B64.Url(hostSigningPublic), Mac: "");
        }
        finally
        {
            if (owned) e.Dispose();
        }
    }

    /// <summary>
    /// What the MAC or the signature covers: every field of the grant but the MAC itself, with the device key
    /// the caller holds in place of one read from the wire. The version differs between the two so an HMAC text
    /// can never be presented as a signed one, or the reverse.
    /// </summary>
    private static byte[] AuthenticatedText(string version, KeyGrant grant, string devicePublic)
        => Canonical.Bytes(version, grant.HostId, grant.DeviceId, Epoch(grant.Epoch), grant.EphemeralPublic, devicePublic,
            grant.Nonce, grant.Ciphertext, grant.AuthBy, grant.HostSigningPublic);

    /// <summary>A field missing from a grant read off the wire is as untrustworthy as one that is not base64url.</summary>
    private static byte[] Decode(string? text)
        => text is null ? throw new EnvelopeException("A grant field is missing.") : B64.FromUrl(text);

    private static string Epoch(uint epoch) => epoch.ToString(CultureInfo.InvariantCulture);
}

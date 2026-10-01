namespace Enactive.Remote.Contracts.Crypto;

using System.Buffers.Text;
using System.Security.Cryptography;

/// <summary>
/// P-256 rather than X25519 because both ends must have it without a third-party library: .NET has
/// ECDH on NIST curves in the BCL and WebCrypto has it in every browser (spec D5). Public keys travel
/// as the raw uncompressed point - what WebCrypto exports with "raw" - so neither side converts.
/// </summary>
public static class P256
{
    public static ECDiffieHellman Generate() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] PublicRaw(ECDiffieHellman key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var raw = new byte[65];
        raw[0] = 4;
        q.X!.CopyTo(raw, 1);
        q.Y!.CopyTo(raw, 33);
        return raw;
    }

    /// <summary>The platform (on Windows, CNG) validates that the point is on the curve; an invalid point throws PlatformNotSupportedException, which is converted to CryptographicException so callers see one exception family.</summary>
    public static ECDiffieHellman ImportPublic(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != 65 || raw[0] != 4)
            throw new CryptographicException("A P-256 public key is 65 bytes beginning with 0x04.");
        try
        {
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = raw[1..33].ToArray(), Y = raw[33..65].ToArray() }
            });
        }
        catch (PlatformNotSupportedException ex)
        {
            // On Windows (CNG), a point that is not on the curve surfaces as PlatformNotSupportedException ("The specified curve 'nistP256' or its parameters are not valid for this platform"), not CryptographicException; convert for consistent caller experience.
            throw new CryptographicException("A P-256 public key point must be on the curve.", ex);
        }
    }

    /// <summary>The x-coordinate of the shared point, 32 bytes - what WebCrypto's deriveBits(ECDH, 256) returns.</summary>
    public static byte[] Agree(ECDiffieHellman mine, ReadOnlySpan<byte> theirPublicRaw)
    {
        using var theirs = ImportPublic(theirPublicRaw);
        return mine.DeriveRawSecretAgreement(theirs.PublicKey);
    }
}

/// <summary>Base64url without padding - the one text encoding for binary in protocol 2.</summary>
public static class B64
{
    public static string Url(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public static byte[] FromUrl(string text)
    {
        try { return Base64Url.DecodeFromChars(text); }
        catch (FormatException ex) { throw new CryptographicException("Not base64url.", ex); }
    }
}

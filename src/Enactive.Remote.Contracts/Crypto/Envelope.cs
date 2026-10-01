namespace Enactive.Remote.Contracts.Crypto;

using System.Buffers.Binary;
using System.Security.Cryptography;

public sealed class EnvelopeException(string message) : CryptographicException(message);

/// <summary>
/// A sealed string field: "e1:" + base64url(0x01 | epoch u32 BE | nonce 12 | ciphertext | tag 16).
/// The associated data is never inside: both ends rebuild it from the record the envelope sits in, so
/// an envelope moved to another record fails to open instead of showing text that belongs elsewhere.
/// Nonces are random: an epoch key seals far fewer than 2^32 messages before rotation, the bound
/// for random 96-bit GCM nonces.
/// </summary>
public static class Envelope
{
    public const string Prefix = "e1:";
    private const byte Version = 1;
    private const int Header = 1 + 4 + 12;
    private const int Tag = 16;

    public static string Seal(ReadOnlySpan<byte> key, uint epoch, ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> nonce = default)
    {
        if (!nonce.IsEmpty && nonce.Length != 12) throw new ArgumentException("A nonce is 12 bytes.", nameof(nonce));
        var blob = new byte[Header + plaintext.Length + Tag];
        blob[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(1, 4), epoch);
        var n = blob.AsSpan(5, 12);
        if (nonce.IsEmpty) RandomNumberGenerator.Fill(n); else nonce.CopyTo(n);
        using var aes = new AesGcm(key, Tag);
        aes.Encrypt(n, plaintext, blob.AsSpan(Header, plaintext.Length), blob.AsSpan(Header + plaintext.Length, Tag), associatedData);
        return Prefix + B64.Url(blob);
    }

    public static byte[] Open(ReadOnlySpan<byte> key, string @sealed, ReadOnlySpan<byte> associatedData)
    {
        var blob = Decode(@sealed);
        var plaintext = new byte[blob.Length - Header - Tag];
        try
        {
            using var aes = new AesGcm(key, Tag);
            aes.Decrypt(blob.AsSpan(5, 12), blob.AsSpan(Header, plaintext.Length), blob.AsSpan(Header + plaintext.Length, Tag), plaintext, associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new EnvelopeException("The envelope does not open with this key for this record.");
        }
        return plaintext;
    }

    public static uint EpochOf(string @sealed) => BinaryPrimitives.ReadUInt32BigEndian(Decode(@sealed).AsSpan(1, 4));

    /// <summary>The gateway's whole check: the shape, never the content - it has no key.</summary>
    public static bool LooksSealed(string? value, int maxChars)
    {
        if (value is null || value.Length > maxChars || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try { Decode(value); return true; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>A blob shorter than header plus tag is refused here, before any slicing: otherwise "e1:" alone would compute a negative plaintext length and crash instead of being refused.</summary>
    private static byte[] Decode(string @sealed)
    {
        if (!@sealed.StartsWith(Prefix, StringComparison.Ordinal)) throw new EnvelopeException("Not an e1 envelope.");
        byte[] blob;
        try { blob = B64.FromUrl(@sealed[Prefix.Length..]); }
        catch (CryptographicException) { throw new EnvelopeException("An envelope is base64url."); }
        if (blob.Length < Header + Tag || blob[0] != Version) throw new EnvelopeException("Not an e1 envelope.");
        return blob;
    }
}

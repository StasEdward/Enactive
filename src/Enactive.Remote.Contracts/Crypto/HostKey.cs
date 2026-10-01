namespace Enactive.Remote.Contracts.Crypto;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// One computer's key for one epoch (spec §4). The secret itself is used for nothing but derivation:
/// content is sealed with the message key, rotation grants are authenticated with the grant-auth key.
/// </summary>
public sealed class HostKey
{
    private HostKey(uint epoch, byte[] secret)
    {
        if (secret.Length != 32) throw new ArgumentException("A host key is 32 bytes.", nameof(secret));
        Epoch = epoch;
        Secret = secret;
        MessageKey = RemoteKdf.Derive(secret, RemoteKdf.Message);
        GrantAuthKey = RemoteKdf.Derive(secret, RemoteKdf.GrantAuth);
    }

    public uint Epoch { get; }
    public ReadOnlyMemory<byte> Secret { get; }
    public byte[] MessageKey { get; }
    public byte[] GrantAuthKey { get; }

    public static HostKey Create(uint epoch) => new(epoch, RandomNumberGenerator.GetBytes(32));
    public static HostKey From(uint epoch, byte[] secret) => new(epoch, secret);

    public string SealText(string plaintext, byte[] associatedData, ReadOnlySpan<byte> nonce = default)
        => Envelope.Seal(MessageKey, Epoch, Encoding.UTF8.GetBytes(plaintext), associatedData, nonce);

    public string OpenText(string @sealed, byte[] associatedData)
    {
        if (Envelope.EpochOf(@sealed) != Epoch) throw new EnvelopeException("Sealed under another epoch.");
        return Encoding.UTF8.GetString(Envelope.Open(MessageKey, @sealed, associatedData));
    }
}

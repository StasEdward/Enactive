namespace Enactive.Remote.Contracts.Crypto;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Every key protocol 2 uses that is not random is derived here, with HKDF-SHA256, an empty salt and
/// a fixed info string, so one secret never serves two purposes: the message key and the grant-auth
/// key of an epoch come from the same host key and cannot be confused.
/// </summary>
public static class RemoteKdf
{
    public const string Message = "enactive-msg-v1";
    public const string GrantAuth = "enactive-grant-auth-v1";
    public const string Pair = "enactive-pair-v1";

    public static byte[] Derive(ReadOnlySpan<byte> ikm, string info) => Derive(ikm, Encoding.UTF8.GetBytes(info));

    public static byte[] Derive(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt: ReadOnlySpan<byte>.Empty, info: info);
        return output;
    }
}

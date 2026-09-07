namespace Enactive.Secrets;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Encrypts small secrets (the Anthropic API key) at rest using Windows DPAPI
/// (<see cref="ProtectedData"/>, CurrentUser scope) so settings.json no longer holds plaintext keys.
/// The ciphertext is bound to the current Windows user account and cannot be read by another user or
/// on another machine. Protected values carry a "dpapi:" prefix so plaintext (legacy) values are
/// recognised and migrated on the next save.
///
/// <para><b>Protect never returns anything but ciphertext.</b> It used to fall back to returning the
/// plaintext - "never lose the value over an encryption glitch" - and the caller then stored that in
/// a field named <c>ApiKeyProtected</c>. So a DPAPI failure wrote the API key to settings.json in
/// the clear, silently, and the next load read it back as a "legacy plaintext" value and carried on
/// happily for ever. Losing a value the user can retype is a nuisance; writing their key to disk
/// unencrypted without telling them is not a trade to make on their behalf. It throws now, the save
/// fails, and they are told why.</para>
/// </summary>
public static class Secret
{
    private const string Prefix = "dpapi:";

    // Extra entropy: ties the ciphertext to this app, not just the user account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Enactive.settings.v1");

    /// <summary>
    /// A "dpapi:"-prefixed base64 ciphertext, or an empty string for an empty input.
    /// </summary>
    /// <exception cref="SecretProtectionException">
    /// The value could not be encrypted - including on a host with no DPAPI at all. The caller must
    /// let the save fail; there is no acceptable fallback that involves writing the secret down.
    /// </exception>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        if (!OperatingSystem.IsWindows())
            throw new SecretProtectionException(
                "This build stores secrets with Windows DPAPI, and there is none on this system. "
                + "The value was not saved, because saving it would mean writing it down in the clear.");

        try
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            throw new SecretProtectionException(
                "Windows could not encrypt this secret (DPAPI, current user). Nothing was saved - "
                + "the alternative would be storing it in the clear.", ex);
        }
    }

    /// <summary>
    /// Whether a stored value is actually encrypted. A value without the prefix is legacy plaintext:
    /// it still LOADS, so nobody is locked out of their own settings, but it is worth saying out loud
    /// rather than treating as normal.
    /// </summary>
    public static bool IsProtected(string? stored)
        => stored is { Length: > 0 } && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Decrypts a "dpapi:" value; returns plaintext values (legacy) as-is.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored; // legacy plaintext or already-clear value
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var clear = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return string.Empty; // wrong user/machine or corrupt — treat as no key
        }
    }
}

/// <summary>A secret could not be encrypted, so it must not be written anywhere.</summary>
public sealed class SecretProtectionException : Exception
{
    public SecretProtectionException(string message, Exception? inner = null) : base(message, inner) { }
}

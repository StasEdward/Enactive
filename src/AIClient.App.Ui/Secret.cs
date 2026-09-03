namespace AIClient.App.Ui;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Encrypts small secrets (the Anthropic API key) at rest using Windows DPAPI
/// (<see cref="ProtectedData"/>, CurrentUser scope) so settings.json no longer holds plaintext keys.
/// The ciphertext is bound to the current Windows user account and cannot be read by another user or
/// on another machine. Protected values carry a "dpapi:" prefix so plaintext (legacy) values are
/// recognised and migrated on the next save. On a non-Windows host (the app is Windows-only, but be
/// safe) it degrades to pass-through rather than throwing.
/// </summary>
internal static class Secret
{
    private const string Prefix = "dpapi:";

    // Extra entropy: ties the ciphertext to this app, not just the user account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIClient.settings.v1");

    /// <summary>Returns a "dpapi:"-prefixed, base64 ciphertext (or the input unchanged if empty / not on Windows).</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            return plaintext; // Windows-only app; degrade gracefully elsewhere.

        try
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            return plaintext; // never lose the value over an encryption glitch
        }
    }

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

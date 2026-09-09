namespace Enactive.Remote.Gateway;

using System.Security.Cryptography;
using System.Text;

/// <summary>Identifiers and the one hash the gateway computes for itself.</summary>
public static class Ids
{
    /// <summary>A new server-side id: 32 hex characters, the width every id column expects.</summary>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// A device credential. 32 random bytes, shown once and never stored - only
    /// <see cref="Hash"/> of it is.
    /// </summary>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256, lowercase hex.
    ///
    /// <para>Unsalted and uniterated, and that is correct HERE and nowhere else in this file's
    /// neighbourhood: a device token is 256 bits of randomness, so there is no dictionary to
    /// stretch against, and this runs on the authentication path of every request a Host makes.
    /// Written down because the obvious "improvement" to PBKDF2 would buy nothing and put a
    /// deliberate delay in front of every call.</para>
    /// </summary>
    public static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// What a CommandId was first used for.
    ///
    /// <para>The id makes a retried request idempotent. The fingerprint is what stops that from
    /// also making a DIFFERENT request silently succeed: send the same id for "cancel run A" and
    /// then for "start task B" and the second is a conflict, not a no-op that quietly returns the
    /// first one's result.</para>
    /// </summary>
    public static string Fingerprint(string action) => Hash(action);
}

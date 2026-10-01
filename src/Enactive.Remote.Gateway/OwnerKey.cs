namespace Enactive.Remote.Gateway;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The single owner credential, and the counting that goes with it.
///
/// <para>One key, one person. There is exactly one valid answer, so a per-IP window throttles a
/// guesser and does nothing at all about a distributed one: a thousand addresses each trying ten
/// times a minute stay under every per-address limit ever set. So failures are ALSO counted
/// globally, and past the threshold nobody logs in for a while - including the owner, which is the
/// cost of the only defence that actually applies here.</para>
///
/// <para>Deliberately not stored: the key is configuration, and the sessions it grants are
/// invalidated by changing it. Named per-device sessions and a second factor are written down in
/// the remote-access design and are not built.</para>
/// </summary>
public sealed class OwnerKey(string key)
{
    private const int Threshold = 20;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private readonly byte[] _expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    private readonly Lock _gate = new();

    private int _failures;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Changing the configured key invalidates every session, because it is stamped into the cookie
    /// and checked on each request. That is what "sign out everywhere" is here.
    /// </summary>
    public string Version { get; } = Ids.Hash(key);

    public bool LockedOut
    {
        get
        {
            lock (_gate)
            {
                return DateTimeOffset.UtcNow < _lockedUntil;
            }
        }
    }

    /// <summary>
    /// Constant-time comparison of the hashes, so the answer takes the same time whether the first
    /// character was wrong or the last one was.
    /// </summary>
    public bool Matches(string? supplied)
    {
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? ""));
        var ok = CryptographicOperations.FixedTimeEquals(candidate, _expected);

        lock (_gate)
        {
            if (ok)
            {
                _failures = 0;
                return true;
            }

            if (++_failures >= Threshold)
            {
                _failures = 0;
                _lockedUntil = DateTimeOffset.UtcNow.Add(Lockout);
            }

            return false;
        }
    }
}

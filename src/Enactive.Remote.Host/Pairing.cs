namespace Enactive.Remote.Host;

using System.Security.Cryptography;
using Enactive.Remote.Contracts.Crypto;

/// <summary>What applying a connection code did. <paramref name="Paired"/> is false when the person said no.</summary>
public sealed record PairingOutcome(bool Paired, string HostId, string DeviceId, int GrantsQueued, bool Replaced);

/// <summary>
/// A connection code applied to this computer's keys (spec §5.2): the code's browser becomes the first
/// device this computer trusts, and is owed a grant of every key it holds, authenticated with the pair
/// key derived from the code's pairing secret.
///
/// <para><b>The pairing secret is used, never kept.</b> It authenticates the grant made here and is
/// in nothing written afterwards but that grant's MAC. Stored, it would let whoever read it later make
/// a grant the code's browser accepts as this computer's - a key of the reader's choosing, with every
/// command sealed under it readable by them.</para>
/// </summary>
public static class Pairing
{
    /// <summary>Who decided to trust a device that arrived in a connection code, as the trusted list records it.</summary>
    public const string AddedBy = "connection code";

    /// <summary>
    /// The pairing id of the grant answering a connection code. The gateway accepts exactly this for a
    /// computer's own first grant; an invitation's is its 32-character id instead.
    /// </summary>
    public const string PairingId = "connect";

    /// <summary>
    /// What a person is asked before a code replaces keys this computer already has. Asked because the
    /// answer cannot be taken back: every device that holds the old keys keeps what it read, and will
    /// read nothing this computer sends from then on.
    /// </summary>
    public const string ReplaceQuestion =
        "This replaces this computer's remote identity; devices that could read it will not read anything new. Continue?";

    /// <summary>
    /// What the trusted list calls a device it knows only from a code. The code carries no label, and
    /// the browser that made it is the one that registered this computer.
    /// </summary>
    private const string DeviceLabel = "The browser that registered this computer";

    /// <summary>
    /// The code in <paramref name="text"/>, or null with <paramref name="problem"/> saying what is
    /// wrong in words for the person who pasted it - the parser's own sentence, which says what to do.
    /// </summary>
    public static ConnectionCode? TryRead(string? text, out string problem)
    {
        try
        {
            var code = ConnectionCode.Parse(text ?? string.Empty);
            problem = string.Empty;
            return code;
        }
        catch (PairingCodeException unusable)
        {
            problem = unusable.Message;
            return null;
        }
    }

    /// <summary>
    /// Trusts the code's device and queues a grant of every key <paramref name="keys"/> holds to it.
    ///
    /// <para>Every epoch, not only the current one: a computer connected again with a new code may
    /// have rotated, and history sealed under an older epoch is still this browser's to read.</para>
    ///
    /// <para>A device this computer revoked is trusted again only with the key it had - the code is the
    /// person at both ends, so it may bring a device back, but a new key under a known id is another
    /// device using its name. A live device with another key is refused for the same reason.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The code is for another computer than these keys.</exception>
    /// <exception cref="InvalidOperationException">The code's device id is known here with another key.</exception>
    public static PairingOutcome Apply(ConnectionCode code, HostKeyStore keys, TimeProvider? clock = null)
    {
        // Keys of one computer granted under another computer's id would be a grant no device could
        // use, and a grant the gateway refuses only after the device was already trusted.
        if (code.HostId != keys.HostId)
            throw new ArgumentException($"This code is for computer {code.HostId}, not {keys.HostId}.", nameof(code));

        var known = keys.Trusted.FirstOrDefault(d => d.DeviceId == code.DeviceId);
        if (known is { RevokedAt: not null })
        {
            keys.Retrust(code.DeviceId, code.DevicePublic, AddedBy);
        }
        else
        {
            keys.Trust(new TrustedDevice(code.DeviceId, code.DevicePublic, DeviceLabel, AddedBy,
                (clock ?? TimeProvider.System).GetUtcNow(), RevokedAt: null));
        }

        var pairKey = code.PairKey;
        try
        {
            var signingPublic = keys.SigningPublic;
            foreach (var key in keys.All)
            {
                keys.EnqueueGrant(Grants.CreatePaired(
                    keys.HostId, code.DeviceId, code.DevicePublic, key, PairingId, pairKey, signingPublic));
            }
        }
        finally
        {
            // Derived from the secret, and as good as it for making grants this browser accepts.
            CryptographicOperations.ZeroMemory(pairKey);
        }

        return new PairingOutcome(true, keys.HostId, code.DeviceId, keys.All.Count, Replaced: false);
    }

    /// <summary>
    /// Applies a code to the keys in <paramref name="store"/>, making them first when there are none.
    ///
    /// <para>The keys there are replaced only with the person's yes, through
    /// <paramref name="confirmReplace"/>, and only when they have to be: when they belong to another
    /// computer than the code names (<paramref name="knownHostId"/>, the id the settings hold, says
    /// whose they are), or when this Windows account cannot read them. A no changes nothing.</para>
    ///
    /// <para>Nothing else may hold a key store over <paramref name="store"/> while this runs: replacing
    /// the keys under one that is sealing would leave it sealing with keys written nowhere.</para>
    /// </summary>
    public static async Task<PairingOutcome> ConnectAsync(
        HostStore store, string? knownHostId, ConnectionCode code, Func<Task<bool>> confirmReplace,
        TimeProvider? clock = null)
    {
        var replace = false;

        if (HostKeyStore.HasKeys(store))
        {
            // Keys and no id in the settings are keys of a computer nobody can name any more - the
            // settings were reset, or copied without them. They are no more this code's computer than
            // keys under another id.
            replace = knownHostId != code.HostId || !Readable(store, code.HostId);
        }

        if (replace)
        {
            if (!await confirmReplace())
                return new PairingOutcome(false, code.HostId, code.DeviceId, 0, Replaced: false);

            HostKeyStore.Reset(store);
        }

        using var keys = new HostKeyStore(store, code.HostId, clock);
        return Apply(code, keys, clock) with { Replaced = replace };
    }

    private static bool Readable(HostStore store, string hostId)
    {
        try
        {
            using (new HostKeyStore(store, hostId)) { }
            return true;
        }
        catch (HostKeysUnreadableException)
        {
            return false;
        }
    }
}

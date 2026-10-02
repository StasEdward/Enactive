namespace Enactive.Remote.Host;

using System.Security.Cryptography;
using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// What applying a connection code did. <paramref name="Paired"/> is false when the person said no, or
/// when the code was refused - then <paramref name="Problem"/> says why, in words for the person.
/// </summary>
public sealed record PairingOutcome(
    bool Paired, string HostId, string DeviceId, int GrantsQueued, bool Replaced, string? Problem = null);

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
    /// Asked before a code for this same computer admits a device it does not trust now - one it never
    /// trusted, or one it revoked. The computer's id is no secret (it is on the settings pane, in the log,
    /// and known to the gateway's operator), so a code naming it proves nothing about who made the code;
    /// applied silently, a crafted one would hand its own device every key this computer holds, and bring
    /// a revoked device back with the epochs made after its revocation.
    /// </summary>
    public const string AdmitQuestion =
        "This code admits a new device to every key this computer holds. Continue?";

    /// <summary>
    /// What a person is told when a code for this computer finds its keys unreadable. They are NOT
    /// replaced under the same id: the gateway and every browser pinned the old signing key, so a new one
    /// could never be delivered - every grant it signed would be refused as a bad grant, for good.
    /// </summary>
    public const string UnreadableForThisComputer =
        "This computer's remote keys cannot be read on this account. Make a new connection code in the browser "
        + "(Computers → Register) and paste it here.";

    /// <summary>What a person is told when a code names a device this computer knows with another key.</summary>
    public const string AnotherKey =
        "This code names a device this computer already knows with a different key, so it was not applied - "
        + "a device id is never given to a new key. Make a new code in the browser.";

    /// <summary>
    /// Asked before a code moves this computer to another gateway. The token would go there from then on,
    /// and the computer would take its commands from there - a crafted code could otherwise move it to a
    /// service of the crafter's choosing without a word.
    /// </summary>
    public static string MoveQuestion(Uri gateway)
        => $"This code moves this computer to another service ({gateway.GetLeftPart(UriPartial.Authority)}). Continue?";

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
            // The secret and the key derived from it are as good as each other for making grants this
            // browser accepts, and neither is needed once the grants are made: wiped, rather than left in
            // memory for as long as the code object happens to live.
            CryptographicOperations.ZeroMemory(pairKey);
            CryptographicOperations.ZeroMemory(code.PairingSecret);
        }

        return new PairingOutcome(true, keys.HostId, code.DeviceId, keys.All.Count, Replaced: false);
    }

    /// <summary>
    /// Applies a code to the keys in <paramref name="store"/>, making them first when there are none.
    ///
    /// <para>Every question is asked before anything changes, through <paramref name="confirm"/>, and a no
    /// to any of them changes nothing. Silent only for the first connection and for an exact re-paste:
    /// the stored computer, the stored gateway, and a device this computer already trusts with that key.
    /// Otherwise: another computer (<paramref name="knownHostId"/> is the id the settings hold) replaces
    /// the keys after <see cref="ReplaceQuestion"/>; another gateway asks <see cref="MoveQuestion"/>; a
    /// device not trusted now asks <see cref="AdmitQuestion"/>. A device known with another key, and keys
    /// of this same computer that this account cannot read, are refused with a sentence.</para>
    ///
    /// <para><paramref name="beforeChange"/> runs once every answer is yes and before the first write: the
    /// caller stops whatever holds a key store over the same file there, so a person who says no keeps a
    /// running service and the remote tasks on it. Until then this only reads.</para>
    /// </summary>
    public static async Task<PairingOutcome> ConnectAsync(
        HostStore store, string? knownHostId, string? knownGateway, ConnectionCode code,
        Func<string, Task<bool>> confirm, Func<Task>? beforeChange = null, TimeProvider? clock = null)
    {
        var replace = false;

        if (HostKeyStore.HasKeys(store))
        {
            if (knownHostId != code.HostId)
            {
                // Keys and no id in the settings are keys of a computer nobody can name any more - the
                // settings were reset, or copied without them. They are no more this code's computer than
                // keys under another id.
                if (!await confirm(ReplaceQuestion))
                    return Declined(code);

                replace = true;
            }
            else
            {
                List<string> questions;
                try
                {
                    questions = QuestionsFor(store, knownGateway, code, clock, out var refusal);
                    if (refusal is not null)
                        return Declined(code) with { Problem = refusal };
                }
                catch (HostKeysUnreadableException)
                {
                    return Declined(code) with { Problem = UnreadableForThisComputer };
                }

                foreach (var question in questions)
                {
                    if (!await confirm(question))
                        return Declined(code);
                }
            }
        }

        if (beforeChange is not null)
            await beforeChange();

        if (replace)
            HostKeyStore.Reset(store);

        using var keys = new HostKeyStore(store, code.HostId, clock);
        return Apply(code, keys, clock) with { Replaced = replace };
    }

    /// <summary>
    /// What to ask before a code for this same computer is applied, or a refusal. Reads the key store and
    /// writes nothing - a key store made here over keys that exist creates nothing.
    /// </summary>
    private static List<string> QuestionsFor(
        HostStore store, string? knownGateway, ConnectionCode code, TimeProvider? clock, out string? refusal)
    {
        using var keys = new HostKeyStore(store, code.HostId, clock);
        var known = keys.Trusted.FirstOrDefault(d => d.DeviceId == code.DeviceId);
        refusal = null;

        if (known is not null && !CryptographicOperations.FixedTimeEquals(known.PublicKey, code.DevicePublic))
        {
            refusal = AnotherKey;
            return [];
        }

        var questions = new List<string>();

        if (!SameOrigin(code.Gateway, knownGateway))
            questions.Add(MoveQuestion(code.Gateway));

        if (known is not { RevokedAt: null })
            questions.Add(AdmitQuestion);

        return questions;
    }

    private static bool SameOrigin(Uri gateway, string? known)
        => Uri.TryCreate(known, UriKind.Absolute, out var stored)
           && string.Equals(gateway.GetLeftPart(UriPartial.Authority), stored.GetLeftPart(UriPartial.Authority),
               StringComparison.OrdinalIgnoreCase);

    private static PairingOutcome Declined(ConnectionCode code)
        => new(false, code.HostId, code.DeviceId, 0, Replaced: false);
}

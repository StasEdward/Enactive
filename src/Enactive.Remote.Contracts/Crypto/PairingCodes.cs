namespace Enactive.Remote.Contracts.Crypto;

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// A pairing code or link that cannot be used. The message is written for the person who pasted it
/// (what is wrong and what to do), because it is shown to them as it is.
/// </summary>
public sealed class PairingCodeException(string message) : FormatException(message);

/// <summary>
/// What the browser shows when a person registers a computer (spec §5.2): where the gateway is, the
/// computer's id and device token, the browser's own device key and the pairing secret that
/// authenticates the first key grant. The person carries it to the desktop app by hand; the gateway
/// never sees the pairing secret.
/// </summary>
public sealed record ConnectionCode(Uri Gateway, string HostId, string Token, string DeviceId, byte[] DevicePublic, byte[] PairingSecret)
{
    private const string Prefix = "enactive-connect:";
    private const int TokenLength = 64;
    private const int SecretLength = 32;
    private const string CopyAgain = "The code was cut short or changed - copy all of it again.";

    public byte[] PairKey => RemoteKdf.Derive(PairingSecret, RemoteKdf.Pair);

    public string Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", PairingOrigin.Version);
            json.WriteString("g", PairingOrigin.Format(Gateway));
            json.WriteString("h", HostId);
            json.WriteString("t", Token);
            json.WriteString("d", DeviceId);
            json.WriteString("k", B64.Url(DevicePublic));
            json.WriteString("p", B64.Url(PairingSecret));
            json.WriteEndObject();
        }
        return Prefix + B64.Url(buffer.WrittenSpan);
    }

    public static ConnectionCode Parse(string text)
    {
        // A code pasted from a terminal or a message usually brings a newline or spaces with it; refusing those would make a good code look broken.
        var trimmed = (text ?? "").Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            throw new PairingCodeException("This is not an Enactive connection code - it should begin with \"enactive-connect:\". Copy the whole code from the browser again.");

        using var document = Open(trimmed[Prefix.Length..]);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new PairingCodeException(CopyAgain);

        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != PairingOrigin.Version)
            throw new PairingCodeException($"The code's version is missing or is not one this Enactive understands (version {PairingOrigin.Version}) - update the desktop app and reload the browser page, then make a new code.");

        var gateway = PairingOrigin.Parse(Text(root, "g", "gateway address"));
        var hostId = Text(root, "h", "computer id");
        var token = Text(root, "t", "token");
        if (token.Length != TokenLength || !token.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new PairingCodeException("The code's token is not valid (it must be 64 lowercase hex characters) - copy all of it again.");
        var deviceId = Text(root, "d", "device id");
        var devicePublic = Binary(root, "k", "device key");
        try { P256.ImportPublic(devicePublic).Dispose(); }
        catch (CryptographicException) { throw new PairingCodeException("The code's device key is not a valid P-256 public key - copy all of it again."); }
        var secret = Binary(root, "p", "pairing secret");
        if (secret.Length != SecretLength) throw new PairingCodeException("The code's pairing secret has the wrong length - copy all of it again.");

        return new ConnectionCode(gateway, hostId, token, deviceId, devicePublic, secret);
    }

    private static JsonDocument Open(string base64)
    {
        if (base64.Length == 0) throw new PairingCodeException("The code is empty after \"enactive-connect:\" - copy all of it again.");
        try { return JsonDocument.Parse(B64.FromUrl(base64)); }
        // Invalid UTF-8 in the decoded bytes surfaces as ArgumentException rather than JsonException.
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            throw new PairingCodeException(CopyAgain);
        }
    }

    private static string Text(JsonElement root, string member, string what)
    {
        if (!root.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
            throw new PairingCodeException($"The code has no {what} - copy all of it again.");
        return value.GetString()!;
    }

    private static byte[] Binary(JsonElement root, string member, string what)
    {
        try { return B64.FromUrl(Text(root, member, what)); }
        catch (CryptographicException) { throw new PairingCodeException($"The code's {what} is damaged - copy all of it again."); }
    }
}

/// <summary>
/// What a trusted party shows to admit another device (spec §5.3). The pairing secret is in the URL
/// fragment because a browser never sends the fragment to the server: the gateway serves the page
/// but cannot read the secret.
/// </summary>
public sealed record InviteLink(Uri Gateway, string InviteId, byte[] PairingSecret)
{
    private const string Path = "/pair";
    private const int SecretLength = 32;

    public byte[] PairKey => RemoteKdf.Derive(PairingSecret, RemoteKdf.Pair);

    public string Format() =>
        $"{PairingOrigin.Format(Gateway)}{Path}#v={PairingOrigin.Version}&i={Uri.EscapeDataString(InviteId)}&p={B64.Url(PairingSecret)}";

    public static InviteLink Parse(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) || uri.AbsolutePath != Path)
            throw new PairingCodeException("This is not an Enactive invitation link - it should open the /pair page of the gateway. Copy the whole link again.");
        var gateway = PairingOrigin.Parse(uri.GetLeftPart(UriPartial.Authority));

        // Split by hand: the invite id is escaped on the way out, so each value is unescaped only after the split.
        var members = new Dictionary<string, string>();
        foreach (var part in uri.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('=');
            if (at > 0) members.TryAdd(part[..at], Uri.UnescapeDataString(part[(at + 1)..]));
        }

        if (!members.TryGetValue("v", out var version) || version != PairingOrigin.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new PairingCodeException($"The link's version is missing or is not one this Enactive understands (version {PairingOrigin.Version}) - update the desktop app and ask for a new link.");
        if (!members.TryGetValue("i", out var inviteId) || inviteId.Length == 0)
            throw new PairingCodeException("The link has no invitation id - copy all of it again.");
        if (!members.TryGetValue("p", out var encoded))
            throw new PairingCodeException("The link has no pairing secret - copy all of it again, including the part after the #.");
        byte[] secret;
        try { secret = B64.FromUrl(encoded); }
        catch (CryptographicException) { throw new PairingCodeException("The link's pairing secret is damaged - copy all of it again."); }
        if (secret.Length != SecretLength) throw new PairingCodeException("The link's pairing secret has the wrong length - copy all of it again.");

        return new InviteLink(gateway, inviteId, secret);
    }
}

/// <summary>
/// What the new device sends to prove which public key the invitation was for. The gateway relays the
/// enrollment but does not know the pairing secret, so it cannot produce a MAC for a key of its own;
/// without this check it could substitute its key and receive the host keys wrapped for it.
/// </summary>
public static class Enrollment
{
    private const string MacVersion = "enactive-enroll-v1";

    public static string Mac(byte[] pairKey, string inviteId, string deviceId, ReadOnlySpan<byte> devicePublic) =>
        B64.Url(Compute(pairKey, inviteId, deviceId, devicePublic));

    public static bool Verify(byte[] pairKey, string inviteId, string deviceId, ReadOnlySpan<byte> devicePublic, string mac)
    {
        byte[] given;
        // The MAC comes from the gateway's relay, so a malformed one is an answer ("no"), not an exception that the caller's handler must remember to catch.
        try { given = B64.FromUrl(mac); }
        catch (CryptographicException) { return false; }
        return CryptographicOperations.FixedTimeEquals(Compute(pairKey, inviteId, deviceId, devicePublic), given);
    }

    private static byte[] Compute(byte[] pairKey, string inviteId, string deviceId, ReadOnlySpan<byte> devicePublic) =>
        HMACSHA256.HashData(pairKey, Canonical.Bytes(MacVersion, inviteId, deviceId, B64.Url(devicePublic)));
}

/// <summary>
/// The one rule for a gateway address in a code or link. Plain http to a remote host would carry the
/// device token in the clear, so http is accepted only for a machine's own loopback (local development).
/// </summary>
internal static class PairingOrigin
{
    /// <summary>Pinned to 2 rather than read from RemoteProtocol.Version: these formats are the protocol-2 ones, and an old Host or panel must refuse them by version, not misread them.</summary>
    public const int Version = 2;

    public static string Format(Uri gateway)
    {
        if (!Allowed(gateway)) throw new PairingCodeException(Refusal);
        return gateway.GetLeftPart(UriPartial.Authority);
    }

    public static Uri Parse(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !Allowed(uri)) throw new PairingCodeException(Refusal);
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    private const string Refusal =
        "The gateway address must be an https address (http is allowed only for localhost) with no password, path or query - check it and make a new code.";

    private static bool Allowed(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
}

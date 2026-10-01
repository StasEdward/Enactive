namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Xunit;

/// <summary>
/// The protocol-2 cryptography: one canonical rule, envelopes, grants and pairing codes. The browser
/// implements the same in JS; tests/vectors/remote-crypto-v2.json pins both to the same bytes.
/// </summary>
public sealed class RemoteCryptoTests
{
    [Fact]
    public void Canonical_text_prefixes_every_field_with_its_utf8_length()
        => Assert.Equal("v1\n3:abc\n0:\n2:é\n", Canonical.Text("v1", "abc", null, "é"));

    /// <summary>A separator inside a field cannot make two field lists read the same.</summary>
    [Fact]
    public void Two_field_lists_never_share_a_canonical_text()
        => Assert.NotEqual(Canonical.Text("v", "a\n1:b"), Canonical.Text("v", "a", "b"));

    [Fact]
    public void Two_parties_agree_on_the_same_secret()
    {
        using var a = P256.Generate();
        using var b = P256.Generate();
        var secret = P256.Agree(a, P256.PublicRaw(b));
        Assert.Equal(secret, P256.Agree(b, P256.PublicRaw(a)));
        Assert.Equal(32, secret.Length);
    }

    [Fact]
    public void A_public_key_is_65_bytes_starting_with_4()
    {
        using var key = P256.Generate();
        var raw = P256.PublicRaw(key);
        Assert.Equal(65, raw.Length);
        Assert.Equal(4, raw[0]);
    }

    /// <summary>A point that is not on the curve is refused at import, not used in an agreement.</summary>
    [Fact]
    public void A_point_off_the_curve_is_refused()
    {
        var raw = new byte[65];
        raw[0] = 4;
        raw[64] = 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => P256.ImportPublic(raw));
    }

    [Fact]
    public void A_key_of_the_wrong_length_or_prefix_is_refused()
    {
        // 64 bytes instead of 65
        var short_key = new byte[64];
        short_key[0] = 4;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => P256.ImportPublic(short_key));

        // 65 bytes but starting with 0x02 instead of 0x04
        var wrong_prefix = new byte[65];
        wrong_prefix[0] = 2;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => P256.ImportPublic(wrong_prefix));
    }

    [Fact]
    public void Derivations_with_different_info_differ()
    {
        var ikm = new byte[32];
        Assert.NotEqual(RemoteKdf.Derive(ikm, RemoteKdf.Message), RemoteKdf.Derive(ikm, RemoteKdf.GrantAuth));
        Assert.Equal(32, RemoteKdf.Derive(ikm, RemoteKdf.Message).Length);
    }

    [Fact]
    public void A_sealed_text_opens_with_the_same_key_and_associated_data()
    {
        var key = HostKey.Create(1);
        var ad = Canonical.Bytes("enactive-event-v1", "host-a", "run-1", "3", "Progress");
        var @sealed = key.SealText("Step 2 done", ad);
        Assert.StartsWith(Envelope.Prefix, @sealed);
        Assert.Equal(1u, Envelope.EpochOf(@sealed));
        Assert.Equal("Step 2 done", key.OpenText(@sealed, ad));
    }

    /// <summary>Review focus 1: an envelope copied to another record must not open there.</summary>
    [Fact]
    public void Moved_envelope_does_not_open()
    {
        var key = HostKey.Create(1);
        var @sealed = key.SealText("secret", Canonical.Bytes("enactive-event-v1", "host-a", "run-1", "3", "Progress"));
        Assert.Throws<EnvelopeException>(() => key.OpenText(@sealed, Canonical.Bytes("enactive-event-v1", "host-a", "run-2", "3", "Progress")));
    }

    [Fact]
    public void A_changed_byte_does_not_open()
    {
        var key = HostKey.Create(1);
        var ad = Canonical.Bytes("x");
        var raw = B64.FromUrl(key.SealText("secret", ad)[Envelope.Prefix.Length..]);
        raw[^1] ^= 1;
        Assert.Throws<EnvelopeException>(() => key.OpenText(Envelope.Prefix + B64.Url(raw), ad));
    }

    [Fact]
    public void Another_epochs_key_does_not_open()
    {
        var ad = Canonical.Bytes("x");
        var @sealed = HostKey.Create(1).SealText("secret", ad);
        Assert.Throws<EnvelopeException>(() => HostKey.Create(2).OpenText(@sealed, ad));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("e1:")]
    [InlineData("e1:!!!")]
    public void Something_that_is_not_an_envelope_is_refused(string value)
        => Assert.False(Envelope.LooksSealed(value, 1000));

    /// <summary>Without the length check "e1:" alone would slice a negative-length plaintext and crash instead of refusing.</summary>
    [Theory]
    [InlineData("e1:")]
    [InlineData("e1:AQ")]
    public void Opening_something_that_is_not_an_envelope_fails_cleanly(string value)
        => Assert.Throws<EnvelopeException>(() => HostKey.Create(1).OpenText(value, Canonical.Bytes("x")));

    [Fact]
    public void A_grant_opens_on_the_device_it_was_made_for()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var key = HostKey.Create(3);
        var grant = Grants.Create("host-a", "dev-1", P256.PublicRaw(device), key, Grants.AuthByPairing("connect"), pairKey);
        var opened = Grants.Open(grant, device, pairKey);
        Assert.Equal(3u, opened.Epoch);
        Assert.Equal(key.Secret.ToArray(), opened.Secret.ToArray());
    }

    /// <summary>
    /// Review focus 2: the gateway can wrap a key of its own to any device's public key - the key is
    /// public - but without the pairing secret its MAC does not verify, and the device refuses it.
    /// </summary>
    [Fact]
    public void A_grant_without_the_pairing_secret_is_rejected()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var forged = Grants.Create("host-a", "dev-1", P256.PublicRaw(device), HostKey.Create(1),
            Grants.AuthByPairing("connect"), RemoteKdf.Derive(new byte[] { 9 }, RemoteKdf.Pair));
        Assert.Throws<EnvelopeException>(() => Grants.Open(forged, device, pairKey));
    }

    [Fact]
    public void A_grant_for_another_device_does_not_open()
    {
        using var device = P256.Generate();
        using var other = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var grant = Grants.Create("host-a", "dev-1", P256.PublicRaw(other), HostKey.Create(1), Grants.AuthByPairing("connect"), pairKey);
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant, device, pairKey));
    }

    [Fact]
    public void A_grant_moved_to_another_host_does_not_open()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var grant = Grants.Create("host-a", "dev-1", P256.PublicRaw(device), HostKey.Create(1), Grants.AuthByPairing("connect"), pairKey);
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { HostId = "host-b" }, device, pairKey));
    }

    /// <summary>The MAC covers AuthBy, so a gateway cannot relabel a rotation grant as a pairing grant.</summary>
    [Fact]
    public void A_grant_relabelled_with_another_authentication_does_not_open()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var grant = Grants.Create("host-a", "dev-1", P256.PublicRaw(device), HostKey.Create(2), Grants.AuthByEpoch(1), pairKey);
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { AuthBy = Grants.AuthByPairing("connect") }, device, pairKey));
    }

    /// <summary>
    /// The device code treats EnvelopeException as "this grant is not for me / not trusted", so a field
    /// that is damaged - in any way - must surface as that and not as some other exception family.
    /// </summary>
    [Fact]
    public void A_grant_with_a_corrupted_ephemeral_key_is_refused_as_an_envelope_error()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var grant = Grants.Create("host-a", "dev-1", P256.PublicRaw(device), HostKey.Create(1), Grants.AuthByPairing("connect"), pairKey);

        // Damaged in transit: the MAC no longer matches.
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { EphemeralPublic = "!!!" }, device, pairKey));

        // Damaged before the MAC was made (a holder of the pairing key): the key must still be refused,
        // and without the import check a point off the curve escapes as a PlatformNotSupportedException or a raw CryptographicException.
        var offCurve = new byte[65];
        offCurve[0] = 4;
        offCurve[64] = 1;
        var badKey = B64.Url(offCurve);
        var mac = B64.Url(System.Security.Cryptography.HMACSHA256.HashData(pairKey, Canonical.Bytes(
            "enactive-grant-mac-v1", grant.HostId, grant.DeviceId, "1", badKey, B64.Url(P256.PublicRaw(device)),
            grant.Nonce, grant.Ciphertext, grant.AuthBy)));
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { EphemeralPublic = badKey, Mac = mac }, device, pairKey));
    }

    [Fact]
    public void A_grant_with_malformed_fields_is_refused_as_an_envelope_error()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var grant = Grants.Create("host-a", "dev-1", P256.PublicRaw(device), HostKey.Create(1), Grants.AuthByPairing("connect"), pairKey);
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { Mac = "!!!" }, device, pairKey));
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { Mac = null! }, device, pairKey));
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { Mac = B64.Url(new byte[5]) }, device, pairKey));
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { Nonce = "!!!" }, device, pairKey));
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant with { Ciphertext = "!!!" }, device, pairKey));
    }

    /// <summary>Fields the MAC covers but whose lengths the cipher insists on: a holder of the pairing key can sign a short nonce or ciphertext.</summary>
    [Fact]
    public void A_signed_grant_with_wrong_lengths_is_refused_as_an_envelope_error()
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var dPub = P256.PublicRaw(device);
        var good = Grants.Create("host-a", "dev-1", dPub, HostKey.Create(1), Grants.AuthByPairing("connect"), pairKey);

        KeyGrant Resigned(KeyGrant g) => g with
        {
            Mac = B64.Url(System.Security.Cryptography.HMACSHA256.HashData(pairKey, Canonical.Bytes(
                "enactive-grant-mac-v1", g.HostId, g.DeviceId, g.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                g.EphemeralPublic, B64.Url(dPub), g.Nonce, g.Ciphertext, g.AuthBy)))
        };

        Assert.Throws<EnvelopeException>(() => Grants.Open(Resigned(good with { Nonce = B64.Url(new byte[5]) }), device, pairKey));
        Assert.Throws<EnvelopeException>(() => Grants.Open(Resigned(good with { Ciphertext = B64.Url(new byte[10]) }), device, pairKey));
    }
}

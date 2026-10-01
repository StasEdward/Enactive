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

    [Fact]
    public void A_connection_code_survives_format_and_parse()
    {
        using var device = P256.Generate();
        var code = new ConnectionCode(new Uri("https://remote.enactive.dev"), "host-a", new string('a', 64), "dev-1",
            P256.PublicRaw(device), new byte[32]);
        var text = code.Format();
        Assert.StartsWith("enactive-connect:", text);
        var back = ConnectionCode.Parse(text);
        Assert.Equal(code.Gateway, back.Gateway);
        Assert.Equal(code.HostId, back.HostId);
        Assert.Equal(code.Token, back.Token);
        Assert.Equal(code.DeviceId, back.DeviceId);
        Assert.Equal(code.DevicePublic, back.DevicePublic);
        Assert.Equal(code.PairingSecret, back.PairingSecret);
        Assert.Equal(code.PairKey, back.PairKey);
    }

    /// <summary>The panel writes the members under these exact short names, in this order; the JS twin must read the same text.</summary>
    [Fact]
    public void A_connection_code_is_json_with_the_agreed_members_in_order()
    {
        var code = ConnectionCode.Parse(GoodCode());
        var json = System.Text.Encoding.UTF8.GetString(B64.FromUrl(code.Format()["enactive-connect:".Length..]));
        Assert.Matches("""^\{"v":2,"g":"https://remote\.enactive\.dev","h":"host-a","t":"a{64}","d":"dev-1","k":"[A-Za-z0-9_-]+","p":"[A-Za-z0-9_-]+"\}$""", json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("enactive-connect:")]
    [InlineData("enactive-connect:e30")]           // {}
    [InlineData("something-else:abc")]
    [InlineData("enactive-connect:!!!")]           // not base64url
    [InlineData("enactive-connect:bm90IGpzb24")]   // "not json"
    [InlineData("enactive-connect:W10")]           // []
    public void A_broken_connection_code_is_refused_with_a_reason(string text)
        => Assert.Throws<PairingCodeException>(() => ConnectionCode.Parse(text));

    /// <summary>A code pasted from a terminal or a message often carries a trailing newline or spaces; that must not make it "broken".</summary>
    [Fact]
    public void A_connection_code_with_surrounding_whitespace_is_accepted()
        => Assert.Equal("host-a", ConnectionCode.Parse("  " + GoodCode() + "\r\n").HostId);

    [Fact]
    public void A_connection_code_to_a_loopback_gateway_over_http_is_accepted()
    {
        using var device = P256.Generate();
        var code = new ConnectionCode(new Uri("http://localhost:5100"), "host-a", new string('b', 64), "dev-1",
            P256.PublicRaw(device), new byte[32]);
        Assert.Equal(new Uri("http://localhost:5100"), ConnectionCode.Parse(code.Format()).Gateway);
    }

    /// <summary>Format must not write a gateway that Parse would refuse: a plain-http gateway on the internet would carry the token in the clear.</summary>
    [Fact]
    public void A_connection_code_cannot_be_formatted_for_a_plain_http_remote_gateway()
    {
        using var device = P256.Generate();
        var code = new ConnectionCode(new Uri("http://remote.enactive.dev"), "host-a", new string('a', 64), "dev-1",
            P256.PublicRaw(device), new byte[32]);
        Assert.Throws<PairingCodeException>(() => code.Format());
    }

    /// <summary>Every member that is wrong is named in the message, so the person (or the panel's author) knows what to fix.</summary>
    [Theory]
    [MemberData(nameof(BadMembers))]
    public void A_connection_code_with_a_bad_member_is_refused_naming_that_member(string member, string? jsonValue, string expected)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(GoodJson())!.AsObject();
        if (jsonValue is null) json.Remove(member);
        else json[member] = System.Text.Json.Nodes.JsonNode.Parse(jsonValue);
        var ex = Assert.Throws<PairingCodeException>(() => ConnectionCode.Parse(Encode(json.ToJsonString())));
        Assert.Contains(expected, ex.Message);
    }

    public static IEnumerable<object?[]> BadMembers()
    {
        static string Str(byte[] bytes) => "\"" + B64.Url(bytes) + "\"";
        var offCurve = new byte[65];
        offCurve.AsSpan().Fill(1);
        offCurve[0] = 4;
        var notUncompressed = new byte[65];

        yield return new object?[] { "v", null, "version" };
        yield return new object?[] { "v", "1", "version" };
        yield return new object?[] { "v", "3", "version" };
        yield return new object?[] { "v", "\"2\"", "version" };
        yield return new object?[] { "g", null, "gateway" };
        yield return new object?[] { "g", "5", "gateway" };
        yield return new object?[] { "g", "\"not a url\"", "gateway" };
        yield return new object?[] { "g", "\"http://remote.enactive.dev\"", "gateway" };
        yield return new object?[] { "g", "\"ftp://remote.enactive.dev\"", "gateway" };
        yield return new object?[] { "g", "\"https://remote.enactive.dev/some/path\"", "gateway" };
        yield return new object?[] { "g", "\"https://user:pw@remote.enactive.dev\"", "gateway" };
        yield return new object?[] { "h", null, "computer id" };
        yield return new object?[] { "h", "\"\"", "computer id" };
        yield return new object?[] { "h", "7", "computer id" };
        yield return new object?[] { "t", null, "token" };
        yield return new object?[] { "t", "\"" + new string('a', 63) + "\"", "token" };
        yield return new object?[] { "t", "\"" + new string('A', 64) + "\"", "token" };
        yield return new object?[] { "t", "\"" + new string('g', 64) + "\"", "token" };
        yield return new object?[] { "t", "null", "token" };
        yield return new object?[] { "d", null, "device id" };
        yield return new object?[] { "d", "\"\"", "device id" };
        yield return new object?[] { "d", "true", "device id" };
        yield return new object?[] { "k", null, "device key" };
        yield return new object?[] { "k", "\"!!!\"", "device key" };
        yield return new object?[] { "k", Str(new byte[64]), "device key" };
        yield return new object?[] { "k", Str(notUncompressed), "device key" };
        yield return new object?[] { "k", Str(offCurve), "device key" };
        yield return new object?[] { "k", "12", "device key" };
        yield return new object?[] { "p", null, "pairing secret" };
        yield return new object?[] { "p", "\"!!!\"", "pairing secret" };
        yield return new object?[] { "p", Str(new byte[31]), "pairing secret" };
        yield return new object?[] { "p", Str(new byte[33]), "pairing secret" };
        yield return new object?[] { "p", "[]", "pairing secret" };
    }

    private static string GoodJson()
    {
        using var device = P256.Generate();
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = 2, ["g"] = "https://remote.enactive.dev", ["h"] = "host-a", ["t"] = new string('a', 64), ["d"] = "dev-1",
            ["k"] = B64.Url(P256.PublicRaw(device)), ["p"] = B64.Url(new byte[32])
        });
    }

    private static string Encode(string json) => "enactive-connect:" + B64.Url(System.Text.Encoding.UTF8.GetBytes(json));

    private static string GoodCode() => Encode(GoodJson());

    /// <summary>The pairing secret goes in the fragment: browsers never send a fragment to the server.</summary>
    [Fact]
    public void An_invite_link_carries_its_secret_in_the_fragment()
    {
        var link = new InviteLink(new Uri("https://remote.enactive.dev"), "inv-1", new byte[32]).Format();
        var uri = new Uri(link);
        Assert.Equal("/pair", uri.AbsolutePath);
        Assert.Contains("p=", uri.Fragment);
        Assert.DoesNotContain("p=", uri.Query);
        Assert.Equal("inv-1", InviteLink.Parse(link).InviteId);
    }

    [Fact]
    public void An_invite_link_survives_format_and_parse_with_its_members_in_order()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var link = new InviteLink(new Uri("https://remote.enactive.dev"), "inv-1", secret).Format();
        Assert.Equal($"https://remote.enactive.dev/pair#v=2&i=inv-1&p={B64.Url(secret)}", link);
        var back = InviteLink.Parse(link);
        Assert.Equal(new Uri("https://remote.enactive.dev"), back.Gateway);
        Assert.Equal(secret, back.PairingSecret);
        Assert.Equal(RemoteKdf.Derive(secret, RemoteKdf.Pair), back.PairKey);
    }

    /// <summary>An invite id that holds fragment separators must not split into extra members.</summary>
    [Fact]
    public void An_invite_id_with_separator_characters_survives_the_round_trip()
    {
        var link = new InviteLink(new Uri("https://remote.enactive.dev"), "a b&c=d#e", new byte[32]).Format();
        Assert.Equal("a b&c=d#e", InviteLink.Parse(link).InviteId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://remote.enactive.dev/pair")]                                   // no fragment
    [InlineData("https://remote.enactive.dev/pair#v=2&i=inv-1")]                       // no secret
    [InlineData("https://remote.enactive.dev/pair#v=2&i=inv-1&p=AAAA")]                // secret too short
    [InlineData("https://remote.enactive.dev/pair#v=2&i=inv-1&p=!!!")]                 // secret not base64url
    [InlineData("https://remote.enactive.dev/pair#v=1&i=inv-1&p=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://remote.enactive.dev/pair#i=inv-1&p=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // no version
    [InlineData("https://remote.enactive.dev/pair#v=2&p=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]       // no invite id
    [InlineData("https://remote.enactive.dev/elsewhere#v=2&i=inv-1&p=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("http://remote.enactive.dev/pair#v=2&i=inv-1&p=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_broken_invite_link_is_refused_with_a_reason(string url)
        => Assert.Throws<PairingCodeException>(() => InviteLink.Parse(url));

    [Fact]
    public void An_invite_link_names_the_member_that_is_wrong()
    {
        var secret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.Contains("secret", Assert.Throws<PairingCodeException>(() => InviteLink.Parse("https://remote.enactive.dev/pair#v=2&i=inv-1")).Message);
        Assert.Contains("version", Assert.Throws<PairingCodeException>(() => InviteLink.Parse($"https://remote.enactive.dev/pair#v=1&i=inv-1&p={secret}")).Message);
        Assert.Contains("invitation id", Assert.Throws<PairingCodeException>(() => InviteLink.Parse($"https://remote.enactive.dev/pair#v=2&p={secret}")).Message);
    }

    [Fact]
    public void An_enrollment_with_a_swapped_public_key_does_not_verify()
    {
        using var device = P256.Generate();
        using var swapped = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var mac = Enrollment.Mac(pairKey, "inv-1", "dev-1", P256.PublicRaw(device));
        Assert.True(Enrollment.Verify(pairKey, "inv-1", "dev-1", P256.PublicRaw(device), mac));
        Assert.False(Enrollment.Verify(pairKey, "inv-1", "dev-1", P256.PublicRaw(swapped), mac));
    }

    [Fact]
    public void An_enrollment_is_bound_to_its_invitation_its_device_and_its_pair_key()
    {
        using var device = P256.Generate();
        var dPub = P256.PublicRaw(device);
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        var mac = Enrollment.Mac(pairKey, "inv-1", "dev-1", dPub);
        Assert.False(Enrollment.Verify(pairKey, "inv-2", "dev-1", dPub, mac));
        Assert.False(Enrollment.Verify(pairKey, "inv-1", "dev-2", dPub, mac));
        Assert.False(Enrollment.Verify(RemoteKdf.Derive(new byte[32].Select(_ => (byte)1).ToArray(), RemoteKdf.Pair), "inv-1", "dev-1", dPub, mac));
    }

    /// <summary>The MAC arrives from the gateway, so garbage in that field must be a "no", not an exception that escapes the handler.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("AAAA")]
    public void A_malformed_enrollment_mac_does_not_verify_and_does_not_throw(string mac)
    {
        using var device = P256.Generate();
        var pairKey = RemoteKdf.Derive(new byte[32], RemoteKdf.Pair);
        Assert.False(Enrollment.Verify(pairKey, "inv-1", "dev-1", P256.PublicRaw(device), mac));
    }

    // Each builder's theory changes exactly one argument per case; a builder that dropped a field from the associated data would leave one case equal to the baseline, and a gateway could then swap that field between records without the browser noticing.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Task_associated_data_changes_with_each_argument(int changed)
    {
        var baseline = Ad.Task("h", "t", "w");
        Assert.NotEqual(baseline, Ad.Task(changed == 0 ? "h2" : "h", changed == 1 ? "t2" : "t", changed == 2 ? "w2" : "w"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Command_associated_data_changes_with_each_argument(int changed)
    {
        var baseline = Ad.Command("h", "c", CommandKind.StartTask);
        Assert.NotEqual(baseline, Ad.Command(
            changed == 0 ? "h2" : "h", changed == 1 ? "c2" : "c", changed == 2 ? CommandKind.CancelRun : CommandKind.StartTask));
    }

    /// <summary>The kind is what stops a sealed start being replayed as a cancel (or the reverse) under the same command id.</summary>
    [Fact]
    public void A_start_command_and_a_cancel_command_never_share_associated_data()
        => Assert.NotEqual(Ad.Command("h", "c", CommandKind.StartTask), Ad.Command("h", "c", CommandKind.CancelRun));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Event_associated_data_changes_with_each_argument(int changed)
    {
        var baseline = Ad.Event("h", "r", 1, RemoteEventKind.Progress);
        Assert.NotEqual(baseline, Ad.Event(
            changed == 0 ? "h2" : "h", changed == 1 ? "r2" : "r", changed == 2 ? 2 : 1,
            changed == 3 ? RemoteEventKind.Completed : RemoteEventKind.Progress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Approval_associated_data_changes_with_each_argument(int changed)
    {
        var baseline = Ad.Approval("h", "r", "a", "tc", "hash", true);
        Assert.NotEqual(baseline, Ad.Approval(
            changed == 0 ? "h2" : "h", changed == 1 ? "r2" : "r", changed == 2 ? "a2" : "a",
            changed == 3 ? "tc2" : "tc", changed == 4 ? "hash2" : "hash", changed != 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Workspace_associated_data_changes_with_each_argument(int changed)
    {
        var baseline = Ad.Workspace("h", "w");
        Assert.NotEqual(baseline, Ad.Workspace(changed == 0 ? "h2" : "h", changed == 1 ? "w2" : "w"));
    }

    /// <summary>One record's associated data must never open another record's ciphertext, even when the ids line up.</summary>
    [Fact]
    public void Records_of_different_kinds_never_share_associated_data()
    {
        var all = new[]
        {
            Ad.Task("h", "x", "y"), Ad.Workspace("h", "x"), Ad.Command("h", "x", CommandKind.StartTask),
            Ad.Event("h", "x", 1, RemoteEventKind.Running), Ad.Approval("h", "x", "y", "z", "a", true)
        };
        Assert.Equal(all.Length, all.Select(Convert.ToBase64String).Distinct().Count());
    }

    [Fact]
    public void Associated_data_is_the_canonical_bytes_of_the_documented_fields_in_order()
    {
        Assert.Equal(Canonical.Bytes("enactive-task-v1", "h", "t", "w"), Ad.Task("h", "t", "w"));
        Assert.Equal(Canonical.Bytes("enactive-cmd-v1", "h", "c", "CancelRun"), Ad.Command("h", "c", CommandKind.CancelRun));
        Assert.Equal(Canonical.Bytes("enactive-event-v1", "h", "r", "12", "Completed"), Ad.Event("h", "r", 12, RemoteEventKind.Completed));
        Assert.Equal(Canonical.Bytes("enactive-approval-v1", "h", "r", "a", "tc", "hash", "1"), Ad.Approval("h", "r", "a", "tc", "hash", true));
        Assert.Equal(Canonical.Bytes("enactive-approval-v1", "h", "r", "a", "tc", "hash", "0"), Ad.Approval("h", "r", "a", "tc", "hash", false));
        Assert.Equal(Canonical.Bytes("enactive-workspace-v1", "h", "w"), Ad.Workspace("h", "w"));
    }

    private static T RoundTrip<T>(T value)
    {
        var back = RemoteJson.Deserialize<T>(RemoteJson.Serialize(value));
        Assert.Equal(value, back);
        return back;
    }

    [Fact]
    public void Every_sealed_record_round_trips_through_remote_json()
    {
        var at = new DateTimeOffset(2026, 10, 1, 12, 30, 15, TimeSpan.Zero).AddTicks(1234);
        RoundTrip(new SealedTask("Title", "Prompt\nwith \"quotes\" and é"));
        RoundTrip(new StartAuthorization("t", "w", at));
        RoundTrip(new CancelAuthorization("r", at));
        RoundTrip(new DecisionAuthorization("a", "hash", RemoteDecision.Deny, at));
        RoundTrip(new DeviceRevocation("d", at));
        RoundTrip(new DeviceEndorsement("d", "pub", "Phone", at));
        RoundTrip(new SealedAction("run_command", "{\"cmd\":\"ls\"}", "ls -la", "C:\\work", "Listing"));
    }

    [Fact]
    public void A_decision_travels_as_its_name()
        => Assert.Contains("\"decision\":\"Allow\"", RemoteJson.Serialize(new DecisionAuthorization("a", "h", RemoteDecision.Allow, DateTimeOffset.UnixEpoch)));
}

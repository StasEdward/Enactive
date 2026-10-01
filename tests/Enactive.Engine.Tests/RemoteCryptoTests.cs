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
}

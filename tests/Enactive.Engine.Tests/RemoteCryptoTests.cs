namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
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
}

namespace Enactive.Engine.Tests;

using Enactive.Secrets;
using Xunit;

/// <summary>
/// Secrets at rest.
///
/// <para><c>Protect</c> used to fall back to returning the PLAINTEXT when DPAPI failed — "never lose
/// the value over an encryption glitch" — and its caller stored that in a field named
/// <c>ApiKeyProtected</c>. So an encryption failure wrote the API key into settings.json in the
/// clear, silently, and the next load read it back as a "legacy plaintext" value and carried on
/// happily for ever. Losing a value the user can retype is a nuisance; writing their key to disk
/// unencrypted without telling them is not a trade to make on their behalf.</para>
///
/// <para>The guarantee is now structural: the only things <c>Protect</c> can return are an empty
/// string and a "dpapi:" ciphertext. Everything else throws. The DPAPI failure itself cannot be
/// provoked from a test on a healthy Windows box, so what is pinned here is that invariant on every
/// path a test CAN reach, plus the round trip and the detection of a plaintext leftover.</para>
/// </summary>
public sealed class SecretTests
{
    [Fact]
    public void An_empty_secret_stays_empty()
    {
        Assert.Equal(string.Empty, Secret.Protect(null));
        Assert.Equal(string.Empty, Secret.Protect(""));
        Assert.False(Secret.IsProtected(string.Empty));
    }

    [Fact]
    public void What_protect_returns_is_never_the_plaintext()
    {
        const string key = "sk-ant-not-a-real-key-0123456789";

        if (!OperatingSystem.IsWindows())
        {
            // No DPAPI here, so there is nowhere safe to put it. Refusing is the answer; handing the
            // caller the plaintext to store is the bug.
            var refused = Assert.Throws<SecretProtectionException>(() => Secret.Protect(key));
            Assert.DoesNotContain(key, refused.Message, StringComparison.Ordinal);
            return;
        }

        var stored = Secret.Protect(key);

        Assert.True(Secret.IsProtected(stored), "a stored secret must be recognisably encrypted");
        Assert.DoesNotContain(key, stored, StringComparison.Ordinal);
    }

    [Fact]
    public void A_protected_secret_comes_back_exactly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string key = "sk-ant-not-a-real-key-строка-0123456789";

        Assert.Equal(key, Secret.Unprotect(Secret.Protect(key)));
    }

    // The migration path: a value written before any of this existed still loads, so nobody is
    // locked out of their own settings by a fix.
    [Fact]
    public void A_legacy_plaintext_value_still_loads_and_is_recognised_as_unencrypted()
    {
        const string legacy = "sk-ant-written-by-an-older-build";

        Assert.Equal(legacy, Secret.Unprotect(legacy));
        Assert.False(Secret.IsProtected(legacy), "it must be reportable as the leftover it is");
    }

    // Wrong user, wrong machine, or a truncated file: not this account's secret, so it is no secret
    // at all. Never a partial or guessed value.
    [Fact]
    public void A_ciphertext_this_account_cannot_read_is_no_key_at_all()
    {
        Assert.Equal(string.Empty, Secret.Unprotect("dpapi:not-actually-base64-ciphertext"));
        Assert.Equal(string.Empty, Secret.Unprotect("dpapi:AAAAAAAAAAAAAAAAAAAAAA=="));
    }

    [Fact]
    public void Nothing_stored_is_nothing_read()
    {
        Assert.Equal(string.Empty, Secret.Unprotect(null));
        Assert.Equal(string.Empty, Secret.Unprotect(""));
    }
}

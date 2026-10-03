namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Accounts;

public sealed class AdmissionIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github")]
    [InlineData("GitHub:123")]
    [InlineData("github:")]
    [InlineData("github:123\n")]
    [InlineData("github:123\r\n")]
    [InlineData("github:12 3")]
    [InlineData("github:é")]
    public void Invalid_identities_cannot_reach_the_shared_service(string? text)
    {
        Assert.False(AdmissionIdentity.TryParse(text, out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void Identity_preserves_subject_case_colons_and_database_widths()
    {
        var provider = new string('a', 20);
        var subject = "A:B" + new string('x', 252);
        Assert.True(AdmissionIdentity.TryParse(provider + ":" + subject, out var identity));
        Assert.Equal(provider, identity.Provider);
        Assert.Equal(subject, identity.Subject);
        Assert.Equal(provider + ":" + subject, identity.ToString());
        Assert.False(AdmissionIdentity.TryParse(provider + "a:" + subject, out _));
        Assert.False(AdmissionIdentity.TryParse(provider + ":" + subject + "x", out _));
    }
}

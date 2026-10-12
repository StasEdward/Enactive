namespace Enactive.Server.Tests.Components;

using Enactive.Server.Components.Ui;

/// <summary>
/// The repository strip asks GitHub about the branch's pull request by running <c>gh</c> as the service's account: a click
/// asks again, but not more often than every few seconds - anybody signed in can click, and every click was a process on
/// the server and a call against the service's GitHub rate limit.
/// </summary>
public sealed class RepoBarTests
{
    [Fact]
    public void A_click_asks_github_again_only_after_a_few_seconds()
    {
        Assert.False(RepoBar.AsksPullRequest(clicked: true, TimeSpan.FromSeconds(1)));
        Assert.False(RepoBar.AsksPullRequest(clicked: true, RepoBar.ClickedEvery - TimeSpan.FromMilliseconds(1)));
        Assert.True(RepoBar.AsksPullRequest(clicked: true, RepoBar.ClickedEvery));
        Assert.InRange(RepoBar.ClickedEvery, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Without_a_click_github_is_asked_every_two_minutes()
    {
        Assert.False(RepoBar.AsksPullRequest(clicked: false, RepoBar.ClickedEvery));
        Assert.True(RepoBar.AsksPullRequest(clicked: false, TimeSpan.FromMinutes(2)));
    }
}

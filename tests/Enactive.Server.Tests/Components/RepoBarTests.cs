namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Server.Components.Ui;
using Enactive.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Model = Enactive.Application.Projections.RepoBar;

/// <summary>
/// The repository strip asks GitHub about the branch's pull request by running <c>gh</c> as the service's account: a click
/// asks again, but not more often than every few seconds - anybody signed in can click, and every click was a process on
/// the server and a call against the service's GitHub rate limit. The reading is the window's (RepoBar.ReadAsync), with
/// the service's floor under a click.
/// </summary>
public sealed class RepoBarTests
{
    private static readonly Model Strip = new(RepoBar.ClickedEvery);

    [Fact]
    public void A_click_asks_github_again_only_after_a_few_seconds()
    {
        Assert.False(Strip.AsksPullRequest(asked: true, TimeSpan.FromSeconds(1)));
        Assert.False(Strip.AsksPullRequest(asked: true, RepoBar.ClickedEvery - TimeSpan.FromMilliseconds(1)));
        Assert.True(Strip.AsksPullRequest(asked: true, RepoBar.ClickedEvery));
        Assert.InRange(RepoBar.ClickedEvery, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Without_a_click_github_is_asked_every_two_minutes()
    {
        Assert.False(Strip.AsksPullRequest(asked: false, RepoBar.ClickedEvery));
        Assert.True(Strip.AsksPullRequest(asked: false, TimeSpan.FromMinutes(2)));
    }
}

/// <summary>
/// The service's floor under asking GitHub is the service's, not each page's: a strip's own clock went with the page, so
/// a reload or the same run open in another tab asked <c>gh pr view</c> at once. Every strip asks through one place
/// (RepoReads), and a folder's branch is asked at most every fifteen seconds by all of them together.
/// </summary>
public sealed class RepoBarSharedReadingTests : ComponentHost
{
    [Fact]
    public void Two_strips_on_one_repository_ask_github_once_within_the_floor()
    {
        var clock = new Clock();
        var asked = 0;
        Services.AddSingleton(new RepoReads(
            (_, _) => Task.FromResult<RepoStatus?>(new RepoStatus("o/r", "main", 0, 0, 0, 0, 0, "o/r")),
            (_, _) => { Interlocked.Increment(ref asked); return Task.FromResult<PullRequestStatus?>(new PullRequestStatus(7, "https://github.com/o/r/pull/7", 1, 1, CiState.Passing)); },
            clock));
        var root = Path.GetTempPath();

        var first = Render<RepoBar>(p => p.Add(x => x.Root, root));
        first.WaitForAssertion(() => Assert.Contains("#7", first.Markup));
        // A second tab, or the page reloaded: shown the pull request, and GitHub was not asked again.
        var second = Render<RepoBar>(p => p.Add(x => x.Root, root));
        second.WaitForAssertion(() => Assert.Contains("#7", second.Markup));
        Assert.Equal(1, asked);

        // Past the floor a click asks; the other strip's click right after it is given that answer, not a second ask.
        clock.Now += RepoBar.ClickedEvery;
        first.Find(".repo-again").Click();
        first.WaitForAssertion(() => Assert.Equal(2, asked));
        second.Find(".repo-again").Click();
        second.WaitForAssertion(() => Assert.Contains("#7", second.Markup));
        Assert.Equal(2, asked);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

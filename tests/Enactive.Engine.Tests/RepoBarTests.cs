namespace Enactive.Engine.Tests;

using Enactive.Application.Projections;
using Enactive.Workspace;
using Xunit;

/// <summary>The strip at the foot of the window, as the window and the service both say it (RepoBar).</summary>
public sealed class RepoBarTests
{
    private static RepoStatus Repo(string? branch = "refactor_v2", int? ahead = 2, int? behind = 0, int added = 120, int removed = 45, int untracked = 3)
        => new("StasEdward/Enactive", branch, ahead, behind, added, removed, untracked, "StasEdward/Enactive");

    [Fact]
    public void Without_a_repository_there_is_no_strip()
    {
        var bar = new RepoBar();
        bar.Show(null);

        Assert.False(bar.IsVisible);
        Assert.False(bar.HasPullRequest);
    }

    [Fact]
    public void A_repository_is_said_in_a_line()
    {
        var bar = new RepoBar();
        bar.Show(Repo());

        Assert.True(bar.IsVisible);
        Assert.Equal(("StasEdward/Enactive", "refactor_v2", "↑2", "+120 −45 · 3 new"), (bar.Name, bar.Branch, bar.Sync, bar.Changes));
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData(0, 0, "")]
    [InlineData(0, 4, "↓4")]
    [InlineData(1, 1, "↑1 ↓1")]
    public void How_far_from_its_upstream(int? ahead, int? behind, string said)
    {
        var bar = new RepoBar();
        bar.Show(Repo(ahead: ahead, behind: behind));

        Assert.Equal(said, bar.Sync);
    }

    [Fact]
    public void Nothing_changed_is_said_so_and_a_detached_head_too()
    {
        var bar = new RepoBar();
        bar.Show(Repo(branch: null, added: 0, removed: 0, untracked: 0));

        Assert.Equal(("detached", "no changes"), (bar.Branch, bar.Changes));
    }

    [Fact]
    public void The_pull_request_and_its_checks()
    {
        var bar = new RepoBar();
        bar.Show(Repo());
        bar.ShowPullRequest(new PullRequestStatus(52, "https://github.com/StasEdward/Enactive/pull/52", 10625, 4540, CiState.Failing));

        Assert.Equal(("#52", "+10,625 −4,540", "CI failing", true), (bar.PullRequest, bar.PullRequestChanges, bar.CiText, bar.HasCi));
    }

    /// <summary>Each part on its own, for its own colour: added, removed, new files - or "no changes".</summary>
    [Fact]
    public void The_changes_come_in_parts()
    {
        var bar = new RepoBar();
        bar.Show(Repo(added: 46, removed: 45, untracked: 4));
        Assert.Equal(("+46", "−45", "· 4 new", ""), (bar.Added, bar.Removed, bar.NewFiles, bar.NoChanges));

        bar.Show(Repo(added: 0, removed: 0, untracked: 2));
        Assert.Equal(("", "", "2 new", ""), (bar.Added, bar.Removed, bar.NewFiles, bar.NoChanges));

        bar.Show(Repo(added: 0, removed: 0, untracked: 0));
        Assert.Equal(("", "", "", "no changes"), (bar.Added, bar.Removed, bar.NewFiles, bar.NoChanges));

        bar.ShowPullRequest(new PullRequestStatus(52, "https://github.com/x/y/pull/52", 10625, 4540, CiState.Passing));
        Assert.Equal(("+10,625", "−4,540"), (bar.PullRequestAdded, bar.PullRequestRemoved));
    }

    /// <summary>Another branch is another pull request: the old one is not shown beside it while the new is asked.</summary>
    [Fact]
    public void A_different_branch_forgets_the_pull_request()
    {
        var bar = new RepoBar();
        bar.Show(Repo());
        bar.ShowPullRequest(new PullRequestStatus(52, "https://github.com/x/y/pull/52", 1, 1, CiState.Passing));

        bar.Show(Repo(branch: "master"));

        Assert.False(bar.HasPullRequest);
        Assert.Equal(CiState.None, bar.Ci);
    }

    [Theory]
    [InlineData("https://github.com/StasEdward/Enactive/pull/12", true)]
    [InlineData("https://GitHub.com/StasEdward/Enactive/pull/12/checks", true)]
    [InlineData("http://github.com/StasEdward/Enactive/pull/12", false)]
    [InlineData("https://github.com.example.org/pull/12", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Only_a_github_page_over_https_is_opened(string? url, bool opened)
        => Assert.Equal(opened, RepoBar.GitHubPage(url) is not null);

    /// <summary>
    /// The reading both hosts do, in one place: a read started after another wins - a slow answer about the folder left
    /// behind must not overwrite the one now open. It was written in the window and again in the service's strip.
    /// </summary>
    [Fact]
    public async Task A_read_started_later_wins_over_a_slower_one()
    {
        var slow = new TaskCompletionSource<RepoStatus?>();
        var bar = new RepoBar(read: (root, _) => root == "left" ? slow.Task : Task.FromResult<RepoStatus?>(Repo(branch: "open")),
            readPullRequest: (_, _, _) => Task.FromResult<PullRequestStatus?>(null));

        var left = bar.ReadAsync("left", asked: false);
        await bar.ReadAsync("open", asked: false);
        slow.SetResult(Repo(branch: "left-behind"));
        await left;

        Assert.Equal("open", bar.Branch);
    }

    /// <summary>
    /// GitHub is asked at most every two minutes while nobody asks; a click asks at once in the window (its floor is
    /// zero - one person at it), and not more often than the host's floor where it has one. Another folder is asked about
    /// at once (Moved).
    /// </summary>
    [Fact]
    public async Task Github_is_asked_every_two_minutes_and_on_a_click_above_the_host_s_floor()
    {
        var clock = new Clock();
        var asked = 0;
        RepoBar Bar(TimeSpan floor) => new(floor, (_, _) => Task.FromResult<RepoStatus?>(Repo()),
            (_, _, _) => { asked++; return Task.FromResult<PullRequestStatus?>(new PullRequestStatus(7, "https://github.com/o/r/pull/7", 1, 1, CiState.Passing)); },
            clock);

        var window = Bar(TimeSpan.Zero);
        await window.ReadAsync("w", asked: false);
        Assert.Equal((1, "#7"), (asked, window.PullRequest));
        await window.ReadAsync("w", asked: false);
        Assert.Equal(1, asked);
        await window.ReadAsync("w", asked: true);
        Assert.Equal(2, asked);
        clock.Now += RepoBar.PullRequestEvery;
        await window.ReadAsync("w", asked: false);
        Assert.Equal(3, asked);

        asked = 0;
        var service = Bar(TimeSpan.FromSeconds(15));
        await service.ReadAsync("w", asked: false);
        await service.ReadAsync("w", asked: true);
        Assert.Equal(1, asked);
        clock.Now += TimeSpan.FromSeconds(15);
        await service.ReadAsync("w", asked: true);
        Assert.Equal(2, asked);
        service.Moved();
        await service.ReadAsync("elsewhere", asked: false);
        Assert.Equal(3, asked);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

namespace Enactive.App.Ui.Tests;

using Enactive.App.Ui.ViewModels;
using Enactive.Workspace;

/// <summary>The strip at the foot of the window, as its view model says it.</summary>
public sealed class RepoBarTests
{
    private static RepoStatus Repo(string? branch = "refactor_v2", int? ahead = 2, int? behind = 0, int added = 120, int removed = 45, int untracked = 3)
        => new("StasEdward/Enactive", branch, ahead, behind, added, removed, untracked, "StasEdward/Enactive");

    [Fact]
    public void Without_a_repository_there_is_no_strip()
    {
        var bar = new RepoBarViewModel();
        bar.Show(null);

        Assert.False(bar.IsVisible);
        Assert.False(bar.HasPullRequest);
    }

    [Fact]
    public void A_repository_is_said_in_a_line()
    {
        var bar = new RepoBarViewModel();
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
        var bar = new RepoBarViewModel();
        bar.Show(Repo(ahead: ahead, behind: behind));

        Assert.Equal(said, bar.Sync);
    }

    [Fact]
    public void Nothing_changed_is_said_so_and_a_detached_head_too()
    {
        var bar = new RepoBarViewModel();
        bar.Show(Repo(branch: null, added: 0, removed: 0, untracked: 0));

        Assert.Equal(("detached", "no changes"), (bar.Branch, bar.Changes));
    }

    [Fact]
    public void The_pull_request_and_its_checks()
    {
        var bar = new RepoBarViewModel();
        bar.Show(Repo());
        bar.ShowPullRequest(new PullRequestStatus(52, "https://github.com/StasEdward/Enactive/pull/52", 10625, 4540, CiState.Failing));

        Assert.Equal(("#52", "+10,625 −4,540", "CI failing", true), (bar.PullRequest, bar.PullRequestChanges, bar.CiText, bar.HasCi));
    }

    /// <summary>Each part on its own, for its own colour: added, removed, new files - or "no changes".</summary>
    [Fact]
    public void The_changes_come_in_parts()
    {
        var bar = new RepoBarViewModel();
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
        var bar = new RepoBarViewModel();
        bar.Show(Repo());
        bar.ShowPullRequest(new PullRequestStatus(52, "https://github.com/x/y/pull/52", 1, 1, CiState.Passing));

        bar.Show(Repo(branch: "master"));

        Assert.False(bar.HasPullRequest);
        Assert.Equal(CiState.None, bar.Ci);
    }
}

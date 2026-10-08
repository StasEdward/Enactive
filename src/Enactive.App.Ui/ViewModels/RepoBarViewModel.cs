namespace Enactive.App.Ui.ViewModels;

using System.Globalization;
using Enactive.App.Ui.Mvvm;
using Enactive.Workspace;

/// <summary>
/// The strip at the foot of the window: the workspace's repository, its branch, how far it is from its upstream, what is
/// changed and not committed - and the branch's pull request with its checks, when GitHub can be asked. What a person sees
/// of the folder's history without leaving the window. Read-only: nothing here changes the repository.
/// </summary>
internal sealed class RepoBarViewModel : ObservableObject
{
    private RepoStatus? _repo;
    private PullRequestStatus? _pullRequest;

    /// <summary>Whether there is a repository to show. Not in one, or without git, the strip is not there.</summary>
    public bool IsVisible => _repo is not null;

    public string Name => _repo?.Name ?? "";

    /// <summary>The branch, or "detached" when the head is not on one.</summary>
    public string Branch => _repo is null ? "" : _repo.Branch ?? "detached";

    /// <summary>"↑2 ↓1" against the upstream; empty without one, or when level with it.</summary>
    public string Sync => _repo is { Ahead: { } ahead, Behind: { } behind } && (ahead > 0 || behind > 0)
        ? string.Join(" ", new[] { ahead > 0 ? $"↑{ahead}" : null, behind > 0 ? $"↓{behind}" : null }.OfType<string>())
        : "";

    /// <summary>"+120 −45 · 3 new" since the last commit; "no changes" when there are none.</summary>
    public string Changes
    {
        get
        {
            if (_repo is null) return "";
            var parts = new List<string>();
            if (_repo.Added > 0 || _repo.Removed > 0) parts.Add($"+{Count(_repo.Added)} −{Count(_repo.Removed)}");
            if (_repo.Untracked > 0) parts.Add($"{Count(_repo.Untracked)} new");
            return parts.Count == 0 ? "no changes" : string.Join(" · ", parts);
        }
    }

    public bool HasPullRequest => _pullRequest is not null;
    public string PullRequest => _pullRequest is { } pr ? $"#{pr.Number}" : "";
    public string PullRequestChanges => _pullRequest is { } pr ? $"+{Count(pr.Additions)} −{Count(pr.Deletions)}" : "";
    public string? PullRequestUrl => _pullRequest?.Url;

    /// <summary>Where the pull request's checks stand - what the CI light's colour says (Palette.Ci).</summary>
    public CiState Ci => _pullRequest?.Ci ?? CiState.None;

    /// <summary>The checks in a word, beside the light; empty when the pull request reports none.</summary>
    public string CiText => Ci switch
    {
        CiState.Passing => "CI passing",
        CiState.Failing => "CI failing",
        CiState.Pending => "CI running",
        _ => ""
    };

    public bool HasCi => Ci != CiState.None;

    /// <summary>The repository as it now stands - or null, and the strip goes. A different repository forgets the pull request.</summary>
    public void Show(RepoStatus? repo)
    {
        if (repo is null || _repo?.GitHub != repo.GitHub || _repo?.Branch != repo.Branch)
            _pullRequest = null;
        _repo = repo;
        Changed();
    }

    /// <summary>The branch's pull request as GitHub said it - or null, when it has none or could not be asked.</summary>
    public void ShowPullRequest(PullRequestStatus? pullRequest)
    {
        _pullRequest = _repo is null ? null : pullRequest;
        Changed();
    }

    private void Changed()
    {
        foreach (var name in new[] { nameof(IsVisible), nameof(Name), nameof(Branch), nameof(Sync), nameof(Changes), nameof(HasPullRequest),
                     nameof(PullRequest), nameof(PullRequestChanges), nameof(PullRequestUrl), nameof(Ci), nameof(CiText), nameof(HasCi) })
            OnPropertyChanged(name);
    }

    private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

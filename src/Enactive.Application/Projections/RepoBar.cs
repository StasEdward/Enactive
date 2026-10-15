namespace Enactive.Application.Projections;

using System.Globalization;
using Enactive.Application.Mvvm;
using Enactive.Workspace;

/// <summary>
/// The strip at the foot of the window: the workspace's repository, its branch, how far it is from its upstream, what is
/// changed and not committed - and the branch's pull request with its checks, when GitHub can be asked. What a person sees
/// of the folder's history without leaving the window. Read-only: nothing here changes the repository.
///
/// <para>Moved from the window (it was RepoBarViewModel), unchanged, so the service's live run shows the same strip from
/// the same reading of the repository - a second copy of what "↑2 ↓1" or "no changes" means would drift. So is the
/// reading itself (<see cref="ReadAsync"/>): which read wins and how often GitHub is asked were written once in the window
/// and again in the service's strip.</para>
/// </summary>
internal sealed class RepoBar : ObservableObject
{
    /// <summary>GitHub is asked at most this often unless the person asks: a network call, and a strip is not worth a rate limit.</summary>
    public static readonly TimeSpan PullRequestEvery = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _askedEvery;
    private readonly Func<string, CancellationToken, Task<RepoStatus?>> _read;
    private readonly Func<string, RepoStatus, CancellationToken, Task<PullRequestStatus?>> _readPullRequest;
    private readonly TimeProvider _clock;
    private RepoStatus? _repo;
    private PullRequestStatus? _pullRequest;
    private int _reads;
    private DateTimeOffset _pullRequestReadAt = DateTimeOffset.MinValue;

    /// <param name="askedEvery">
    /// How often a read the person asked for (a click) may ask GitHub: zero, every time - the window's, whose person is the
    /// only one at it. A host where anybody signed in can click (the service, as its own account) gives a floor.
    /// </param>
    /// <param name="read">Reads the repository; git, unless a test stands in for it.</param>
    /// <param name="readPullRequest">
    /// Asks GitHub about the pull request of the branch of the folder (the first argument); gh, unless a host shares its
    /// asking between strips (the service's, one clock for every page) or a test stands in for it.
    /// </param>
    /// <param name="clock">The time, for how long ago GitHub was asked; a test's own.</param>
    public RepoBar(TimeSpan askedEvery = default,
        Func<string, CancellationToken, Task<RepoStatus?>>? read = null,
        Func<string, RepoStatus, CancellationToken, Task<PullRequestStatus?>>? readPullRequest = null,
        TimeProvider? clock = null)
    {
        _askedEvery = askedEvery;
        _read = read ?? RepoStatusReader.ReadAsync;
        _readPullRequest = readPullRequest ?? ((_, repo, ct) => RepoStatusReader.ReadPullRequestAsync(repo, ct));
        _clock = clock ?? TimeProvider.System;
    }

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

    // The parts of the line above, each in its own colour (2026-10-09): what was added green, what was removed red, as git
    // and GitHub show them; new files and "no changes" plain. Empty when the part has nothing to say.
    public string Added => _repo is { } r && (r.Added > 0 || r.Removed > 0) ? $"+{Count(r.Added)}" : "";
    public string Removed => _repo is { } r && (r.Added > 0 || r.Removed > 0) ? $"−{Count(r.Removed)}" : "";
    public string NewFiles => _repo is { Untracked: > 0 } r ? (r.Added > 0 || r.Removed > 0 ? "· " : "") + $"{Count(r.Untracked)} new" : "";
    public string NoChanges => _repo is { Added: 0, Removed: 0, Untracked: 0 } ? "no changes" : "";
    public string PullRequestAdded => _pullRequest is { } pr ? $"+{Count(pr.Additions)}" : "";
    public string PullRequestRemoved => _pullRequest is { } pr ? $"−{Count(pr.Deletions)}" : "";

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

    /// <summary>
    /// The page the strip opens for an address, or null: only a github.com page over https, which is what the strip shows -
    /// an address read from a repository's remote is the repository's to choose, and a link to anywhere else from a
    /// trusted strip would be one click from a page nobody asked for. Moved from the window (OpenRepoPage) with the strip.
    /// </summary>
    public static Uri? GitHubPage(string? url)
        => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    /// <summary>Whether GitHub is asked now, <paramref name="since"/> it last was - asked for by the person, or not.</summary>
    public bool AsksPullRequest(bool asked, TimeSpan since) => since >= (asked ? _askedEvery : PullRequestEvery);

    /// <summary>
    /// The strip is another folder's now: its pull request is asked about at the next read, as when GitHub was last asked
    /// says nothing about this one.
    /// </summary>
    public void Moved() => _pullRequestReadAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Reads the repository of <paramref name="root"/> into the strip - and its pull request when two minutes have passed
    /// since GitHub was last asked, or the person asked (<paramref name="asked"/>) and the host's floor allows. A read
    /// started after this one wins: a slow answer about the folder left behind must not overwrite the one now open.
    /// <paramref name="drawn"/> is called each time the strip changed, on the caller's context - the awaits here keep it.
    /// </summary>
    public async Task ReadAsync(string? root, bool asked, Action? drawn = null, CancellationToken ct = default)
    {
        var read = ++_reads;
        var repo = root is null ? null : await _read(root, ct);
        if (read != _reads || ct.IsCancellationRequested)
            return;
        Show(repo);
        drawn?.Invoke();
        if (root is null || repo?.GitHub is null || !AsksPullRequest(asked, _clock.GetUtcNow() - _pullRequestReadAt))
            return;
        _pullRequestReadAt = _clock.GetUtcNow();
        var pullRequest = await _readPullRequest(root, repo, ct);
        if (read != _reads || ct.IsCancellationRequested)
            return;
        ShowPullRequest(pullRequest);
        drawn?.Invoke();
    }

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
                     nameof(PullRequest), nameof(PullRequestChanges), nameof(PullRequestUrl), nameof(Ci), nameof(CiText), nameof(HasCi),
                     nameof(Added), nameof(Removed), nameof(NewFiles), nameof(NoChanges), nameof(PullRequestAdded), nameof(PullRequestRemoved) })
            OnPropertyChanged(name);
    }

    private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

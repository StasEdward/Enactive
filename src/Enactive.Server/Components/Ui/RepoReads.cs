namespace Enactive.Server.Components.Ui;

using Enactive.Workspace;

/// <summary>
/// How the service's repository strips read a repository and ask GitHub about its branch's pull request - one for the
/// whole service, every page's strip asking through it. Each ask is a <c>gh pr view</c> started as the service's account
/// against its GitHub sign-in, and a strip's own clock (RepoBar's floor under a click) went with the page: a reload, or
/// the same run open in another tab, asked at once, so anybody signed in could start as many as they could reload.
/// Here a folder's branch is asked at most every <see cref="AskedEvery"/> by all of them together, and a strip asking
/// within that is given the answer already asked for - the pull request still shows on a fresh page.
/// </summary>
internal sealed class RepoReads
{
    /// <summary>
    /// How often GitHub is asked about one folder's branch, whoever asks: the service's floor under a click, which a
    /// strip's own two minutes between unasked reads are above.
    /// </summary>
    public static readonly TimeSpan AskedEvery = TimeSpan.FromSeconds(15);

    // Folders are compared as the file system names them: on Windows C:\Repo and c:\repo\ are one folder, and two keys for
    // it were two clocks.
    private static readonly StringComparer Folders = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Func<string, CancellationToken, Task<RepoStatus?>> _read;
    private readonly Func<RepoStatus, CancellationToken, Task<PullRequestStatus?>> _readPullRequest;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Asked> _asked = new(Folders);

    /// <param name="read">Reads a repository; git, unless a test stands in for it.</param>
    /// <param name="readPullRequest">Asks GitHub; gh, unless a test stands in for it.</param>
    /// <param name="clock">The time; a test's own.</param>
    public RepoReads(Func<string, CancellationToken, Task<RepoStatus?>>? read = null,
        Func<RepoStatus, CancellationToken, Task<PullRequestStatus?>>? readPullRequest = null,
        TimeProvider? clock = null)
    {
        _read = read ?? RepoStatusReader.ReadAsync;
        _readPullRequest = readPullRequest ?? RepoStatusReader.ReadPullRequestAsync;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The service's time, which a strip's own clock (RepoBar's) is to keep too: two clocks would disagree in a test.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>The repository of a folder, read now: local git, which a strip reads whenever its files may have changed.</summary>
    public Task<RepoStatus?> ReadAsync(string root, CancellationToken ct) => _read(root, ct);

    /// <summary>
    /// The pull request of <paramref name="repo"/>'s branch in <paramref name="root"/>: asked of GitHub when nobody asked
    /// about that folder's branch in the last <see cref="AskedEvery"/>, else the answer that was asked for then.
    /// </summary>
    public Task<PullRequestStatus?> PullRequestAsync(string root, RepoStatus repo, CancellationToken ct)
    {
        var key = Key(root, repo.Branch);
        Task<PullRequestStatus?> answer;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            // A failed ask is not kept: the next strip asks again rather than being handed the same failure.
            if (!_asked.TryGetValue(key, out var last) || now - last.At >= AskedEvery || last.Answer.IsFaulted || last.Answer.IsCanceled)
            {
                // Asked with no page's token: the answer is shared, and the page that asked first going away must not
                // cancel it under another one waiting for it. gh's own fifteen seconds bound it (ReadPullRequestAsync).
                last = new Asked(now, _readPullRequest(repo, CancellationToken.None));
                _asked[key] = last;
                Forget(now);
            }
            answer = last.Answer;
        }
        return answer.WaitAsync(ct);
    }

    /// <summary>Forgets the asks too old to answer anybody now: one entry per folder and branch ever shown would only grow.</summary>
    private void Forget(DateTimeOffset now)
    {
        foreach (var (key, asked) in _asked.ToList())
            if (now - asked.At >= AskedEvery)
                _asked.Remove(key);
    }

    private static string Key(string root, string? branch)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + "\n" + (branch ?? "");

    private sealed record Asked(DateTimeOffset At, Task<PullRequestStatus?> Answer);
}

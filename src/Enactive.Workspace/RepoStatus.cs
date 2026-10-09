namespace Enactive.Workspace;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Where the workspace's repository stands, for the strip at the foot of the window: which repository, which branch, how
/// far from its upstream, and what is changed and not committed. Read for a person to see, never handed to a model.
/// </summary>
/// <param name="Name">owner/repo of a GitHub origin, the origin's last part for another host, or the folder's name.</param>
/// <param name="Branch">The branch checked out; null when the head is detached.</param>
/// <param name="Ahead">Commits on the branch its upstream does not have; null when there is no upstream.</param>
/// <param name="Behind">Commits on the upstream the branch does not have; null when there is no upstream.</param>
/// <param name="Added">Lines added since the last commit, staged or not.</param>
/// <param name="Removed">Lines removed since the last commit, staged or not.</param>
/// <param name="Untracked">Files git does not track and does not ignore.</param>
/// <param name="GitHub">owner/repo when the origin is on github.com - what a pull request is asked of; null otherwise.</param>
public sealed record RepoStatus(string Name, string? Branch, int? Ahead, int? Behind, int Added, int Removed, int Untracked,
    string? GitHub = null);

/// <summary>Where a pull request's checks stand: nothing reported, still running, all passed, or one failed.</summary>
public enum CiState { None, Pending, Passing, Failing }

/// <summary>The pull request of the branch checked out, as GitHub says it: its number, its page, its size, its checks.</summary>
public sealed record PullRequestStatus(int Number, string Url, int Additions, int Deletions, CiState Ci);

public static class RepoStatusReader
{
    // Short: this is read every half minute while the window is idle, and a strip that waits on a slow repository is a
    // window that waits on it.
    private const int GitTimeoutMs = 3000;

    /// <summary>
    /// The repository the folder is in, or null when it is not in one (or git is not installed). Through AutomaticGit:
    /// a workspace is somebody's folder, and its own git configuration - an fsmonitor, a hook, a filter - is not run.
    /// </summary>
    public static async Task<RepoStatus?> ReadAsync(string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var (_, said) = await AutomaticGit.RunAsync(root, ["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--abbrev-ref", "HEAD"], ct, GitTimeoutMs);
        var lines = said.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Read by what it printed, not by its exit: a repository without a commit has no HEAD to name, and rev-parse says
        // the rest and fails on that. Not in a repository - or git refusing a configuration it will not run - prints nothing.
        if (lines.Length < 2 || lines[0] != "true") return null;
        var top = lines[1];
        var branch = lines.Length > 2 && lines[2] != "HEAD" ? lines[2] : null;

        var (_, origin) = await AutomaticGit.RunAsync(root, ["config", "--get", "remote.origin.url"], ct, GitTimeoutMs);
        var (aheadExit, counts) = await AutomaticGit.RunAsync(root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"], ct, GitTimeoutMs);
        var (_, numstat) = await AutomaticGit.RunAsync(root, ["diff", "--numstat", "HEAD"], ct, GitTimeoutMs);
        var (_, untracked) = await AutomaticGit.RunAsync(root, ["ls-files", "--others", "--exclude-standard"], ct, GitTimeoutMs);

        var (ahead, behind) = aheadExit == 0 ? ParseAheadBehind(counts) : (null, null);
        var (added, removed) = ParseNumstat(numstat);
        return new RepoStatus(RepoName(origin.Trim(), top), branch, ahead, behind, added, removed,
            untracked.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, GitHubRepo(origin.Trim()));
    }

    /// <summary>`rev-list --left-right --count` prints "ahead&lt;tab&gt;behind".</summary>
    public static (int? Ahead, int? Behind) ParseAheadBehind(string output)
    {
        var parts = output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) ? (a, b) : (null, null);
    }

    /// <summary>`diff --numstat`: added, removed, path per line - "-" for each count of a binary file, which counts nothing.</summary>
    public static (int Added, int Removed) ParseNumstat(string output)
    {
        int added = 0, removed = 0;
        foreach (var line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (int.TryParse(parts[0], out var a)) added += a;
            if (int.TryParse(parts[1], out var r)) removed += r;
        }
        return (added, removed);
    }

    private static readonly Regex GitHubUrl = new(@"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>owner/repo of a github.com origin - https or ssh - or null.</summary>
    public static string? GitHubRepo(string origin)
        => GitHubUrl.Match(origin) is { Success: true } m ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;

    /// <summary>What the repository is called: owner/repo on GitHub, the origin's last part elsewhere, the folder without one.</summary>
    public static string RepoName(string origin, string top)
    {
        if (GitHubRepo(origin) is { } github) return github;
        var last = origin.TrimEnd('/').Split('/', ':').LastOrDefault(p => p.Length > 0);
        if (!string.IsNullOrEmpty(last)) return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
        return Path.GetFileName(top.TrimEnd('/', '\\')) is { Length: > 0 } folder ? folder : top;
    }

    /// <summary>
    /// The pull request of this branch, as `gh` reports it - or null without one, without gh, without its sign-in, or when
    /// it does not answer in time. Asked with the repository and the branch named, from outside the workspace: gh runs git
    /// where it is started, and the workspace's own git configuration is not to be run (see <see cref="ReadAsync"/>).
    /// </summary>
    public static async Task<PullRequestStatus?> ReadPullRequestAsync(RepoStatus repo, CancellationToken ct)
    {
        if (repo.GitHub is not { } github || repo.Branch is not { } branch) return null;
        var psi = new ProcessStartInfo("gh")
        {
            WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var arg in new[] { "pr", "view", branch, "--repo", github, "--json", "number,url,additions,deletions,statusCheckRollup" })
            psi.ArgumentList.Add(arg);
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(lifetime.Token);
            _ = process.StandardError.ReadToEndAsync(lifetime.Token);
            await process.WaitForExitAsync(lifetime.Token);
            return process.ExitCode == 0 ? ParsePullRequest(await output) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }   // gh is not installed
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>What `gh pr view --json number,url,additions,deletions,statusCheckRollup` printed, or null when it is not that.</summary>
    public static PullRequestStatus? ParsePullRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("number", out var number) || !root.TryGetProperty("url", out var url)) return null;
            var checks = root.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array
                ? rollup.EnumerateArray().Select(CheckState).ToArray() : [];
            var ci = checks.Length == 0 ? CiState.None
                : checks.Contains(CiState.Failing) ? CiState.Failing
                : checks.Contains(CiState.Pending) ? CiState.Pending
                : CiState.Passing;
            return new PullRequestStatus(number.GetInt32(), url.GetString() ?? "",
                root.TryGetProperty("additions", out var a) ? a.GetInt32() : 0,
                root.TryGetProperty("deletions", out var d) ? d.GetInt32() : 0, ci);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>
    /// One check: a commit status says a state; a check run says a conclusion once it has finished, and none until then.
    /// Anything that did not succeed - failed, errored, timed out, was cancelled, wants action - is a failure; anything with
    /// no conclusion yet is still running; succeeded, neutral and skipped pass.
    /// </summary>
    private static CiState CheckState(JsonElement check)
    {
        string? Text(string name) => check.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.ToUpperInvariant() : null;
        if (Text("state") is { } state)
            return state switch { "SUCCESS" => CiState.Passing, "PENDING" or "EXPECTED" => CiState.Pending, _ => CiState.Failing };
        return Text("conclusion") switch
        {
            "SUCCESS" or "NEUTRAL" or "SKIPPED" => CiState.Passing,
            null or "" => CiState.Pending,
            _ => CiState.Failing
        };
    }
}

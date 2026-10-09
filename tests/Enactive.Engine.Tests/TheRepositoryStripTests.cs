namespace Enactive.Engine.Tests;

using System.Diagnostics;
using System.Xml.Linq;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What the strip at the foot of the window says of the workspace's repository: its name, its branch, how far from its
/// upstream, what is changed and not committed - read with git as AutomaticGit runs it, so nothing of the folder's own
/// configuration is run - and the branch's pull request with its checks, as `gh` reports them.
/// </summary>
public sealed class TheRepositoryStripTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("enactive-repo-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);   // git's own files are read-only
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_root, name), text);

    [Fact]
    public async Task A_folder_outside_a_repository_has_no_strip()
        => Assert.Null(await RepoStatusReader.ReadAsync(_root, default));

    /// <summary>The branch, the lines changed since the last commit - staged or not - and the files git does not track.</summary>
    [Fact]
    public async Task A_repository_says_its_branch_and_what_is_not_committed()
    {
        Git("init", "-b", "work");
        Write("notes.txt", "one\ntwo\nthree\n");
        Git("add", "notes.txt");
        Git("commit", "-m", "first");
        Write("notes.txt", "one\nTWO\nthree\nfour\n");   // a line changed, a line added: 2 added, 1 removed
        Write("draft.txt", "new");

        var repo = await RepoStatusReader.ReadAsync(_root, default);

        Assert.NotNull(repo);
        Assert.Equal(Path.GetFileName(_root), repo.Name);
        Assert.Equal("work", repo.Branch);
        Assert.Equal((2, 1, 1), (repo.Added, repo.Removed, repo.Untracked));
        Assert.Null(repo.Ahead);   // no upstream
        Assert.Null(repo.GitHub);
    }

    /// <summary>A GitHub origin names the repository owner/repo - what a pull request is asked of.</summary>
    [Fact]
    public async Task A_github_origin_names_the_repository()
    {
        Git("init", "-b", "main");
        Git("remote", "add", "origin", "https://github.com/StasEdward/Enactive.git");

        var repo = await RepoStatusReader.ReadAsync(_root, default);

        Assert.Equal(("StasEdward/Enactive", "StasEdward/Enactive"), (repo!.Name, repo.GitHub));
    }

    [Theory]
    [InlineData("2\t1", 2, 1)]
    [InlineData("0\t0\n", 0, 0)]
    [InlineData("", null, null)]
    public void Ahead_and_behind_are_read(string output, int? ahead, int? behind)
        => Assert.Equal((ahead, behind), RepoStatusReader.ParseAheadBehind(output));

    [Fact]
    public void Changed_lines_are_summed_and_a_binary_file_counts_none()
        => Assert.Equal((7, 3), RepoStatusReader.ParseNumstat("5\t1\tsrc/a.cs\n2\t2\tsrc/b.cs\n-\t-\tlogo.png\n"));

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/owner/repo", "owner/repo")]
    [InlineData("https://gitlab.com/group/project.git", null)]
    public void A_github_origin_is_recognised(string origin, string? github)
        => Assert.Equal(github, RepoStatusReader.GitHubRepo(origin));

    [Theory]
    [InlineData("https://gitlab.com/group/project.git", "C:/work/x", "project")]
    [InlineData("", "C:/work/my-folder", "my-folder")]
    public void Another_host_or_none_names_it_by_what_there_is(string origin, string top, string name)
        => Assert.Equal(name, RepoStatusReader.RepoName(origin, top));

    // ── the pull request ────────────────────────────────────────────────────

    private static string Pull(string checks) =>
        $$"""{"number":52,"url":"https://github.com/StasEdward/Enactive/pull/52","additions":10625,"deletions":4540,"statusCheckRollup":[{{checks}}]}""";

    [Theory]
    [InlineData("""{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"COMPLETED","conclusion":"SKIPPED"}""", CiState.Passing)]
    [InlineData("""{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"COMPLETED","conclusion":"FAILURE"}""", CiState.Failing)]
    [InlineData("""{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"IN_PROGRESS","conclusion":""}""", CiState.Pending)]
    [InlineData("""{"state":"PENDING"},{"status":"COMPLETED","conclusion":"SUCCESS"}""", CiState.Pending)]
    [InlineData("""{"state":"ERROR"}""", CiState.Failing)]
    [InlineData("", CiState.None)]
    public void A_pull_request_is_read_with_its_checks(string checks, CiState ci)
    {
        var pull = RepoStatusReader.ParsePullRequest(Pull(checks));

        Assert.Equal((52, "https://github.com/StasEdward/Enactive/pull/52", 10625, 4540, ci),
            (pull!.Number, pull.Url, pull.Additions, pull.Deletions, pull.Ci));
    }

    [Fact]
    public void What_is_not_a_pull_request_is_none()
    {
        Assert.Null(RepoStatusReader.ParsePullRequest("no pull requests found for branch \"work\""));
        Assert.Null(RepoStatusReader.ParsePullRequest("{}"));
    }

    /// <summary>Without a GitHub origin or a branch there is nothing to ask, and nothing is started.</summary>
    [Fact]
    public async Task No_pull_request_is_asked_without_a_github_branch()
    {
        Assert.Null(await RepoStatusReader.ReadPullRequestAsync(new RepoStatus("x", "work", null, null, 0, 0, 0), default));
        Assert.Null(await RepoStatusReader.ReadPullRequestAsync(new RepoStatus("x", null, null, null, 0, 0, 0, "o/r"), default));
    }

    /// <summary>
    /// The strip is the window's foot: the last row of the grid it is in, a row that is there and sized to it. Placed in a
    /// row its grid did not have, it was laid in the grid's last row instead - over the current action panel, not under it.
    /// </summary>
    [Fact]
    public void The_strip_is_at_the_foot_of_its_grid()
    {
        var window = XDocument.Load(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var strip = window.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RepoBar");
        var grid = strip.Parent!;
        Assert.Equal("Grid", grid.Name.LocalName);
        var rows = grid.Elements().Where(e => e.Name.LocalName == "Grid.RowDefinitions").Elements().ToList();

        Assert.Equal(rows.Count - 1, int.Parse((string?)strip.Attribute("Grid.Row") ?? "0"));
        Assert.Equal("Auto", (string?)rows[^1].Attribute("Height"));
    }

    private static XElement Strip()
    {
        var window = XDocument.Load(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return window.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RepoBar");
    }

    /// <summary>
    /// One font and one size for every word on the strip, each centred: the branch in a monospace face sat on another
    /// baseline and the line looked uneven (2026-10-09).
    /// </summary>
    [Fact]
    public void Every_word_on_the_strip_is_one_font_one_size_on_one_line()
    {
        var texts = Strip().Descendants().Where(e => e.Name.LocalName == "TextBlock").ToArray();

        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Null(t.Attribute("FontFamily")));
        Assert.All(texts.Where(t => t.Attribute("FontSize") is not null), t => Assert.Equal("12", (string?)t.Attribute("FontSize")));
        Assert.All(texts.Where(t => t.Parent?.Name.LocalName == "StackPanel" && t.Parent.Parent?.Name.LocalName != "Button"),
            t => Assert.Equal("Center", (string?)t.Attribute("VerticalAlignment")));
    }

    /// <summary>What was added is green and what was removed red, as git and GitHub show them - the work's and the pull request's.</summary>
    [Fact]
    public void Added_is_green_and_removed_red()
    {
        var strip = Strip();
        string? ColourOf(string binding) => (string?)strip.Descendants()
            .Single(e => (string?)e.Attribute("Text") == "{Binding " + binding + "}").Attribute("Foreground");

        Assert.Equal("{DynamicResource Brand.Success}", ColourOf("Repo.Added"));
        Assert.Equal("{DynamicResource Brand.Danger}", ColourOf("Repo.Removed"));
        Assert.Equal("{DynamicResource Brand.Success}", ColourOf("Repo.PullRequestAdded"));
        Assert.Equal("{DynamicResource Brand.Danger}", ColourOf("Repo.PullRequestRemoved"));
    }

    /// <summary>
    /// Read again after each step of the run the window shows, and on the half-minute while a run is going too: a strip
    /// that waited for the run to end showed the state before it the whole time (2026-10-09).
    /// </summary>
    [Fact]
    public void The_strip_is_read_again_between_steps_and_while_a_run_goes()
    {
        var window = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "MainWindow.axaml.cs"));
        var render = window[window.IndexOf("private void RenderEvent(WorkEvent ev)", StringComparison.Ordinal)..];

        Assert.Matches(@"if \(ev\.Kind == EventKind\.StepCompleted\)\s+_ = RefreshRepoAsync\(\);", render[..2000]);
        Assert.Contains("_repoPoll.Tick += (_, _) => { if (IsActive) _ = RefreshRepoAsync(); };", window, StringComparison.Ordinal);
    }
}

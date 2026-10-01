namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Artifacts;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What a step changed, made by anything - a file tool, a shell command, a script.
///
/// <para>The reviewer used to see only what the artifact store journalled, and of that only the
/// first 8,000 characters. Measured 2026-09-24, run 5e5b51: two steps appended their findings to a
/// long report and passed content review on an excerpt that "covers only the pages 1-3 section".
/// And anything written by a command - <c>dotnet format</c>, <c>&gt; report.txt</c>, PowerShell's
/// <c>Add-Content</c> - was not in the store at all.</para>
/// </summary>
public sealed class WorkspaceChangesTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Names_only_comparison_includes_external_changes_without_patches(bool git)
    {
        if (git) Repository();
        else { Write("report.md", "before\n"); Write("gone.md", "delete me\n"); }
        using var changes = new WorkspaceChanges(_root);
        var before = await changes.TakeAsync(default);
        Assert.NotNull(before);
        Write("report.md", "after\n");
        Write("external.txt", "created outside the artifact store\n");
        File.Delete(Path.Combine(_root, "gone.md"));
        var after = await changes.TakeAsync(default);
        Assert.NotNull(after);
        var names = await changes.ComparePathsAsync(before, after, default);
        var full = await changes.CompareAsync(before, after, default);
        Assert.NotNull(names);
        Assert.NotNull(full);
        Assert.Equal(full.Select(c => (c.Path, c.Kind, c.OldPath)), names.Select(c => (c.Path, c.Kind, c.OldPath)));
        Assert.Contains(names, c => c.Path == "external.txt" && c.Kind == FileChangeKind.Added);
        Assert.Contains(names, c => c.Path == "gone.md" && c.Kind == FileChangeKind.Deleted);
        Assert.All(names, c => Assert.Null(c.Diff));
        if (git) Assert.Contains(full, c => c.Diff is not null);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-changes-" + Guid.NewGuid().ToString("N"));

    public WorkspaceChangesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* a temp folder */ }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var errors = p.StandardError.ReadToEnd();
        p.WaitForExit();

        // A setup command that failed must fail the test. Ignored, it leaves a fixture that is not
        // what the test says it is, and an empty answer from a failed read looks exactly like a
        // clean index.
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {p.ExitCode}: {errors}");
        return output;
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void Repository()
    {
        Git("init", "-q");
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "t");
        Git("config", "core.autocrlf", "false");
        Write("report.md", "# Report\n\n## Page 1\nfine\n");
        Write("gone.md", "to be deleted\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "start");
    }

    /// <summary>
    /// A repository whose .gitignore lists the engine's own folder - which the engine writes into
    /// every folder it opens. `git add -A -- . ':(exclude).enactive'` exits 1 there, and every
    /// snapshot was null: review diffs, the closing line and handover facts all fell back in silence.
    /// </summary>
    [Fact]
    public async Task A_repository_that_ignores_the_engines_folder_is_still_measured()
    {
        Repository();
        Write(".gitignore", ".enactive/\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "ignore the engine's folder");
        Write(".enactive/runs.db", "engine state");
        using var changes = new WorkspaceChanges(_root);

        var before = await changes.TakeAsync(default);
        Assert.NotNull(before);

        File.AppendAllText(Path.Combine(_root, "report.md"), "appended\n");
        Write(".enactive/runs.db", "engine state, changed");
        var after = await changes.TakeAsync(default);

        var change = Assert.Single((await changes.CompareAsync(before!, after!, default))!);
        Assert.Equal("report.md", change.Path);
        Assert.Contains("+appended", change.Diff, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. A repository that does NOT ignore it still leaves it out.</summary>
    [Fact]
    public async Task The_engines_folder_is_left_out_even_when_git_would_take_it()
    {
        Repository();
        using var changes = new WorkspaceChanges(_root);

        var before = await changes.TakeAsync(default);
        Write(".enactive/runs.db", "engine state");
        var after = await changes.TakeAsync(default);

        Assert.Empty((await changes.CompareAsync(before!, after!, default))!);
    }

    /// <summary>THE ONE THAT MATTERS: a change no file tool made is seen, as a diff.</summary>
    [Fact]
    public async Task A_change_made_by_a_command_is_seen_as_a_diff()
    {
        Repository();
        using var changes = new WorkspaceChanges(_root);

        var before = await changes.TakeAsync(default);
        Assert.NotNull(before);

        // What a shell command does: writes straight to disk, past every tool.
        File.AppendAllText(Path.Combine(_root, "report.md"), "\n## Page 2\nappended by a command\n");
        Write("new.txt", "created\n");
        File.Delete(Path.Combine(_root, "gone.md"));
        Write(".enactive/scratch/helper.ps1", "not the work\n");

        var after = await changes.TakeAsync(default);
        var found = await changes.CompareAsync(before!, after!, default);

        Assert.NotNull(found);
        var report = Assert.Single(found!, c => c.Path == "report.md");
        Assert.Equal(FileChangeKind.Modified, report.Kind);
        Assert.Contains("+appended by a command", report.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("+# Report", report.Diff, StringComparison.Ordinal);   // unchanged lines are not additions

        Assert.Contains(found!, c => c.Path == "new.txt" && c.Kind == FileChangeKind.Added);
        Assert.Contains(found!, c => c.Path == "gone.md" && c.Kind == FileChangeKind.Deleted);
        Assert.DoesNotContain(found!, c => c.Path.StartsWith(".enactive", StringComparison.Ordinal));
    }

    /// <summary>
    /// The person's own staging area is theirs: a snapshot must not stage or unstage anything.
    ///
    /// <para>Started from a staging area that HAS something in it - a change staged, and the same
    /// file changed again after, so staged and working versions differ. It used to start from an
    /// empty one, where a snapshot that reset the index to HEAD had nothing to reset, and it did not
    /// check that either snapshot was taken, so two failed ones passed
    ///.</para>
    /// </summary>
    [Fact]
    public async Task The_persons_own_index_is_untouched()
    {
        Repository();
        File.AppendAllText(Path.Combine(_root, "report.md"), "## Staged\nthe person staged this\n");
        Git("add", "report.md");
        File.AppendAllText(Path.Combine(_root, "report.md"), "## Not staged\nand then kept writing\n");
        Write("wip.txt", "the person's own unstaged work\n");

        var staged = Git("diff", "--cached", "--binary");
        var index = Git("ls-files", "--stage");
        var head = Git("rev-parse", "HEAD");
        var status = Git("status", "--porcelain");
        var working = File.ReadAllText(Path.Combine(_root, "report.md"));
        Assert.Contains("the person staged this", staged, StringComparison.Ordinal);

        using var changes = new WorkspaceChanges(_root);
        var first = await changes.TakeAsync(default);
        var second = await changes.TakeAsync(default);
        Assert.NotNull(first?.Tree);
        Assert.NotNull(second?.Tree);

        Assert.Equal(staged, Git("diff", "--cached", "--binary"));
        Assert.Equal(index, Git("ls-files", "--stage"));
        Assert.Equal(head, Git("rev-parse", "HEAD"));
        Assert.Equal(status, Git("status", "--porcelain"));
        Assert.Equal(working, File.ReadAllText(Path.Combine(_root, "report.md")));
    }

    [Fact]
    public async Task Nothing_changed_is_nothing()
    {
        Repository();
        using var changes = new WorkspaceChanges(_root);

        var before = await changes.TakeAsync(default);
        var after = await changes.TakeAsync(default);

        Assert.Empty((await changes.CompareAsync(before!, after!, default))!);
    }

    /// <summary>Outside git there is no "before" to diff against, but WHICH files changed is still known.</summary>
    [Fact]
    public async Task Outside_git_the_changed_files_are_named()
    {
        Write("a.txt", "one\n");
        Write("b.txt", "two\n");
        using var changes = new WorkspaceChanges(_root);

        var before = await changes.TakeAsync(default);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one, and a much longer second version\n");
        Write("c.txt", "three\n");
        File.Delete(Path.Combine(_root, "b.txt"));
        var after = await changes.TakeAsync(default);

        var found = (await changes.CompareAsync(before!, after!, default))!;

        Assert.Contains(found, c => c.Path == "a.txt" && c.Kind == FileChangeKind.Modified && c.Diff is null);
        Assert.Contains(found, c => c.Path == "c.txt" && c.Kind == FileChangeKind.Added);
        Assert.Contains(found, c => c.Path == "b.txt" && c.Kind == FileChangeKind.Deleted);
    }

    /// <summary>WorkspaceChanges.MaxDiffCharsKept: a huge diff is kept to a limit, and says where it was cut.</summary>
    [Fact]
    public async Task A_huge_diff_is_kept_to_a_limit_and_says_so()
    {
        Repository();
        using var changes = new WorkspaceChanges(_root);

        var before = await changes.TakeAsync(default);
        Write("huge.txt", string.Join("\n", Enumerable.Repeat(new string('z', 99), 3_000)));
        var after = await changes.TakeAsync(default);

        var huge = Assert.Single((await changes.CompareAsync(before!, after!, default))!);
        Assert.EndsWith("(diff cut here)", huge.Diff, StringComparison.Ordinal);
    }
}

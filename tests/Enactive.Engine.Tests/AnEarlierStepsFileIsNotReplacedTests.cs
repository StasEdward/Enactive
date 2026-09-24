namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A whole-file write that would drop most of what a file holds is refused even when the file does
/// not shrink; and git saying a path is not in a revision is an answer, not a failure.
///
/// <para><b>Measured 2026-09-24 21:06, run a19a2c.</b> Step 2 was told to append pages 4-6 to the
/// report step 1 had written. It replaced the report with pages 4-6 alone: 10,938 bytes over 9,696,
/// so the shrink check saw a file that grew. It noticed, tried <c>git show HEAD:</c> on a file that
/// was never committed, retyped step 1's findings from its context - and the step was marked
/// Incomplete for the git call, skipping the two steps after it.</para>
/// </summary>
public sealed class AnEarlierStepsFileIsNotReplacedTests
{
    private static string Section(string page, int lines)
        => $"## {page}\n" + string.Join("\n", Enumerable.Range(1, lines).Select(i => $"- claim {i} of {page}: checked against the code, matches."));

    private static readonly string PagesOneToThree =
        "# DRIFT report - pages 1-3\n\n" + Section("Overview.md", 20) + "\n\n" + Section("Architecture.md", 20) + "\n\n" + Section("Getting-Started.md", 20) + "\n";

    private static readonly string PagesFourToSix =
        "# DRIFT report - pages 4-6\n\n" + Section("Running-Tasks.md", 22) + "\n\n" + Section("Console.md", 22) + "\n\n" + Section("Templates.md", 22) + "\n";

    private static string Write(string path, string content, bool? append = null, bool? allowShrink = null)
    {
        var args = new Dictionary<string, object> { ["path"] = path, ["content"] = content };
        if (append is { } a) args["append"] = a;
        if (allowShrink is { } s) args["allow_shrink"] = s;
        return System.Text.Json.JsonSerializer.Serialize(args);
    }

    // ── write_file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Replacing_a_report_with_different_pages_is_refused_even_when_it_grows()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", PagesOneToThree);
        Assert.True(PagesFourToSix.Length > PagesOneToThree.Length);

        var result = await fx.Invoke(new WriteFileTool(), Write("report.md", PagesFourToSix));

        Assert.False(result.Success);
        Assert.Contains("keeps 0 of the", result.Error, StringComparison.Ordinal);
        Assert.Contains("\"append\": true", result.Error, StringComparison.Ordinal);
        Assert.Equal(PagesOneToThree, fx.Read("report.md"));
    }

    [Fact]
    public async Task Appending_the_new_pages_keeps_the_old_ones()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", PagesOneToThree);

        var result = await fx.Invoke(new WriteFileTool(), Write("report.md", PagesFourToSix, append: true));

        Assert.True(result.Success, result.Error);
        Assert.Contains("Overview.md", fx.Read("report.md"), StringComparison.Ordinal);
        Assert.Contains("Templates.md", fx.Read("report.md"), StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. A rewrite that keeps most of the file - a few lines changed - is a rewrite, not a loss.</summary>
    [Fact]
    public async Task A_rewrite_that_keeps_most_lines_goes_through()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", PagesOneToThree);
        var revised = PagesOneToThree.Replace("claim 3 of Overview.md: checked against the code, matches.",
                                              "claim 3 of Overview.md: DOES NOT MATCH - see Program.cs line 12.");

        var result = await fx.Invoke(new WriteFileTool(), Write("report.md", revised));

        Assert.True(result.Success, result.Error);
        Assert.Equal(revised, fx.Read("report.md"));
    }

    [Fact]
    public async Task A_replacement_that_is_meant_goes_through_when_it_says_so()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", PagesOneToThree);

        var result = await fx.Invoke(new WriteFileTool(), Write("report.md", PagesFourToSix, allowShrink: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal(PagesFourToSix, fx.Read("report.md"));
    }

    /// <summary>THE BOUNDARY. A short file is not what this protects: rewriting it whole is not the risky act.</summary>
    [Fact]
    public async Task A_short_file_is_replaced_without_question()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "one\ntwo\nthree\n");

        var result = await fx.Invoke(new WriteFileTool(), Write("notes.md", "four\nfive\nsix\n"));

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task A_replacement_does_not_invite_the_model_to_restore_it_itself()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", PagesOneToThree);

        var result = await fx.Invoke(new WriteFileTool(), Write("report.md", PagesFourToSix, allowShrink: true));

        Assert.Contains("there is no tool for it", result.Output, StringComparison.Ordinal);
    }

    // ── git ──────────────────────────────────────────────────────────────────

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static void Repository(EngineFixture fx)
    {
        Git(fx.Root, "init", "-q");
        Git(fx.Root, "config", "user.email", "t@example.com");
        Git(fx.Root, "config", "user.name", "t");
        fx.Write("committed.md", "in git");
        Git(fx.Root, "add", "-A");
        Git(fx.Root, "commit", "-q", "-m", "start");
    }

    [Fact]
    public async Task A_path_not_in_the_revision_is_an_answer()
    {
        using var fx = new EngineFixture();
        Repository(fx);
        fx.Write("untracked.md", "never committed");

        var result = await fx.Invoke(new GitTool(), """{"args":["show","HEAD:untracked.md"]}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
        Assert.Contains("not in that revision", result.Error, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. Git failing for any other reason is still a failure.</summary>
    [Fact]
    public void Other_git_errors_are_not_answers()
    {
        Assert.False(Enactive.Core.Context.ShellOutcome.GitFoundNothing("fatal: not a git repository (or any of the parent directories): .git"));
        Assert.False(Enactive.Core.Context.ShellOutcome.GitFoundNothing("fatal: invalid object name 'HAED'."));
        Assert.True(Enactive.Core.Context.ShellOutcome.GitFoundNothing("fatal: path 'x.md' does not exist in 'HEAD'"));
    }

    [Fact]
    public async Task A_step_that_asked_git_and_moved_on_is_not_left_incomplete()
    {
        using var fx = new EngineFixture();
        Repository(fx);
        fx.Write("untracked.md", "never committed");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look for an older copy"}"""),
            Turn.Calls1("git", """{"args":["show","HEAD:untracked.md"]}""", "g1"),
            // As in the run: the lookup answered no, and the step went on and did its work another way.
            Turn.Calls1("write_file", """{"path":"notes.md","content":"No committed copy; the file on disk is the only one."}""", "w1"),
            Turn.Says("There is no committed copy of untracked.md; noted in notes.md."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "look for an older copy");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }
}

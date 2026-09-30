namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Xunit;

/// <summary>
/// The reviewer is shown what the step CHANGED - by any tool - not the first 8,000 characters of
/// whatever the file tools happened to write.
///
/// <para><b>Measured 2026-09-24, run 5e5b51.</b> Two steps appended their findings to a long report
/// and passed content review on an excerpt that could not contain them: "The excerpt shows only the
/// pages 1-3 section; the pages 4-6 findings the agent says it appended fall in the part not shown" -
/// PASS. And a change made by a command - <c>dotnet format</c>, <c>&gt; report.txt</c>, PowerShell's
/// <c>Add-Content</c> - never reached the reviewer at all: only the file tools go through the store.
/// That is most of the writing in a refactoring, a disk check or a log analysis.</para>
/// </summary>
public sealed class TheReviewSeesWhatTheStepChangedTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"do it"}""";

    // The short review answers in its own form; a pass cites a call it was shown.
    private static FakeChatProvider Reviewer(bool shortReview) => new()
    {
        WhenExhausted = shortReview ? Turn.Says("""{"verdict":"pass","reason":"done","calls":[1],"files":[]}""") : Verdicts.Pass()
    };

    private static string PromptOf(FakeChatProvider reviewer)
        => string.Join("\n", reviewer.Requests.SelectMany(r => r.Messages).Select(m => m.Content ?? ""));

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static void Repository(EngineFixture fx, string report)
    {
        Git(fx.Root, "init", "-q");
        Git(fx.Root, "config", "user.email", "t@example.com");
        Git(fx.Root, "config", "user.name", "t");
        Git(fx.Root, "config", "core.autocrlf", "false");
        fx.Write("report.md", report);
        Git(fx.Root, "add", "-A");
        Git(fx.Root, "commit", "-q", "-m", "start");
    }

    /// <summary>
    /// THE ONE THAT MATTERS for everything that is not a wiki: a file written by a COMMAND, in a step
    /// that ran commands, reaches the review as a diff.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_change_made_by_a_command_reaches_the_review(bool shortReview)
    {
        using var fx = new EngineFixture { ShortReview = shortReview };
        Repository(fx, "# Report\n\n## Page 1\nfine\n");

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo appended-by-a-command>>report.md"}""", "c1"),
            Turn.Says("Appended the finding."));
        var reviewer = Reviewer(shortReview);

        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "append it");

        var prompt = PromptOf(reviewer);
        Assert.Contains(shortReview ? "--- report.md - CHANGED while this step ran" : "What CHANGED in the workspace while this step ran",
            prompt, StringComparison.Ordinal);
        Assert.Contains("+appended-by-a-command", prompt, StringComparison.Ordinal);
        if (shortReview)                                                   // a short file: what changed, and the file it is in
            Assert.Contains("--- as it is now, whole:", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The measured case: a step appends to a LONG report through the file tools and runs no command.
    /// The review is shown the appended section, as a diff, not the report's opening.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_append_to_a_long_report_is_reviewed_by_what_was_appended(bool shortReview)
    {
        using var fx = new EngineFixture { ShortReview = shortReview };
        var body = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i}: " + new string('x', 80)));
        Repository(fx, "# Report\n\n" + body + "\n");

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"## Pages 4-6\nFINDING-FROM-THIS-STEP\n","append":true}""", "w1"),
            Turn.Says("Appended pages 4-6."));
        var reviewer = Reviewer(shortReview);

        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "append it");

        var prompt = PromptOf(reviewer);
        Assert.Contains("CHANGED by this step - a unified diff", prompt, StringComparison.Ordinal);
        Assert.Contains("+FINDING-FROM-THIS-STEP", prompt, StringComparison.Ordinal);

        // Not the opening of the report: the first lines are not this step's work and are not shown.
        Assert.DoesNotContain("line 1: ", prompt, StringComparison.Ordinal);
    }

    /// <summary>Outside git there is no "before", but a file a command created is still shown whole.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outside_git_a_file_a_command_created_is_shown(bool shortReview)
    {
        using var fx = new EngineFixture { ShortReview = shortReview };

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo disk-report-line>disks.txt"}""", "c1"),
            Turn.Says("Saved the disk report."));
        var reviewer = Reviewer(shortReview);

        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "check the disks");

        var prompt = PromptOf(reviewer);
        Assert.Contains("NEW FILE, created while this step ran, by no file tool of it (a command it ran can have done it", prompt, StringComparison.Ordinal);
        Assert.Contains("disk-report-line", prompt, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. A step that changed nothing is not told it changed something.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_step_that_changed_nothing_has_no_changes_section(bool shortReview)
    {
        using var fx = new EngineFixture { ShortReview = shortReview };
        Repository(fx, "# Report\n");

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo just looking"}""", "c1"),
            Turn.Says("Looked."));
        var reviewer = Reviewer(shortReview);

        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "look");

        Assert.DoesNotContain("What CHANGED in the workspace", PromptOf(reviewer), StringComparison.Ordinal);
        Assert.DoesNotContain("--- report.md", PromptOf(reviewer), StringComparison.Ordinal);
    }
}

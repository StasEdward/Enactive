namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Git refusing its own arguments is a call that never happened — the same sentence the shells have
/// been saying since 2026-09-20, finally said by the tool that could not say it.
///
/// <para><b>Why git was left out and what that cost.</b> Every existing refusal test reads a SHELL
/// declining to start a line. The git tool starts git directly, with an argument list and no shell,
/// so none of that vocabulary applies: git starts, prints its refusal, exits 1, and from outside is
/// indistinguishable from a merge conflict or a rejected push. Three runs on 2026-09-22 died of
/// exactly that — <c>["st","--porcelain"]</c> corrected to <c>status</c> a second later;
/// <c>{"args": show HEAD:file}</c> whose quotes never parsed; and
/// <c>{"args": "[\"status\", \"--short\"]"}</c>, a JSON array encoded as a string, which git read as
/// a subcommand named <c>["status",</c> (run 068668, 22:45, TaskFailed). In each case git did
/// nothing, a step was marked Incomplete for it, and the rest of the plan was skipped.</para>
///
/// <para>These call the real <c>git</c>. That is the point: the wording being matched is git's own,
/// and a test that typed the message out by hand would prove only that this file agrees with
/// itself.</para>
/// </summary>
public sealed class RefusedByGitTests
{
    /// <summary>
    /// A real repository in the fixture's root. Needed because git checks for one BEFORE it parses
    /// options: outside a repository "status --porcelainn" is answered with "not a git repository"
    /// and the option is never looked at, so a test run there would be measuring the wrong refusal.
    /// </summary>
    private static void GitInit(string root)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init -q")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10_000);
    }

    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    /// <summary>
    /// The first of the three, verbatim. A subcommand git does not have is a word, not a failure,
    /// and the model that typed it corrected itself one second later — which the step could not
    /// accept, because it was holding a failure that never occurred.
    /// </summary>
    [Fact]
    public async Task A_subcommand_git_does_not_have_never_ran()
    {
        using var fx = new EngineFixture();

        var result = await new GitTool().InvokeAsync(
            """{"args":["st","--porcelain"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
        Assert.Contains("Nothing was run", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one that cost run 068668: a JSON array handed in as a STRING. Git is given one argument
    /// spelled <c>["status",</c> and says so; it is still nothing having happened, and the fix the
    /// refusal names is the encoding, because that is the mistake.
    /// </summary>
    [Fact]
    public async Task A_json_array_sent_as_a_string_never_ran()
    {
        using var fx = new EngineFixture();

        var result = await new GitTool().InvokeAsync(
            """{"args":"[\"status\", \"--short\"]"}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
        Assert.Contains("each argument as its own element", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An option git does not have, which arrives in a different sentence — <i>unknown option</i>
    /// or <i>unknown switch</i>, quoted with a backtick and closed with an apostrophe. Read, or the
    /// half of git's refusals that are not about subcommands go on costing runs.
    /// </summary>
    [Theory]
    [InlineData("--porcelainn")]   // error: unknown option `porcelainn'
    [InlineData("--oopsie")]       // fatal: unrecognized argument: --oopsie
    public async Task An_option_git_does_not_have_never_ran(string option)
    {
        using var fx = new EngineFixture();
        GitInit(fx.Root);

        var result = await new GitTool().InvokeAsync(
            $$"""{"args":["status","{{option}}"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
    }

    /// <summary>
    /// THE BOUNDARY, and the reason this is narrow. A git command that RAN and failed is still a
    /// failure: the step keeps holding it, and nothing here forgives it. Widening the match until
    /// this test goes green would quietly tell steps that nothing happened while git was changing
    /// the repository.
    /// </summary>
    [Fact]
    public async Task Git_that_ran_and_failed_is_still_a_failure()
    {
        using var fx = new EngineFixture();
        GitInit(fx.Root);   // or the failure would be "not a git repository", which proves nothing

        var result = await new GitTool().InvokeAsync(
            """{"args":["show","refs/heads/no-such-branch-here"]}""",
            Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.DidNotRun, result.Error);
    }

    /// <summary>
    /// And a git that SUCCEEDED is never read for refusals at all — the check is behind the exit
    /// code, so a <c>git log</c> whose output quotes somebody's commit message about a command not
    /// existing is a git that worked.
    /// </summary>
    [Fact]
    public async Task Output_that_merely_talks_about_a_refusal_is_not_one()
    {
        Assert.False(ShellOutcome.GitRefusedIt(
            new[] { "log", "-1" },
            "commit abc123\n\n    Say so when 'foo' is not a git command\n"),
            "a commit message is not git refusing anything - but the word WE sent must match");

        // The same sentence about a word we did send IS a refusal.
        Assert.True(ShellOutcome.GitRefusedIt(
            new[] { "foo" },
            "git: 'foo' is not a git command. See 'git --help'."));
    }
}

namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Reported by Stas, 2026-09-07, 17:33: a Code Review run made <b>eighteen</b> git calls in ten
/// different formulations, never saw a diff, and wrote "No issues were found" to review.md.
///
/// <para>Every one of those calls had the same defect. The model sent
/// <c>{"args":["diff HEAD"]}</c> — a whole command line in one array element — and each element is
/// passed to the process verbatim, so git was asked for a subcommand called "diff HEAD" and said
/// so. Only <c>["status"]</c> and <c>["ls-files"]</c> worked, because they are one word.</para>
///
/// <para><b>And the model was never told.</b> The failure reached it as
/// <c>ERROR: git exited with code 1.</c> and nothing else, while the output holding
/// <c>git: 'diff HEAD' is not a git command</c> sat in the journal, in the log and on screen —
/// visible to everyone except the only party that could act on it. So it did not correct the shape;
/// it guessed at the CONTENT, ten times, degrading as it went (the tenth call contained a stray
/// Hebrew letter), and then wrote a conclusion about a diff it had never read.</para>
///
/// <para>The same class as everything else this month: the record somebody works from is shortened,
/// and nothing says so.</para>
///
/// <para>One thing worked exactly as designed and is worth keeping visible — the open-failure guard
/// noticed the model was finishing over ten unresolved calls and made the run <b>Incomplete</b>
/// rather than Completed. That is the only reason an invented review did not come back green.</para>
/// </summary>
public sealed class FailedToolFeedbackTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts, Services: null!);

    // ── what a failure tells the model ──────────────────────────────────────

    /// <summary>
    /// The reported shape, end to end: a process whose exit code is useless on its own and whose
    /// stderr is the entire diagnosis. Before this the model saw the first sentence and none of the
    /// second.
    ///
    /// <para>It has to be a PROCESS. A first attempt at this test used read_file on a missing path
    /// and passed with the fix reverted — read_file puts everything it has to say in the error, so
    /// nothing was ever lost there. Only a tool whose diagnosis lives in the OUTPUT can tell the two
    /// behaviours apart, and a test in this file that cannot is worse than no test.</para>
    /// </summary>
    [Fact]
    public async Task A_command_that_failed_reaches_the_model_with_its_stderr()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Run something","steps":[]}"""),
            Turn.Calls1("run_command", """{"command":"cmd /c echo enactive-marker-text 1>&2 & exit 1"}"""),
            Turn.Says("It failed."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        var toolMessages = provider.Requests.Last().Messages
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .ToArray();

        Assert.Contains(toolMessages, m => m.Contains("enactive-marker-text", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failure with nothing to add says only the error - a bare "ERROR: x" must not become
    /// "ERROR: x\n" with an empty line after it, which is the sort of thing that accumulates.
    /// </summary>
    [Fact]
    public async Task A_failure_with_no_output_stays_one_line()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Bad arguments","steps":[]}"""),
            Turn.Calls1("read_file", """{}"""),
            Turn.Says("I need a path."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "read");

        var refusal = provider.Requests.Last().Messages
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .Single(m => m.Contains("'path' is required", StringComparison.Ordinal));

        Assert.Equal(refusal.TrimEnd(), refusal);
    }

    /// <summary>A successful call is unchanged: its output, and no ERROR prefix.</summary>
    [Fact]
    public async Task A_successful_call_is_reported_exactly_as_before()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "the contents");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Read","steps":[]}"""),
            Turn.Calls1("read_file", """{"path":"notes.md"}"""),
            Turn.Says("Read it."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "read notes.md");

        var message = provider.Requests.Last().Messages
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .Single();

        Assert.Contains("the contents", message);
        Assert.DoesNotContain("ERROR", message, StringComparison.Ordinal);
    }

    // ── the argument mistake, named ─────────────────────────────────────────

    /// <summary>
    /// The exact call from the log. It is refused with the shape it should have had, rather than run
    /// and returned as "exit code 1".
    /// </summary>
    [Fact]
    public async Task A_whole_command_line_in_one_argument_is_refused_with_the_fix()
    {
        using var fx = new EngineFixture();

        var result = await new GitTool().InvokeAsync(
            """{"args":["diff HEAD"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("\"diff\", \"HEAD\"", result.Error);
        Assert.Contains("ONE argument", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And an argument that legitimately contains spaces is untouched. Checking only the FIRST
    /// element is what makes this exact: no subcommand contains whitespace, a commit message does.
    /// </summary>
    [Theory]
    // args, refused?
    [InlineData(new[] { "diff HEAD" }, true)]
    [InlineData(new[] { "log -1" }, true)]
    [InlineData(new[] { "diff", "HEAD" }, false)]
    [InlineData(new[] { "status" }, false)]
    [InlineData(new[] { "commit", "-m", "a message with spaces" }, false)]
    [InlineData(new[] { "show", "HEAD:some file.txt" }, false)]
    public void The_rule_itself(string[] args, bool refused)
        => Assert.Equal(refused, ProcessExec.WrongShapeOfArgs("git", args) is not null);

    /// <summary>A plain string is still split, so the simple form keeps working.</summary>
    [Fact]
    public async Task A_plain_string_is_still_split_into_arguments()
    {
        using var fx = new EngineFixture();

        var result = await new GitTool().InvokeAsync(
            """{"args":"status --porcelain"}""", Context(fx), CancellationToken.None);

        // It ran git rather than refusing the shape - whether this folder is a repository is not
        // what is being tested, so only the refusal is ruled out.
        Assert.DoesNotContain("is not a git subcommand", result.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>docker attracts the same mistake and gets the same answer.</summary>
    [Fact]
    public async Task Docker_gets_the_same_treatment()
    {
        using var fx = new EngineFixture();

        var result = await new DockerTool().InvokeAsync(
            """{"args":["ps -a"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("\"ps\", \"-a\"", result.Error);
        Assert.Contains("docker subcommand", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools' own descriptions have to carry the rule, because the description is what the model
    /// reads before it makes the mistake - the refusal only helps afterwards.
    /// </summary>
    [Theory]
    [InlineData("git")]
    [InlineData("docker")]
    public void The_description_shows_the_wrong_shape_as_well_as_the_right_one(string tool)
    {
        var definition = EngineFixture.ShippedTools()
            .Single(t => t.Definition.Name == tool).Definition;

        Assert.Contains("ONE ARGUMENT PER ELEMENT", definition.Description, StringComparison.Ordinal);
        Assert.Contains("NOT", definition.Description, StringComparison.Ordinal);
    }
}

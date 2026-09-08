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
    ///
    /// <para>The message names the PIECES and how many there should be. It used to show the array
    /// literal — «pass ["diff", "HEAD"] instead» — and on 2026-09-08 17:25 a model answered that by
    /// sending <c>{"args":["diff\",\"HEAD"]}</c>: the syntax copied into the string it was meant to
    /// replace. A fix that can be pasted wrongly will be.</para>
    /// </summary>
    [Fact]
    public async Task A_whole_command_line_in_one_argument_is_refused_with_the_fix()
    {
        using var fx = new EngineFixture();

        var result = await new GitTool().InvokeAsync(
            """{"args":["diff HEAD"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("2 elements", result.Error, StringComparison.Ordinal);
        Assert.Contains("diff", result.Error, StringComparison.Ordinal);
        Assert.Contains("HEAD", result.Error, StringComparison.Ordinal);
        Assert.Contains("ONE argument", result.Error, StringComparison.OrdinalIgnoreCase);
        // The literal the model copied is gone, not merely accompanied by better advice.
        Assert.DoesNotContain("\"diff\", \"HEAD\"", result.Error);
    }

    /// <summary>
    /// And the shape it copied is refused too. <c>diff","HEAD</c> is one argument containing the
    /// JSON that should have been around it; no real argument holds a quote-comma-quote. Without
    /// this the call reaches git, which answers "exit code 1" — the unactionable message this whole
    /// check exists to replace.
    /// </summary>
    [Fact]
    public async Task The_array_syntax_written_inside_one_argument_is_refused_too()
    {
        using var fx = new EngineFixture();

        var result = await new GitTool().InvokeAsync(
            """{"args":["diff\",\"HEAD"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun);
        Assert.Contains("2 elements", result.Error, StringComparison.Ordinal);
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

    /// <summary>
    /// A call the tool could not READ leaves nothing unfinished, so the same tool working afterwards
    /// closes it — and the run is not failed for a sentence that did not parse.
    ///
    /// <para>The reported run, 2026-09-08 17:25. A quick action ran <c>git status</c> and
    /// <c>git diff</c>, both fine; asked for <c>git ["diff HEAD"]</c>, which was refused before git
    /// ran; asked again in the mangled shape, refused too; ran <c>git diff</c> and <c>git status</c>
    /// again; wrote review.md and reported truthfully what it had found. The run was Incomplete:
    /// <i>"Finished without resolving 2 tool call(s) that did not go through."</i></para>
    ///
    /// <para>The engine already believed the principle — a <c>write_file</c> refused for a missing
    /// path is closed by the next <c>write_file</c> that works, because it "is not work that did not
    /// happen, it is a sentence that did not parse". The rule was written to apply only to tools
    /// that write files, which was never what made it true.</para>
    /// </summary>
    [Fact]
    public async Task A_call_the_tool_could_not_read_is_closed_by_the_same_tool_working()
    {
        using var fx = new EngineFixture();
        fx.Write("review.md", "old");

        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"review","steps":[]}"""),
            // The fixture workspace is a bare temp folder, so the run makes it a repository first -
            // in band, through the same tool, rather than by reaching around the engine.
            Turn.Calls1("git", """{"args":["init"]}""", "g0"),
            Turn.Calls1("git", """{"args":["diff HEAD"]}""", "g1"),
            Turn.Calls1("git", """{"args":["status"]}""", "g2"),
            Turn.Calls1("write_file", """{"path":"review.md","content":"no changes"}""", "w1"))
        {
            WhenExhausted = Turn.Says("git status showed no changes; I wrote review.md.")
        };

        var events = await fx.RunAsync(
            fx.Build(worker,
                     worker: EngineFixture.WorkerWith("git", "write_file"),
                     policy: new PermissionPolicy(PermissionLevel.Autonomous, ["*"], [])),
            "review the changes");

        // The reason is carried into the failure message on purpose: this test was written twice
        // against the wrong cause — once with a worker whose role did not offer git at all, once in
        // a workspace that was not a repository — and both times it failed for a reason that had
        // nothing to do with the rule under test.
        Assert.True(events.Last().Outcome() == RunOutcomeKind.Completed,
                    string.Join(" | ", events.TakeLast(3).Select(e => e.Kind + ": " + e.Summary)));
        Assert.DoesNotContain(events, e => e.Summary.Contains("did not go through", StringComparison.Ordinal));
    }

    /// <summary>docker attracts the same mistake and gets the same answer.</summary>
    [Fact]
    public async Task Docker_gets_the_same_treatment()
    {
        using var fx = new EngineFixture();

        var result = await new DockerTool().InvokeAsync(
            """{"args":["ps -a"]}""", Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("2 elements", result.Error, StringComparison.Ordinal);
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

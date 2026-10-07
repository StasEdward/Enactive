namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Providers;
using Enactive.Core.Workers;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// 2026-09-07: "I give it a task to change a file and the system breaks the file. Tried twice."
///
/// <para>The request was to add one entry to the top menu of a 414-line page. Both runs did the
/// same thing: read lines 1-400 of 414, then call <c>write_file</c> with the whole document
/// regenerated from context - 168 lines the first time, 165 the second. 21164 bytes became 7982,
/// and both runs reported success.</para>
///
/// <para>The model was not misbehaving. No worker had ever been given <c>edit_file</c>, so the only
/// way it had to change one line of a document was to retype the document - and retyping a document
/// from context is summarising it. The tool that solves this had been written the day before, after
/// the same failure in a different file, and then handed to nobody.</para>
///
/// <para>Two fixes, so two halves of this file: the tool is reachable, and a rewrite that throws
/// most of a file away is refused rather than reported as a success.</para>
/// </summary>
public sealed class SilentTruncationTests
{
    private static ToolContext Context(EngineFixture fx, IArtifactStore? store = null)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: store ?? fx.Artifacts);

    private static IReadOnlyList<Worker> Roles()
        => DefaultWorkers.Seed(new ModelRef("fake", "fake-model"));

    /// <summary>A page of roughly the shape that was lost: long enough that retyping it is the risk.</summary>
    private static string Page(int lines)
        => string.Join('\n', Enumerable.Range(0, lines)
            .Select(i => $"  <li class=\"nav-item\" data-index=\"{i}\">Menu entry number {i} — пункт меню</li>"));

    // ── half one: the tool has to be reachable ──────────────────────────────

    /// <summary>
    /// The whole failure in one assertion. edit_file was registered by the host and named by no
    /// role, so the model was never offered it and write_file was the only door.
    /// </summary>
    [Theory]
    [InlineData("developer")]
    [InlineData("writer")]
    public void A_role_that_may_write_a_file_may_also_edit_one(string roleId)
    {
        var worker = Roles().Single(w => w.Id == roleId);

        Assert.Contains("write_file", worker.ToolAllowlist);
        Assert.Contains("edit_file", worker.ToolAllowlist);
    }

    [Fact]
    public void A_role_that_may_not_write_does_not_gain_editing_either()
    {
        // The reviewer is Observe: read and list, nothing else. Handing out edit_file must not have
        // widened anything - it is a narrower capability than write_file, never a new one.
        var reviewer = Roles().Single(w => w.Id == "reviewer");

        Assert.DoesNotContain("write_file", reviewer.ToolAllowlist);
        Assert.DoesNotContain("edit_file", reviewer.ToolAllowlist);
    }

    // ── half two: a rewrite that loses the file is refused ──────────────────

    /// <summary>
    /// The reported case, to scale: 21164 bytes replaced by 7982. It came back as
    /// "REPLACED the existing file" with a byte count, which is a true sentence about a destroyed
    /// document, and the run went green.
    /// </summary>
    [Fact]
    public async Task Rewriting_a_long_file_down_to_a_third_of_itself_is_refused()
    {
        using var fx = new EngineFixture();
        var original = Page(414);
        fx.Write("index.html", original);

        var result = await new WriteFileTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                content = Page(168)
            }),
            Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("edit_file", result.Error);

        // And the point of refusing rather than warning: the file is still there.
        Assert.Equal(original, fx.Read("index.html"));
    }

    [Fact]
    public async Task The_refusal_says_how_to_go_ahead_anyway_and_that_works()
    {
        using var fx = new EngineFixture();
        fx.Write("index.html", Page(414));
        var shorter = Page(168);

        var result = await new WriteFileTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                content = shorter,
                allow_shrink = true
            }),
            Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(shorter, fx.Read("index.html"));
    }

    /// <summary>
    /// The guard must not turn ordinary work into a fight. Growing a file, trimming it moderately,
    /// creating a new one and rewriting a short one are all normal and stay untouched.
    /// </summary>
    [Fact]
    public async Task Ordinary_writes_are_not_affected()
    {
        using var fx = new EngineFixture();

        // A new file of any size.
        Assert.True((await new WriteFileTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { path = "new.html", content = Page(10) }),
            Context(fx), CancellationToken.None)).Success);

        // Adding to an existing one - the actual shape of "add a menu entry".
        fx.Write("grow.html", Page(414));
        Assert.True((await new WriteFileTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { path = "grow.html", content = Page(415) }),
            Context(fx), CancellationToken.None)).Success);

        // Losing a third of a long file: real editing, not a symptom.
        fx.Write("trim.html", Page(414));
        Assert.True((await new WriteFileTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { path = "trim.html", content = Page(280) }),
            Context(fx), CancellationToken.None)).Success);

        // A short file, rewritten to nothing. Below the floor there is no risk worth a round trip:
        // a model reproduces a small file reliably, and emptying one is a thing people ask for.
        fx.Write("tiny.txt", "one\ntwo\nthree\n");
        Assert.True((await new WriteFileTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { path = "tiny.txt", content = "" }),
            Context(fx), CancellationToken.None)).Success);
    }

    [Theory]
    // previous, new, refused?
    [InlineData(21164, 7982, true)]    // the reported case
    [InlineData(21164, 10583, false)]  // just over half survives
    [InlineData(21164, 10581, true)]   // just under
    [InlineData(1999, 0, false)]       // under the floor: not our business
    [InlineData(2000, 999, true)]
    [InlineData(2000, 1000, false)]
    [InlineData(0, 0, false)]          // nothing was there
    public void The_rule_itself(long previous, long added, bool refused)
        => Assert.Equal(refused, WriteFileTool.WouldLoseMostOfTheFile(previous, added));
}

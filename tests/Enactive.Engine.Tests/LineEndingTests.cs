namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Docs/FIX_PLAN.md §9b: the fixture's world was LF-only, and the product ships on Windows.
///
/// <para><c>EngineFixture.Write</c> wrote whatever string it was handed, and every test but a
/// handful handed it <c>\n</c>. So <c>edit_file</c> was never tried against a CRLF file until a user
/// found it refusing every edit on one — the model sent five LFs, the file had CRLF on all 413
/// lines, and a carriage return is invisible in what <c>read_file</c> returns. Most text files on
/// the platform this product ships for are the case the suite never ran.</para>
///
/// <para>Every test here runs twice, over both endings, with the fixture writing the file the way
/// the test asks. Where the two behave differently on purpose, the test says which and why.</para>
/// </summary>
public sealed class LineEndingTests
{
    private static ToolContext Context(EngineFixture fx, IArtifactStore? store = null)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: store ?? fx.Artifacts, Services: null!);

    private static Task<ToolResult> Call(ITool tool, EngineFixture fx, string argumentsJson)
        => tool.InvokeAsync(argumentsJson, Context(fx), CancellationToken.None);

    public static TheoryData<Newline> Both => EngineFixture.Endings;

    private const string Page =
        "<header>\n  <nav>\n    <a>Home</a>\n    <a>Docs</a>\n  </nav>\n</header>\n<main>\n  <p>Body</p>\n</main>\n";

    // ── read_file ───────────────────────────────────────────────────────────

    /// <summary>
    /// A window has to count the same lines either way. Splitting on <c>\n</c> and splitting on
    /// <c>\r\n</c> disagree by one on every line if the CR is not accounted for, which would make
    /// "lines 1–400 of 414" wrong on exactly the platform the product runs on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task A_window_counts_the_same_lines_whatever_the_endings(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}")), endings);

        var result = await Call(new ReadFileTool(), fx, """{"path":"doc.md","offset":2,"limit":3}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(500, Convert.ToInt32(result.Metadata!["totalLines"]));
        Assert.Equal(4, Convert.ToInt32(result.Metadata!["lastLine"]));
        Assert.StartsWith("line 2", result.Output);
        Assert.Contains("line 4", result.Output);
        Assert.DoesNotContain("line 5", result.Output);
    }

    /// <summary>
    /// And the bytes come back as they are. A read that normalised endings would hand the model a
    /// document it can never quote back exactly — which is the whole shape of the edit_file failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task A_read_does_not_quietly_normalise_the_file(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "one\ntwo\nthree\n", endings);

        var result = await Call(new ReadFileTool(), fx, """{"path":"doc.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(endings == Newline.Crlf, result.Output!.Contains('\r'));
    }

    // ── edit_file ───────────────────────────────────────────────────────────

    /// <summary>
    /// The reported defect, as a theory rather than as two hand-written cases: a passage typed with
    /// LFs matches either file, and the splice takes the endings the file already had.
    /// </summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task An_edit_typed_with_LF_works_on_either_file(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("index.html", Page, endings);

        var result = await Call(new EditFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                old_string = "    <a>Home</a>\n    <a>Docs</a>\n",
                new_string = "    <a>Home</a>\n    <a>Docs</a>\n    <a>Remote</a>\n"
            }));

        Assert.True(result.Success, result.Error);

        var after = fx.Read("index.html");
        Assert.Contains("<a>Remote</a>", after);
        Assert.Equal(EngineFixture.Normalize(
            Page.Replace("    <a>Docs</a>\n", "    <a>Docs</a>\n    <a>Remote</a>\n"), endings), after);
    }

    /// <summary>The same, typed the other way round: a model that emits CRLF against an LF file.</summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task An_edit_typed_with_CRLF_works_on_either_file(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("index.html", Page, endings);

        var result = await Call(new EditFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                old_string = "  <nav>\r\n    <a>Home</a>",
                new_string = "  <nav>\r\n    <a>Start</a>"
            }));

        Assert.True(result.Success, result.Error);
        Assert.Equal(EngineFixture.Normalize(Page.Replace("<a>Home</a>", "<a>Start</a>"), endings),
                     fx.Read("index.html"));
    }

    // ── search_files ────────────────────────────────────────────────────────

    /// <summary>
    /// A line number is a line number. A search that counted CRLF files differently would send the
    /// model to the wrong line of the right file, which is worse than not finding it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task Search_reports_the_same_line_number_whatever_the_endings(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("src/a.cs", "class A\n{\n    void Broken() { }\n}\n", endings);

        var result = await Call(new SearchFilesTool(), fx, """{"pattern":"Broken"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("a.cs:3:", result.Output);

        // And a carriage return does not ride along into the matched text.
        Assert.DoesNotContain('\r', result.Output!);
    }

    // ── move_file ───────────────────────────────────────────────────────────

    /// <summary>A move is a move: the bytes at the new path are the bytes that were at the old one.</summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task A_move_carries_the_line_endings_across(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("old.md", Page, endings);
        var before = fx.Read("old.md");

        var result = await Call(new MoveFileTool(), fx,
            """{"from":"old.md","to":"new.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(before, fx.Read("new.md"));
        Assert.False(fx.Exists("old.md"));
    }

    // ── write_file ──────────────────────────────────────────────────────────

    /// <summary>
    /// The shrink guard measures BYTES, and a CRLF file is one byte per line larger than the same
    /// text in LF. The rule has to hold either way — a guard that fired on one platform and not the
    /// other would be worse than no guard, because it would be trusted.
    /// </summary>
    [Theory]
    [MemberData(nameof(Both))]
    public async Task The_shrink_guard_holds_on_either_platforms_files(Newline endings)
    {
        using var fx = new EngineFixture();
        var long_ = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"<li>entry number {i}</li>"));
        fx.Write("index.html", long_, endings);

        var result = await Call(new WriteFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                content = EngineFixture.Normalize(
                    string.Join('\n', Enumerable.Range(0, 100).Select(i => $"<li>entry number {i}</li>")), endings)
            }));

        Assert.False(result.Success);
        Assert.Contains("edit_file", result.Error);
        Assert.Equal(EngineFixture.Normalize(long_, endings), fx.Read("index.html"));
    }

    /// <summary>
    /// What write_file does NOT do, pinned so it is a decision rather than a surprise: it writes the
    /// content it is given, exactly. Handing it LF content for a CRLF file therefore converts the
    /// whole document — every line shows as changed in a diff, and a repository with autocrlf churns
    /// on it, for an edit meant to touch one line.
    ///
    /// <para>This is a real hazard and the reason edit_file is the tool a role is told to prefer; it
    /// is NOT a bug in write_file, which is also how a file's endings get deliberately changed. It is
    /// recorded here so that a change to it is a change somebody made on purpose. Docs/FIX_PLAN.md
    /// §9b carries it as open.</para>
    /// </summary>
    [Fact]
    public async Task A_whole_file_rewrite_writes_exactly_what_it_was_given()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "alpha\nbeta\ngamma\n", Newline.Crlf);

        var result = await Call(new WriteFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "notes.md",
                content = "alpha\nbeta\ngamma\ndelta\n"   // LF, and longer, so the shrink guard is not in play
            }));

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain('\r', fx.Read("notes.md"));
    }
}

namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// The defect log: the fixture's world was LF-only, and the product ships on Windows.
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Exact_match_still_normalises_replacement_endings(bool crlf, bool staged, bool multiline)
    {
        using var fx = new EngineFixture();
        var ending = crlf ? "\r\n" : "\n";
        var opposite = crlf ? "\n" : "\r\n";
        var before = $"prefix{ending}old{ending}tail{ending}suffix{ending}";
        var proposals = new Enactive.Workspace.StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals.BeginStep() : fx.Artifacts;
        // Pending content deliberately has different endings from the underlying disk.
        fx.Write("doc.txt", staged ? $"disk{opposite}" : before);
        if (staged)
            await store.CreateAsync("doc.txt", ArtifactKind.FileSet, "text/plain",
                async stream => await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(before)), default);
        var result = await fx.Invoke(new EditFileTool(), System.Text.Json.JsonSerializer.Serialize(new
        {
            path = "doc.txt",
            old_string = multiline ? $"old{ending}tail" : "old",
            new_string = $"new{opposite}extra"
        }), store);
        Assert.True(result.Success, result.Error);
        var expected = $"prefix{ending}new{ending}extra{ending}"
            + (multiline ? "" : $"tail{ending}") + $"suffix{ending}";
        if (staged)
        {
            Assert.Equal(expected, await store.TryReadPendingAsync("doc.txt", default));
            Assert.Equal($"disk{opposite}", fx.Read("doc.txt"));
            foreach (var change in proposals.Changes) Assert.True(proposals.Apply(change.Id).Applied);
        }
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(fx.PathOf("doc.txt")));
    }

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
    /// A whole-file REWRITE is written in the endings the file already has.
    ///
    /// <para>This test used to pin the opposite, on the argument that <c>write_file</c> writes what
    /// it is given and is also how a file's endings get changed deliberately. Its own comment said
    /// it was recorded "so that a change to it is a change somebody made on purpose" — and on
    /// 2026-09-08 that is what happened, because the argument does not survive contact with the
    /// caller. A model cannot SEE a carriage return in what <c>read_file</c> returns, so it cannot
    /// send one either: every conversion here was accidental, and the deliberate case was
    /// indistinguishable from the accident. A shell command converts a file on purpose.</para>
    ///
    /// <para>The cost of the old behaviour was not theoretical: every line of the document shows as
    /// changed in a diff and a repository with autocrlf churns on it, for an edit meant to touch one
    /// line. <c>edit_file</c> has followed this rule since a user found it unusable on Windows; this
    /// is the same defect one level up.</para>
    /// </summary>
    [Fact]
    public async Task A_whole_file_rewrite_keeps_the_endings_the_file_already_had()
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
        Assert.Equal("alpha\r\nbeta\r\ngamma\r\ndelta\r\n", fx.Read("notes.md"));

        // And it says so, because a tool that quietly changes what it was handed is the thing this
        // whole area is about.
        Assert.Contains("line endings the file already used", result.Output);
    }

    /// <summary>The other direction: CRLF content into an LF file does not leave it mixed.</summary>
    [Fact]
    public async Task A_rewrite_of_an_LF_file_does_not_leave_it_with_carriage_returns()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "alpha\nbeta\ngamma\n", Newline.Lf);

        var result = await Call(new WriteFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "notes.md",
                content = "alpha\r\nbeta\r\ngamma\r\ndelta\r\n"
            }));

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain('\r', fx.Read("notes.md"));
    }

    /// <summary>
    /// A NEW file keeps exactly what it was given. There are no endings to be consistent with, and
    /// the caller's choice is the only one there is — the rule is about not changing a document
    /// under somebody, not about having an opinion on line endings.
    /// </summary>
    [Fact]
    public async Task A_new_file_is_written_exactly_as_it_was_given()
    {
        using var fx = new EngineFixture();

        var result = await Call(new WriteFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "fresh.md",
                content = "alpha\r\nbeta\r\n"
            }));

        Assert.True(result.Success, result.Error);
        Assert.Equal("alpha\r\nbeta\r\n", fx.Read("fresh.md"));
        Assert.DoesNotContain("line endings", result.Output);
    }

    /// <summary>A file whose endings already match is left alone, and says nothing about it.</summary>
    [Fact]
    public async Task A_rewrite_that_already_matches_says_nothing_about_endings()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "alpha\nbeta\n", Newline.Lf);

        var result = await Call(new WriteFileTool(), fx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "notes.md",
                content = "alpha\nbeta\ngamma\n"
            }));

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("line endings", result.Output);
    }
}

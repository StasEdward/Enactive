namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The four tools added so the workspace can be worked on without an external MCP server: a
/// surgical edit, a search, a read window, and a journalled move. Every one of them exists to keep
/// a capability INSIDE the guard/journal/revert machinery — a tool that reached the disk directly
/// would be a hole through all of it, which is the whole reason they are here rather than borrowed.
/// </summary>
public sealed class FileToolsTests
{
    private static ToolContext Context(EngineFixture fx, IArtifactStore? store = null)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: store ?? fx.Artifacts, Services: null!);

    private static Task<ToolResult> Call(ITool tool, EngineFixture fx, string argumentsJson,
        IArtifactStore? store = null)
        => tool.InvokeAsync(argumentsJson, Context(fx, store), CancellationToken.None);

    // ── edit_file ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_edit_replaces_only_the_passage_it_names()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "one\ntwo\nthree\n");

        var result = await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"two","new_string":"TWO"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal("one\nTWO\nthree\n", fx.Read("doc.md"));
    }

    // The point of the tool: the rest of the file is not retyped, so it cannot be corrupted. A whole
    // file rewrite is what let a local model leak CJK characters into an identifier on 2026-09-06.
    [Fact]
    public async Task An_edit_leaves_the_rest_of_a_large_file_byte_for_byte()
    {
        using var fx = new EngineFixture();
        var body = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i} — ключевое слово"));
        fx.Write("big.md", body + "\nTARGET\ntail\n");

        var result = await Call(new EditFileTool(), fx,
            """{"path":"big.md","old_string":"TARGET","new_string":"REPLACED"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(body + "\nREPLACED\ntail\n", fx.Read("big.md"));
    }

    [Fact]
    public async Task An_edit_that_matches_nothing_is_refused()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "one\ntwo\n");

        var result = await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"absent","new_string":"x"}""");

        Assert.False(result.Success);
        Assert.Contains("does not appear", result.Error!, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\n", fx.Read("doc.md"));
    }

    // Several matches means the model cannot know which one it meant, so neither can we.
    [Fact]
    public async Task An_edit_that_matches_twice_is_refused_and_says_so()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "value\nvalue\n");

        var result = await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"value","new_string":"x"}""");

        Assert.False(result.Success);
        Assert.Contains("2 times", result.Error!, StringComparison.Ordinal);
        Assert.Equal("value\nvalue\n", fx.Read("doc.md"));
    }

    [Fact]
    public async Task An_empty_new_string_deletes_the_passage_but_a_missing_one_is_an_error()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "keep\nDROP\nkeep\n");

        Assert.True((await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"DROP\n","new_string":""}""")).Success);
        Assert.Equal("keep\nkeep\n", fx.Read("doc.md"));

        var missing = await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"keep"}""");
        Assert.False(missing.Success);
        Assert.Contains("'new_string' is required", missing.Error!, StringComparison.Ordinal);
    }

    // The whole reason it goes through the store: a rejected step must be able to put it back.
    [Fact]
    public async Task An_edit_is_journalled_and_can_be_reverted()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "before\n");

        var step = fx.Artifacts.BeginStep();
        var result = await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"before","new_string":"after"}""", step);

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Artifacts);
        Assert.Equal("after\n", fx.Read("doc.md"));

        await step.RevertAsync(new[] { "doc.md" }, default);
        Assert.Equal("before\n", fx.Read("doc.md"));
    }

    [Fact]
    public async Task An_edit_cannot_reach_outside_the_workspace()
    {
        using var fx = new EngineFixture();

        var result = await Call(new EditFileTool(), fx,
            """{"path":"../outside.txt","old_string":"a","new_string":"b"}""");

        Assert.False(result.Success);
    }

    // Staging holds the current version, so an edit has to apply to the proposal, not to the disk
    // underneath it — otherwise the outstanding proposal would silently win later.
    [Fact]
    public async Task An_edit_applies_to_a_staged_version_when_there_is_one()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "on disk\n");
        var staging = new StagingArtifactStore(fx.Root);

        await Call(new WriteFileTool(), fx,
            """{"path":"doc.md","content":"staged\n"}""", staging);
        var result = await Call(new EditFileTool(), fx,
            """{"path":"doc.md","old_string":"staged","new_string":"staged and edited"}""", staging);

        Assert.True(result.Success, result.Error);
        Assert.Equal("staged and edited\n", await staging.TryReadPendingAsync("doc.md", default));
        Assert.Equal("on disk\n", fx.Read("doc.md"));
    }

    // ── search_files ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_reports_the_file_and_line_of_every_match()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "nothing\nNEEDLE here\n");
        fx.Write("sub/b.txt", "also NEEDLE\n");
        fx.Write("c.txt", "unrelated\n");

        var result = await Call(new SearchFilesTool(), fx, """{"pattern":"NEEDLE"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("a.txt:2:", result.Output!, StringComparison.Ordinal);
        Assert.Contains("sub/b.txt:1:", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("c.txt", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_can_be_narrowed_by_glob_and_folder()
    {
        using var fx = new EngineFixture();
        fx.Write("keep.cs", "token\n");
        fx.Write("skip.txt", "token\n");
        fx.Write("sub/deep.cs", "token\n");

        var byGlob = await Call(new SearchFilesTool(), fx, """{"pattern":"token","glob":"*.cs"}""");
        Assert.Contains("keep.cs", byGlob.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.txt", byGlob.Output!, StringComparison.Ordinal);

        var byFolder = await Call(new SearchFilesTool(), fx, """{"pattern":"token","path":"sub"}""");
        Assert.Contains("deep.cs", byFolder.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("keep.cs", byFolder.Output!, StringComparison.Ordinal);
    }

    // The workspace's own state is not the user's material, and neither are build trees. Returning
    // them buries the real matches.
    [Fact]
    public async Task Search_skips_the_state_folder_and_build_output()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/state.json", "SECRETTOKEN\n");
        fx.Write("bin/Debug/generated.cs", "SECRETTOKEN\n");
        fx.Write("node_modules/pkg/index.js", "SECRETTOKEN\n");
        fx.Write("mine.cs", "SECRETTOKEN\n");

        var result = await Call(new SearchFilesTool(), fx, """{"pattern":"SECRETTOKEN"}""");

        Assert.Contains("mine.cs", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain(".enactive", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("bin/", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_says_plainly_when_there_is_nothing()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "hello\n");

        var result = await Call(new SearchFilesTool(), fx, """{"pattern":"nowhere"}""");

        Assert.True(result.Success);
        Assert.Contains("No matches", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_pattern_is_an_error_not_a_crash()
    {
        using var fx = new EngineFixture();

        var result = await Call(new SearchFilesTool(), fx, """{"pattern":"([unclosed"}""");

        Assert.False(result.Success);
        Assert.Contains("valid regular expression", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_is_case_insensitive_by_default_and_exact_on_request()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "Needle\n");

        Assert.Contains("a.txt", (await Call(new SearchFilesTool(), fx, """{"pattern":"needle"}""")).Output!,
            StringComparison.Ordinal);

        var exact = await Call(new SearchFilesTool(), fx,
            """{"pattern":"needle","ignore_case":false}""");
        Assert.Contains("No matches", exact.Output!, StringComparison.Ordinal);
    }

    // ── read_file window ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_returns_a_window_and_says_how_to_continue()
    {
        using var fx = new EngineFixture();
        fx.Write("long.txt", string.Join('\n', Enumerable.Range(1, 1000).Select(i => $"line {i}")));

        var first = await Call(new ReadFileTool(), fx, """{"path":"long.txt","limit":10}""");

        Assert.True(first.Success, first.Error);
        Assert.Contains("line 1\n", first.Output!, StringComparison.Ordinal);
        Assert.Contains("line 10", first.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("line 11\n", first.Output!, StringComparison.Ordinal);
        Assert.Contains("offset 11", first.Output!, StringComparison.Ordinal);
        Assert.Equal(1000, first.Metadata!["totalLines"]);

        var second = await Call(new ReadFileTool(), fx, """{"path":"long.txt","offset":11,"limit":5}""");
        Assert.Contains("line 11", second.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("line 10\n", second.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_defaults_to_the_start_of_the_file()
    {
        using var fx = new EngineFixture();
        fx.Write("short.txt", "alpha\nbeta\n");

        var result = await Call(new ReadFileTool(), fx, """{"path":"short.txt"}""");

        Assert.Equal("alpha\nbeta\n", result.Output);
        Assert.False((bool)result.Metadata!["truncated"]!);
    }

    [Fact]
    public async Task Reading_past_the_end_says_how_long_the_file_is()
    {
        using var fx = new EngineFixture();
        fx.Write("short.txt", "one\ntwo\n");

        var result = await Call(new ReadFileTool(), fx, """{"path":"short.txt","offset":99}""");

        Assert.False(result.Success);
        Assert.Contains("3 line(s)", result.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"path":"a.txt","offset":0}""")]
    [InlineData("""{"path":"a.txt","limit":0}""")]
    public async Task A_nonsense_window_is_refused(string arguments)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "x\n");

        Assert.False((await Call(new ReadFileTool(), fx, arguments)).Success);
    }

    // ── move_file and create_directory ────────────────────────────────────────────────

    [Fact]
    public async Task A_move_leaves_the_content_at_the_new_path_and_nothing_at_the_old()
    {
        using var fx = new EngineFixture();
        fx.Write("old.md", "the content\n");

        var result = await Call(new MoveFileTool(), fx, """{"from":"old.md","to":"sub/new.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.False(fx.Exists("old.md"));
        Assert.Equal("the content\n", fx.Read("sub/new.md"));
    }

    // Both halves of the move are journalled, so a rejected step undoes the whole rename.
    [Fact]
    public async Task A_move_is_undone_completely_when_the_step_is_reverted()
    {
        using var fx = new EngineFixture();
        fx.Write("old.md", "the content\n");

        var step = fx.Artifacts.BeginStep();
        Assert.True((await Call(new MoveFileTool(), fx, """{"from":"old.md","to":"new.md"}""", step)).Success);

        await step.RevertAsync(new[] { "old.md", "new.md" }, default);

        Assert.Equal("the content\n", fx.Read("old.md"));
        Assert.False(fx.Exists("new.md"));
    }

    // A move must hand back the SAME file. This one read its input as text and wrote UTF-8 back, so a
    // PNG or a zip came out with every byte that is not valid UTF-8 replaced by U+FFFD — and then the
    // original was deleted and the result reported as success. Bytes [0,255,254,128,65,0] came back
    // as [0,239,191,189,…]: silent corruption of an asset, announced as a rename.
    [Fact]
    public async Task A_move_preserves_bytes_that_are_not_text()
    {
        using var fx = new EngineFixture();

        var original = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x41, 0x00 };
        File.WriteAllBytes(fx.PathOf("asset.bin"), original);

        var result = await Call(new MoveFileTool(), fx, """{"from":"asset.bin","to":"moved.bin"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(original, File.ReadAllBytes(fx.PathOf("moved.bin")));
        Assert.False(fx.Exists("asset.bin"));
    }

    // The same failure in the shape it takes on a text file: a UTF-16 document is transcoded and its
    // BOM is gone. The file still "reads", which is what makes it worse than the binary case.
    [Fact]
    public async Task A_move_preserves_a_utf16_file_and_its_bom()
    {
        using var fx = new EngineFixture();

        var original = new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true)
            .GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes("привет"))
            .ToArray();
        File.WriteAllBytes(fx.PathOf("doc.txt"), original);

        Assert.True((await Call(new MoveFileTool(), fx, """{"from":"doc.txt","to":"moved.txt"}""")).Success);

        Assert.Equal(original, File.ReadAllBytes(fx.PathOf("moved.txt")));
    }

    // And the byte count it reports is the file's, not the length of some re-encoding of it.
    [Fact]
    public async Task A_move_reports_the_real_size()
    {
        using var fx = new EngineFixture();

        var original = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x41, 0x00 };
        File.WriteAllBytes(fx.PathOf("asset.bin"), original);

        var result = await Call(new MoveFileTool(), fx, """{"from":"asset.bin","to":"moved.bin"}""");

        Assert.Equal(6L, Assert.Contains("bytes", result.Metadata));
    }

    // Both sides are journalled for a binary too, so a rejected step gets the original bytes back
    // rather than a rendering of them.
    [Fact]
    public async Task Reverting_a_move_restores_the_original_bytes()
    {
        using var fx = new EngineFixture();

        var original = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x41, 0x00 };
        File.WriteAllBytes(fx.PathOf("asset.bin"), original);

        var step = fx.Artifacts.BeginStep();
        Assert.True((await Call(new MoveFileTool(), fx, """{"from":"asset.bin","to":"moved.bin"}""", step)).Success);

        await step.RevertAsync(new[] { "asset.bin", "moved.bin" }, default);

        Assert.Equal(original, File.ReadAllBytes(fx.PathOf("asset.bin")));
        Assert.False(fx.Exists("moved.bin"));
    }

    [Fact]
    public async Task A_move_refuses_to_overwrite()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "source\n");
        fx.Write("b.md", "do not lose me\n");

        var result = await Call(new MoveFileTool(), fx, """{"from":"a.md","to":"b.md"}""");

        Assert.False(result.Success);
        Assert.Equal("do not lose me\n", fx.Read("b.md"));
        Assert.Equal("source\n", fx.Read("a.md"));
    }

    [Fact]
    public async Task A_move_cannot_carry_a_file_out_of_the_workspace()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "x\n");

        Assert.False((await Call(new MoveFileTool(), fx, """{"from":"a.md","to":"../escaped.md"}""")).Success);
        Assert.True(fx.Exists("a.md"));
    }

    // Staging cannot express a deletion, so the tool must say so rather than delete the source
    // behind staging's back.
    [Fact]
    public async Task A_move_under_staging_reports_that_the_original_is_still_there()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "x\n");
        var staging = new StagingArtifactStore(fx.Root);

        var result = await Call(new MoveFileTool(), fx, """{"from":"a.md","to":"b.md"}""", staging);

        Assert.False(result.Success);
        Assert.Contains("still there", result.Error!, StringComparison.Ordinal);
        Assert.True(fx.Exists("a.md"));
    }

    [Fact]
    public async Task Create_directory_makes_missing_parents_and_is_idempotent()
    {
        using var fx = new EngineFixture();

        var first = await Call(new CreateDirectoryTool(), fx, """{"path":"one/two/three"}""");
        Assert.True(first.Success, first.Error);
        Assert.True(Directory.Exists(fx.PathOf("one/two/three")));

        var again = await Call(new CreateDirectoryTool(), fx, """{"path":"one/two/three"}""");
        Assert.True(again.Success);
        Assert.True((bool)again.Metadata!["alreadyExisted"]!);
    }

    [Theory]
    [InlineData("""{"path":"../outside"}""")]
    [InlineData("""{"path":".enactive/sneak"}""")]
    public async Task Create_directory_respects_the_workspace_boundary(string arguments)
    {
        using var fx = new EngineFixture();

        Assert.False((await Call(new CreateDirectoryTool(), fx, arguments)).Success);
    }

    // ── the tools are actually offered to a run ───────────────────────────────────────

    [Fact]
    public async Task The_engine_offers_the_new_tools_to_a_worker_that_allows_them()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look around"}"""),
            Turn.Says("Nothing to do."));

        await fx.RunAsync(fx.Build(provider, worker: EngineFixture.WorkerWith("*")), "look around");

        var offered = provider.Requests.Last().Tools!.Select(t => t.Name).ToArray();
        Assert.Contains("edit_file", offered);
        Assert.Contains("search_files", offered);
        Assert.Contains("move_file", offered);
        Assert.Contains("create_directory", offered);
    }
}

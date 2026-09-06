namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Undo must undo (review finding #6). It used to be an unconditional delete: undoing an edit to an
/// existing source file removed the source and reported "undone".
/// </summary>
public sealed class UndoTests
{
    [Fact]
    public async Task Undoing_a_created_file_deletes_it()
    {
        using var fx = new EngineFixture();
        await Write(fx, "generated.txt", "output");

        var result = fx.Artifacts.Undo("generated.txt");

        Assert.True(result.Undone);
        Assert.False(result.Restored);
        Assert.False(fx.Exists("generated.txt"));
    }

    [Fact]
    public async Task Undoing_an_edit_restores_the_previous_content()
    {
        using var fx = new EngineFixture();
        fx.Write("source.cs", "the user's original code");

        await Write(fx, "source.cs", "what the agent decided to put there");
        Assert.Equal("what the agent decided to put there", fx.Read("source.cs"));

        var result = fx.Artifacts.Undo("source.cs");

        Assert.True(result.Undone);
        Assert.True(result.Restored);
        Assert.Equal("the user's original code", fx.Read("source.cs"));
    }

    [Fact]
    public async Task Undo_refuses_when_the_file_changed_afterwards()
    {
        using var fx = new EngineFixture();
        fx.Write("source.cs", "original");
        await Write(fx, "source.cs", "agent version");

        // The user edited it after the run.
        fx.Write("source.cs", "my own fix on top");

        var result = fx.Artifacts.Undo("source.cs");

        Assert.False(result.Undone);
        Assert.Contains("changed", result.Conflict ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("my own fix on top", fx.Read("source.cs"));
    }

    [Fact]
    public async Task A_second_write_does_not_lose_the_users_original()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "the user's notes");

        await Write(fx, "notes.md", "first attempt");
        await Write(fx, "notes.md", "second attempt");

        var result = fx.Artifacts.Undo("notes.md");

        Assert.True(result.Undone);
        // Not "first attempt": undo goes back to before the RUN, not one write.
        Assert.Equal("the user's notes", fx.Read("notes.md"));
    }

    [Fact]
    public void Undo_declines_a_path_this_run_never_wrote()
    {
        using var fx = new EngineFixture();
        fx.Write("untouched.txt", "as it was");

        var result = fx.Artifacts.Undo("untouched.txt");

        Assert.False(result.Undone);
        Assert.Equal("as it was", fx.Read("untouched.txt"));
    }

    [Fact]
    public async Task The_backup_lives_where_tools_cannot_reach_it()
    {
        using var fx = new EngineFixture();
        fx.Write("source.cs", "original");
        await Write(fx, "source.cs", "agent version");

        var record = fx.Artifacts.WriteFor("source.cs");
        Assert.NotNull(record);
        Assert.NotNull(record!.BackupPath);
        Assert.Contains(WorkspaceGuard.ReservedFolder, record.BackupPath!, StringComparison.Ordinal);
    }

    private static Task Write(EngineFixture fx, string relative, string content)
        => fx.Artifacts.CreateAsync(
            relative, ArtifactKind.FileSet, relative,
            async stream =>
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content);
            },
            CancellationToken.None);
}

/// <summary>
/// Staging must be a working copy, not a promise (review findings #15, #18): staged writes are
/// readable, and applying one does not silently erase an edit made in the meantime.
/// </summary>
public sealed class StagingTests
{
    [Fact]
    public async Task A_staged_write_does_not_touch_the_workspace()
    {
        using var fx = new EngineFixture();
        fx.Write("config.json", "{\"original\":true}");
        var staging = new StagingArtifactStore(fx.Root);

        await Stage(staging, "config.json", "{\"proposed\":true}");

        Assert.Equal("{\"original\":true}", fx.Read("config.json"));
    }

    // Finding #18: write_file then read_file returned the OLD content, or "File not found" for a new
    // file — while the messages said the result was on disk.
    [Fact]
    public async Task A_staged_write_is_visible_to_read_file()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write then verify"}"""),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the real numbers"}""", "c1"),
            Turn.Calls1("read_file", """{"path":"report.md"}""", "c2"),
            Turn.Says("Verified."));

        var orchestrator = fx.Build(provider, artifacts: staging);
        var events = await fx.RunAsync(orchestrator, "write report.md and read it back");

        var readBack = events.OfKind(EventKind.ToolResult)
            .Last(e => e.Summary.Contains("read_file", StringComparison.Ordinal));

        Assert.Contains("the real numbers", readBack.Summary, StringComparison.Ordinal);
        Assert.False(fx.Exists("report.md"));   // still only proposed
    }

    [Fact]
    public async Task A_dependent_step_sees_what_the_previous_step_staged()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        var provider = new FakeChatProvider(
            Turn.Says("""
                {"disposition":"task","title":"two steps",
                 "steps":[{"title":"write it","dependsOn":[]},{"title":"read it","dependsOn":[0]}]}
                """),
            Turn.Calls1("write_file", """{"path":"data.txt","content":"step one output"}""", "c1"),
            Turn.Says("Written."),
            Turn.Calls1("read_file", """{"path":"data.txt"}""", "c2"),
            Turn.Says("Read it."));

        var orchestrator = fx.Build(provider, artifacts: staging);
        var events = await fx.RunAsync(orchestrator, "write then read");

        var readBack = events.OfKind(EventKind.ToolResult)
            .Last(e => e.Summary.Contains("read_file", StringComparison.Ordinal));

        Assert.Contains("step one output", readBack.Summary, StringComparison.Ordinal);
    }

    // Finding #15: OldContent was captured for the diff and never compared to anything, so Apply
    // silently destroyed whatever had happened to the file since.
    [Fact]
    public async Task Apply_refuses_when_the_file_changed_after_staging()
    {
        using var fx = new EngineFixture();
        fx.Write("shared.txt", "original");
        var staging = new StagingArtifactStore(fx.Root);

        var reference = await Stage(staging, "shared.txt", "proposal");

        fx.Write("shared.txt", "user edit after staging");

        var result = staging.Apply(reference.Id);

        Assert.False(result.Applied);
        Assert.Equal("user edit after staging", fx.Read("shared.txt"));
        Assert.Contains("changed", result.Conflict ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_writes_when_nothing_moved()
    {
        using var fx = new EngineFixture();
        fx.Write("shared.txt", "original");
        var staging = new StagingArtifactStore(fx.Root);

        var reference = await Stage(staging, "shared.txt", "proposal");
        var result = staging.Apply(reference.Id);

        Assert.True(result.Applied, result.Conflict);
        Assert.Equal("proposal", fx.Read("shared.txt"));
    }

    [Fact]
    public async Task Two_proposals_for_one_path_apply_in_order()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "v0");
        var staging = new StagingArtifactStore(fx.Root);

        var first = await Stage(staging, "doc.md", "v1");
        var second = await Stage(staging, "doc.md", "v2");

        // Applying the newer one first would make the older one's "before" wrong.
        var outOfOrder = staging.Apply(second.Id);
        Assert.False(outOfOrder.Applied);
        Assert.Equal("v0", fx.Read("doc.md"));

        Assert.True(staging.Apply(first.Id).Applied);
        Assert.True(staging.Apply(second.Id).Applied);
        Assert.Equal("v2", fx.Read("doc.md"));
    }

    [Fact]
    public async Task A_rejected_proposal_is_no_longer_served_to_reads()
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "on disk");
        var staging = new StagingArtifactStore(fx.Root);

        var reference = await Stage(staging, "doc.md", "proposed");
        Assert.Equal("proposed", await staging.TryReadPendingAsync("doc.md", CancellationToken.None));

        staging.Reject(reference.Id);

        Assert.Null(await staging.TryReadPendingAsync("doc.md", CancellationToken.None));
    }

    private static Task<ArtifactRef> Stage(StagingArtifactStore staging, string relative, string content)
        => staging.CreateAsync(
            relative, ArtifactKind.FileSet, relative,
            async stream =>
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content);
            },
            CancellationToken.None);
}

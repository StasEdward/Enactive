namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;

public sealed class StagedLineageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_streams_pending_bytes_for_new_and_replaced_sources(bool exists)
    {
        using var fx = new EngineFixture();
        using var store = new StagingArtifactStore(fx.Root);
        if (exists) fx.Write("source.bin", "OLD");
        byte[] bytes = [0xff, 0xfe, 0x00, 0x41, 0x80];
        await store.CreateAsync("source.bin", ArtifactKind.FileSet, "source",
            s => s.WriteAsync(bytes).AsTask(), default);
        var result = await fx.Invoke(new CopyFileTool(), """{"from":"source.bin","to":"copy.bin"}""", store);
        Assert.True(result.Success, result.Error);
        await using var copy = await store.TryOpenPendingAsync("copy.bin", default);
        using var output = new MemoryStream();
        await copy!.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.False(fx.Exists("copy.bin"));
        var move = await fx.Invoke(new MoveFileTool(), """{"from":"source.bin","to":"moved.bin"}""", store);
        Assert.False(move.Success);
        Assert.Contains("staging cannot express a deletion", move.Error);
        Assert.DoesNotContain("moved.bin", store.PendingPaths);
    }

    [Fact]
    public async Task Revert_does_not_claim_to_remove_bytes_retained_by_a_foreign_append()
    {
        using var fx = new EngineFixture();
        using var store = new StagingArtifactStore(fx.Root);
        var a = store.BeginStep();
        var b = store.BeginStep();
        await Put(a, "a.txt", "A\n");
        Assert.True((await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"B\n"}""", b)).Success);
        Assert.Equal(store.Changes[0].Id, store.Changes[1].ParentId);
        var report = await a.RevertAsync(["a.txt"], default);
        Assert.Empty(report.Reverted);
        Assert.Contains("a.txt", report.Kept);
        Assert.Contains("depends", report.WhyKept("a.txt"));
        Assert.Equal("A\nB\n", await store.TryReadPendingAsync("a.txt", default));
        Assert.False(store.Reject(store.Changes[0].Id).Rejected);
        Assert.True(store.Reject(store.Changes[1].Id).Rejected);
        Assert.Contains("a.txt", (await a.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Null(await store.TryReadPendingAsync("a.txt", default));
        Assert.False(store.Apply(store.Changes[1].Id).Applied);
    }

    [Fact]
    public async Task Pending_paths_belong_to_the_owner_and_applied_changes_are_not_reported_as_reverted()
    {
        using var fx = new EngineFixture();
        using var store = new StagingArtifactStore(fx.Root);
        var a = store.BeginStep();
        var b = store.BeginStep();
        await Put(a, "a.txt", "A");
        await Put(b, "b.txt", "B");
        Assert.Equal(["a.txt"], a.PendingPaths);
        Assert.Equal(["b.txt"], b.PendingPaths);
        Assert.True(store.Apply(store.Changes[0].Id).Applied);
        Assert.Empty(a.PendingPaths);
        Assert.Contains("a.txt", (await a.RevertAsync(["a.txt"], default)).Kept);
        Assert.Equal("A", fx.Read("a.txt"));
    }

    [Fact]
    public async Task Apply_and_reject_refuse_while_a_child_proposal_is_being_written()
    {
        using var fx = new EngineFixture();
        using var store = new StagingArtifactStore(fx.Root);
        await Put(store, "a.txt", "A");
        var first = store.Changes[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = store.CreateAsync("a.txt", ArtifactKind.FileSet, "child", async output =>
        {
            entered.SetResult();
            await release.Task;
            await output.WriteAsync(Encoding.UTF8.GetBytes("AB"));
        }, default);
        await entered.Task;
        try
        {
            Assert.False(store.Apply(first.Id).Applied);
            Assert.False(store.Reject(first.Id).Rejected);
        }
        finally { release.TrySetResult(); }
        await child;
        Assert.Equal(first.Id, store.Changes[1].ParentId);
        Assert.Equal("AB", await store.TryReadPendingAsync("a.txt", default));
    }

    [Fact]
    public void Discarded_file_text_requires_fresh_read_coverage()
    {
        var ledger = new ReadLedger();
        var read = new ReadFileTool().Definition;
        var write = new WriteFileTool().Definition;
        var evidence = ToolResults.Ok(metadata: new Dictionary<string, object?>
            { ["path"] = "a", ["firstLine"] = 1, ["lastLine"] = 10, ["totalLines"] = 10 });
        ledger.Saw(new("r", "read_file", "{}"), evidence, read);
        Assert.Null(ledger.Refuse(new("w", "write_file", "{}"), "a", write));
        ledger.ForgetDiscardedReads();
        Assert.NotNull(ledger.Refuse(new("w", "write_file", "{}"), "a", write));
        ledger.Saw(new("r2", "read_file", "{}"), evidence, read);
        Assert.Null(ledger.Refuse(new("w", "write_file", "{}"), "a", write));
    }

    private static Task<ArtifactRef> Put(IArtifactStore store, string path, string text)
        => store.CreateAsync(path, ArtifactKind.FileSet, path,
            s => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask(), default);
}

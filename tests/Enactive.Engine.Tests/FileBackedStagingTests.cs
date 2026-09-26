namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

[Collection("Bounded file allocations")]
public sealed class FileBackedStagingTests
{
    [Fact]
    public async Task Large_staged_append_and_apply_use_bounded_memory_and_preserve_prior_version()
    {
        using var fx = new EngineFixture();
        using var store = new StagingArtifactStore(fx.Root);
        var block = Encoding.UTF8.GetBytes(new string('x', 8191) + "\n");
        using (var file = File.Create(fx.PathOf("large.txt")))
            for (var i = 0; i < 8192; i++) file.Write(block);
        await fx.Invoke(new WriteFileTool(), """{"path":"warm.txt","content":"warm"}""", store);
        var before = GC.GetTotalAllocatedBytes(true);
        var result = await fx.Invoke(new WriteFileTool(),
            """{"path":"large.txt","append":true,"content":"tail"}""", store);
        Assert.True(result.Success, result.Error);
        var change = store.Changes.Last();
        await using (var original = change.OldBytes!.Open())
            Assert.Equal(64L * 1024 * 1024, original.Length);
        await using (var pending = await store.TryOpenPendingAsync("large.txt", default))
        {
            Assert.NotNull(pending);
            pending.Seek(-5, SeekOrigin.End);
            var tail = new byte[5];
            await pending.ReadExactlyAsync(tail);
            Assert.Equal("\ntail", Encoding.UTF8.GetString(tail));
        }
        Assert.True(store.Apply(change.Id).Applied);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(allocated < 16 * 1024 * 1024, $"Allocated {allocated:N0} bytes");
        Assert.Equal(64L * 1024 * 1024 + 4, new FileInfo(fx.PathOf("large.txt")).Length);
    }

    [Fact]
    public async Task Concurrent_appends_chain_pending_versions_and_revert_only_the_last_owner()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base\n");
        using var store = new StagingArtifactStore(fx.Root);
        var a = store.BeginStep();
        var b = store.BeginStep();
        var results = await Task.WhenAll(
            fx.Invoke(new WriteFileTool(), """{"path":"a.txt","content":"A\n","append":true}""", a),
            fx.Invoke(new WriteFileTool(), """{"path":"a.txt","content":"B\n","append":true}""", b));
        Assert.All(results, r => Assert.True(r.Success, r.Error));
        var text = await store.TryReadPendingAsync("a.txt", default);
        Assert.Contains("A\n", text);
        Assert.Contains("B\n", text);
        var last = text!.EndsWith("B\n", StringComparison.Ordinal) ? b : a;
        Assert.Same(store.Changes[0].NewBytes, store.Changes[1].OldBytes);
        await last.RevertAsync(["a.txt"], default);
        Assert.Equal(text.EndsWith("B\n", StringComparison.Ordinal) ? "base\nA\n" : "base\nB\n",
            await store.TryReadPendingAsync("a.txt", default));
        Assert.Equal("base\n", fx.Read("a.txt"));
    }

    [Fact]
    public async Task Failure_and_cancellation_remove_temporary_payloads_without_publishing_a_change()
    {
        using var fx = new EngineFixture();
        using var store = new StagingArtifactStore(fx.Root);
        foreach (var cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            string? temporary = null;
            async Task Write(Stream stream)
            {
                temporary = ((FileStream)stream).Name;
                await stream.WriteAsync(new byte[4096]);
                if (cancel) cts.Cancel();
                else throw new IOException("interrupted");
            }
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.CreateAsync("new.bin", ArtifactKind.FileSet, "new", Write, cts.Token));
            else
                await Assert.ThrowsAsync<IOException>(() =>
                    store.CreateAsync("new.bin", ArtifactKind.FileSet, "new", Write, cts.Token));
            Assert.NotNull(temporary);
            Assert.False(File.Exists(temporary));
            Assert.Empty(store.Changes);
            Assert.False(fx.Exists("new.bin"));
        }
    }

    [Fact]
    public async Task Disposing_store_releases_payload_and_open_readers_keep_their_own_handle()
    {
        using var fx = new EngineFixture();
        var store = new StagingArtifactStore(fx.Root);
        string? temporary = null;
        var reference = await store.CreateAsync("a.txt", ArtifactKind.FileSet, "a", async s =>
        {
            temporary = ((FileStream)s).Name;
            // Existing callers may close the callback stream.
            using var writer = new StreamWriter(s);
            await writer.WriteAsync("kept");
        }, default);
        using (var reader = new StreamReader(await store.OpenAsync(reference.Id, default)))
        {
            store.Dispose();
            Assert.Equal("kept", await reader.ReadToEndAsync());
        }
        Assert.False(File.Exists(temporary));
    }
}

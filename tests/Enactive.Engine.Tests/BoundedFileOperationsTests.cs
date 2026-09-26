namespace Enactive.Engine.Tests;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

[CollectionDefinition("Bounded file allocations", DisableParallelization = true)]
public sealed class BoundedFileAllocationCollection;

[Collection("Bounded file allocations")]
public sealed class BoundedFileOperationsTests
{
    [Fact]
    public async Task Listing_short_names_stops_at_the_entry_limit()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i <= ListDirectoryTool.MaxEntries; i++) fx.Write($"entries/{i:D3}", "");
        var result = await fx.Invoke(new ListDirectoryTool(), """{"path":"entries"}""");
        Assert.True(result.Success, result.Error);
        Assert.Contains("listing truncated", result.Output);
        Assert.Equal(ListDirectoryTool.MaxEntries, result.Output!.Split('\n').Length - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_is_bounded_and_pattern_can_reach_omitted_entries(bool staged)
    {
        using var fx = new EngineFixture();
        var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals : fx.Artifacts;
        for (var i = 0; i < 230; i++)
        {
            var name = $"entries/{i:D3}-" + new string('x', 150) + ".txt";
            if (staged) await store.CreateAsync(name, ArtifactKind.FileSet, name, _ => Task.CompletedTask, default);
            else fx.Write(name, "");
        }
        var result = await fx.Invoke(new ListDirectoryTool(), """{"path":"entries"}""", store);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Output!.Length <= ListDirectoryTool.MaxOutputChars);
        Assert.Contains("listing truncated", result.Output);
        var narrowed = await fx.Invoke(new ListDirectoryTool(), """{"path":"entries","pattern":"229-*"}""", store);
        Assert.True(narrowed.Success, narrowed.Error);
        Assert.Contains("229-", narrowed.Output);
        Assert.DoesNotContain("truncated", narrowed.Output);
        Assert.Equal(staged, narrowed.Output!.Contains("proposed"));
    }

    [Fact]
    public async Task Listing_cancellation_propagates()
    {
        using var fx = new EngineFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ListDirectoryTool().InvokeAsync("{}", fx.ContextFor(), new CancellationToken(true)));
    }

    [Fact]
    public async Task Append_to_new_file_keeps_supplied_endings()
    {
        using var fx = new EngineFixture();
        var result = await fx.Invoke(new WriteFileTool(), """{"path":"new.txt","append":true,"content":"a\r\nb\r\n"}""");
        Assert.True(result.Success, result.Error);
        Assert.Equal("a\r\nb\r\n", fx.Read("new.txt"));
    }

    [Fact]
    public async Task Concurrent_appends_keep_both_additions_and_owned_rollback()
    {
        using var fx = new EngineFixture();
        fx.Write("text.txt", "base\n");
        var first = fx.Artifacts.BeginStep();
        var second = fx.Artifacts.BeginStep();
        var results = await Task.WhenAll(
            fx.Invoke(new WriteFileTool(), """{"path":"text.txt","append":true,"content":"first\n"}""", first),
            fx.Invoke(new WriteFileTool(), """{"path":"text.txt","append":true,"content":"second\n"}""", second));
        Assert.All(results, r => Assert.True(r.Success, r.Error));
        var content = fx.Read("text.txt");
        Assert.Contains("first\n", content);
        Assert.Contains("second\n", content);
        var last = content.EndsWith("second\n", StringComparison.Ordinal) ? second : first;
        var reverted = await last.RevertAsync(["text.txt"], default);
        Assert.Contains("text.txt", reverted.Reverted);
        Assert.Equal(content.EndsWith("second\n", StringComparison.Ordinal) ? "base\nfirst\n" : "base\nsecond\n", fx.Read("text.txt"));
    }

    [Fact]
    public async Task Large_disk_append_has_bounded_allocations_and_is_restorable()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("large.txt");
        var block = Encoding.UTF8.GetBytes(new string('x', 8191) + "\n");
        using (var file = File.Create(path))
            for (var i = 0; i < 8192; i++) file.Write(block); // 64 MiB, no giant fixture string
        var scope = fx.Artifacts.BeginStep();
        // Warm JIT and pools before measuring. Includes the real disk store's backup/hash path.
        await fx.Invoke(new WriteFileTool(), """{"path":"warm.txt","append":true,"content":"warm"}""", scope);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await fx.Invoke(new WriteFileTool(), """{"path":"large.txt","append":true,"content":"tail"}""", scope);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(result.Success, result.Error);
        Assert.True(allocated < 16 * 1024 * 1024, $"Allocated {allocated:N0} bytes for a 64 MiB append");
        Assert.Equal(64L * 1024 * 1024 + 4, new FileInfo(path).Length);
        using (var file = File.OpenRead(path))
        {
            file.Seek(-5, SeekOrigin.End);
            var tail = new byte[5];
            file.ReadExactly(tail);
            Assert.Equal("\ntail", Encoding.UTF8.GetString(tail));
        }
        var reverted = await scope.RevertAsync(["large.txt"], default);
        Assert.Contains("large.txt", reverted.Reverted);
        Assert.Equal(64L * 1024 * 1024, new FileInfo(path).Length);
    }
}

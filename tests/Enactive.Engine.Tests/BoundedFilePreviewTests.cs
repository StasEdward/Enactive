namespace Enactive.Engine.Tests;

using Enactive.Core.Tools;
using Enactive.Agents;
using Enactive.Tools;
using Xunit;

[CollectionDefinition("Preview allocations", DisableParallelization = true)]
public sealed class PreviewAllocationCollection { }

[Collection("Preview allocations")]
public sealed class BoundedFilePreviewTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Large_preview_bounds_allocations_and_announces_the_input_limit()
    {
        using var fx = new EngineFixture();
        var path = Path.Combine(fx.Root, "large.log");
        var block = new byte[65536];
        Array.Fill(block, (byte)'x');
        block[^1] = (byte)'\n';
        using (var file = File.Create(path))
            for (var i = 0; i < 2048; i++) file.Write(block); // 128 MiB, no giant setup string
        fx.Write("small.txt", "warmup");
        await fx.Invoke(new ReadFilesTool(), """{"paths":["small.txt"]}""");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var rssBefore = process.WorkingSet64;
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes(true);
        var result = await fx.Invoke(new ReadFilesTool(), """{"paths":["large.log"]}""");
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        clock.Stop();
        process.Refresh();
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            scenario = "disk-preview-128MiB", runtime = Environment.Version.ToString(),
            elapsedMs = clock.Elapsed.TotalMilliseconds, allocatedBytes = allocated,
            rssBeforeBytes = rssBefore, rssAfterBytes = process.WorkingSet64,
            gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray()
        }));
        Assert.True(result.Success, result.Error);
        Assert.True(allocated < 8 * 1024 * 1024, $"Allocated {allocated:N0} bytes for a 128 MiB file");
        Assert.Contains("input scan stopped", result.Output);
        Assert.Contains(FilePreview.MaxInputChars.ToString(), result.Output);
        Assert.Contains("NOT the file end", result.Output);
        var coverage = Assert.Single(Assert.IsType<List<FileCoverage>>(result.Metadata["files"]));
        Assert.False(coverage.TotalLinesKnown);
        Assert.Equal(0, coverage.LinesShownWhole);
        var ledger = new ReadLedger();
        ledger.Saw(new ToolCall("read", "read_files", """{"paths":["large.log"]}"""), result, new ReadFilesTool().Definition);
        Assert.NotNull(ledger.Refuse(new ToolCall("write", "write_file", "{}"), "large.log", new WriteFileTool().Definition));
    }

    [Fact]
    public async Task Preview_reads_at_most_the_limit_plus_lookahead()
    {
        using var reader = new CountingReader();
        var preview = await FilePreview.ReadAsync(reader, 4000, default);
        Assert.Equal(FilePreview.MaxInputChars + 1, reader.Count);
        Assert.False(preview.Complete);
        Assert.True(preview.Text.Length < 4200);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4000)]
    [InlineData(4001)]
    [InlineData(1048576)]
    [InlineData(1048577)]
    public async Task Disk_and_staged_readers_have_the_same_preview(int size)
    {
        using var fx = new EngineFixture();
        var content = new string('x', size);
        fx.Write("file.txt", content);
        using var disk = new StreamReader(Path.Combine(fx.Root, "file.txt"));
        using var staged = new StringReader(content);
        var actual = await FilePreview.ReadAsync(disk, 4000, default);
        Assert.Equal(await FilePreview.ReadAsync(staged, 4000, default), actual);
        Assert.Equal(size <= FilePreview.MaxInputChars, actual.Complete);
    }

    private sealed class CountingReader : TextReader
    {
        public int Count;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill('x');
            Count += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }
}

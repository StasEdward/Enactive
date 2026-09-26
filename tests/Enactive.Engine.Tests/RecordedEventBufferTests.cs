namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

public sealed class RecordedEventBufferTests
{
    [Fact]
    public void Token_chunks_preserve_text_and_bound_serialized_overhead()
    {
        var run = Guid.NewGuid();
        var task = Guid.NewGuid();
        var original = Enumerable.Range(0, 10000).Select(i =>
            new WorkEvent(Guid.NewGuid(), task, run, DateTimeOffset.UtcNow, EventKind.AssistantDelta, "word ", "{\"step\":1}")).ToArray();
        var buffer = new RecordedEventBuffer();
        foreach (var ev in original) buffer.Add(ev);
        var evidence = new WorkEvent(Guid.NewGuid(), task, run, DateTimeOffset.UtcNow, EventKind.ToolResult, "unchanged result", "{}");
        buffer.Add(evidence);
        buffer.Flush();
        Assert.Same(evidence, buffer.Events[^1]);
        Assert.Equal(string.Concat(original.Select(e => e.Summary)),
            string.Concat(buffer.Events.Where(e => e.Kind == EventKind.AssistantDelta).Select(e => e.Summary)));
        Assert.True(buffer.Events.Count < 20);
        var originalBytes = JsonSerializer.SerializeToUtf8Bytes(original).Length;
        var retainedBytes = JsonSerializer.SerializeToUtf8Bytes(buffer.Events).Length;
        Assert.True(retainedBytes < originalBytes / 10, $"Before: {originalBytes}; after: {retainedBytes}");
    }
}

namespace Enactive.Engine.Tests;

using Enactive.Tools;

public sealed class ProcessCaptureConcurrencyTests
{
    [Fact]
    public async Task Snapshot_is_safe_while_callbacks_append_and_trim_the_tail()
    {
        var capture = new ProcessExec.CapturedStream();
        capture.Add("HEAD_MARKER");
        for (var i = 0; i < 1000; i++) capture.Add(new string('x', 256));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 10000; i++) capture.Add($"writer {n} {i}: " + new string('y', 200));
        })).ToArray();
        var reader = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 1000; i++)
            {
                var snapshot = capture.ToString();
                Assert.StartsWith("HEAD_MARKER", snapshot);
                Assert.True(snapshot.Length < 130000);
            }
        });
        start.SetResult();
        await Task.WhenAll(writers.Append(reader)).WaitAsync(TimeSpan.FromSeconds(10));
        capture.Add("FINAL_MARKER");
        Assert.EndsWith("FINAL_MARKER" + Environment.NewLine, capture.ToString());
    }
}

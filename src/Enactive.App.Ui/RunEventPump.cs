namespace Enactive.App.Ui;

using System.Threading.Channels;
using Enactive.Core.Events;

/// <summary>Bounds queued UI work and drains it before the owning run can finish.</summary>
internal static class RunEventPump
{
    public const int Capacity = 128;

    public static async Task RunAsync(IAsyncEnumerable<WorkEvent> source,
        Func<IReadOnlyList<WorkEvent>, Task> render, CancellationToken ct)
    {
        using var renderingFailed = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, renderingFailed.Token);
        var pending = Channel.CreateBounded<WorkEvent>(new BoundedChannelOptions(Capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in source.WithCancellation(lifetime.Token).ConfigureAwait(false))
                    await pending.Writer.WriteAsync(item, renderingFailed.Token).ConfigureAwait(false);
            }
            finally { pending.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            while (await pending.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // A short frame interval coalesces token bursts without delaying decisions noticeably.
                await Task.Delay(33, CancellationToken.None).ConfigureAwait(false);
                var batch = new List<WorkEvent>(Capacity);
                while (batch.Count < Capacity && pending.Reader.TryRead(out var item)) batch.Add(item);
                await render(batch).ConfigureAwait(false);
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            try { await renderingFailed.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Event pump cancellation callback failed: {0}", ex); }
            finally
            {
                try { await producer.ConfigureAwait(false); }
                catch (Exception) { /* Preserve the rendering/source exception already in flight. */ }
            }
        }
    }
}

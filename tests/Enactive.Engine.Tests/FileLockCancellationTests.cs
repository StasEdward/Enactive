namespace Enactive.Engine.Tests;

using Enactive.Core.Storage;
using Xunit;

/// <summary>
/// A cancelled wait for the file lock ends, for the blocking Take as for TakeAsync - and leaves the
/// lock takeable afterwards.
///
/// <para><b>Found 2026-09-24</b>: Take passed its token to
/// the in-process gate and then waited for the other PROCESS with Thread.Sleep, which no token can
/// end. Cancelled while another process held the file, it went on waiting up to the whole 30 seconds
/// - and then returned the lock it had been told to stop waiting for. TakeAsync, waiting with
/// Task.Delay(PollMs, ct), did not have the defect; nothing tested either.</para>
/// </summary>
public sealed class FileLockCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-filelock", Guid.NewGuid().ToString("N"));
    private readonly string _data;

    public FileLockCancellationTests()
    {
        Directory.CreateDirectory(_root);
        _data = Path.Combine(_root, "store.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>How another process holds the lock: its file, shared with nobody.</summary>
    private FileStream HeldByAnotherProcess()
        => new(FileLock.LockPathFor(_data), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static TheoryData<bool> BothShapes => new() { false, true };

    private Task<FileLock.Hold> Take(bool async, CancellationToken ct)
        => async ? FileLock.TakeAsync(_data, ct) : Task.Run(() => FileLock.Take(_data, ct));

    /// <summary>Afterwards the lock can be taken again at once - the gate was given back.</summary>
    private async Task StillTakeable(bool async)
    {
        using var again = await Take(async, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(BothShapes))]
    public async Task An_already_cancelled_token_takes_nothing(bool async)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Take(async, cancelled.Token));
        await StillTakeable(async);
    }

    /// <summary>THE ONE THAT WAS BROKEN: cancelled while another process holds the file.</summary>
    [Theory]
    [MemberData(nameof(BothShapes))]
    public async Task A_wait_for_another_process_ends_when_cancelled(bool async)
    {
        using var cts = new CancellationTokenSource();
        var foreign = HeldByAnotherProcess();
        Task<FileLock.Hold>? waiting = null;
        try
        {
            waiting = Take(async, cts.Token);
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);

            cts.Cancel();

            // Well inside the 30 seconds it used to wait out - and with the other process still
            // holding the file, so the ending is the cancellation's and not the lock coming free.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            foreign.Dispose();
            if (waiting is { IsCompletedSuccessfully: true })
                waiting.Result.Dispose();
        }

        await StillTakeable(async);
    }

    /// <summary>Cancelled while ANOTHER TAKE in this process holds the gate.</summary>
    [Theory]
    [MemberData(nameof(BothShapes))]
    public async Task A_wait_for_the_gate_ends_when_cancelled(bool async)
    {
        using var cts = new CancellationTokenSource();
        var first = await Take(async, CancellationToken.None);
        Task<FileLock.Hold>? waiting = null;
        try
        {
            waiting = Take(async, cts.Token);
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            first.Dispose();
            if (waiting is { IsCompletedSuccessfully: true })
                waiting.Result.Dispose();
        }

        await StillTakeable(async);
    }
}

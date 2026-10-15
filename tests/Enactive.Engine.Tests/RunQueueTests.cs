namespace Enactive.Engine.Tests;

using Enactive.Application;
using Xunit;

/// <summary>
/// The service's line of runs (server plan A3): at most N going, one per folder, and a run that has to wait says why
/// rather than leaving the person who started it to poll.
/// </summary>
public sealed class RunQueueTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "enactive-queue", "a");
    private static readonly string B = Path.Combine(Path.GetTempPath(), "enactive-queue", "b");
    private static readonly string C = Path.Combine(Path.GetTempPath(), "enactive-queue", "c");

    [Fact]
    public void Runs_start_at_once_while_there_are_slots_and_free_folders()
    {
        var queue = new RunQueue(maxActive: 2);

        using var first = queue.Enqueue(A);
        using var second = queue.Enqueue(B);

        Assert.True(first.Started.IsCompletedSuccessfully);
        Assert.True(second.Started.IsCompletedSuccessfully);
        Assert.Null(first.Waiting);
        Assert.Equal(2, queue.Active);
    }

    /// <summary>Two agents in one folder edit the same files: the second run waits, however its path was written.</summary>
    [Fact]
    public void A_second_run_in_a_busy_folder_waits_and_says_why_then_starts_when_the_first_ends()
    {
        var queue = new RunQueue(maxActive: 4);
        var first = queue.Enqueue(A);

        using var second = queue.Enqueue(" " + A + " ");

        Assert.False(second.Started.IsCompleted);
        Assert.Contains("Another run in this folder", second.Waiting);

        first.Dispose();
        Assert.True(second.Started.IsCompletedSuccessfully);
        Assert.Null(second.Waiting);
    }

    [Fact]
    public void A_run_waits_for_a_slot_when_all_are_taken_and_says_how_many_are_ahead()
    {
        var queue = new RunQueue(maxActive: 1);
        var running = queue.Enqueue(A);
        using var next = queue.Enqueue(B);
        using var after = queue.Enqueue(C);

        Assert.Contains("All 1 run slots are taken", next.Waiting);
        Assert.DoesNotContain("ahead", next.Waiting);
        Assert.Contains("(1 ahead of it)", after.Waiting);

        running.Dispose();
        Assert.True(next.Started.IsCompletedSuccessfully);
        Assert.False(after.Started.IsCompleted);
    }

    /// <summary>
    /// Strict first-in-first-out would leave the free slot idle behind a run waiting for a folder somebody else is
    /// using; the earliest run that CAN start, does.
    /// </summary>
    [Fact]
    public void A_run_for_a_free_folder_is_not_held_behind_one_waiting_for_a_busy_folder()
    {
        var queue = new RunQueue(maxActive: 2);
        using var running = queue.Enqueue(A);
        using var sameFolder = queue.Enqueue(A);

        using var elsewhere = queue.Enqueue(B);

        Assert.False(sameFolder.Started.IsCompleted);
        Assert.True(elsewhere.Started.IsCompletedSuccessfully);
    }

    /// <summary>Two runs waiting for one folder start in the order they were asked for.</summary>
    [Fact]
    public void Runs_waiting_for_one_folder_start_in_the_order_they_came()
    {
        var queue = new RunQueue(maxActive: 3);
        var running = queue.Enqueue(A);
        var earlier = queue.Enqueue(A);
        using var later = queue.Enqueue(A);

        running.Dispose();
        Assert.True(earlier.Started.IsCompletedSuccessfully);
        Assert.False(later.Started.IsCompleted);
        Assert.Contains("Another run in this folder", later.Waiting);

        earlier.Dispose();
        Assert.True(later.Started.IsCompletedSuccessfully);
    }

    /// <summary>A person who gives up waiting leaves the line, and takes no slot or folder with them.</summary>
    [Fact]
    public async Task A_run_given_up_while_waiting_leaves_the_line_and_holds_nothing()
    {
        var queue = new RunQueue(maxActive: 1);
        var running = queue.Enqueue(A);
        var givenUp = queue.Enqueue(B);
        using var behind = queue.Enqueue(C);

        givenUp.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => givenUp.Started);
        Assert.DoesNotContain("ahead", behind.Waiting);

        running.Dispose();
        Assert.True(behind.Started.IsCompletedSuccessfully);
        Assert.Equal(1, queue.Active);
    }

    [Fact]
    public void Letting_go_twice_frees_one_slot()
    {
        var queue = new RunQueue(maxActive: 1);
        var first = queue.Enqueue(A);
        using var second = queue.Enqueue(B);
        using var third = queue.Enqueue(C);

        first.Dispose();
        first.Dispose();

        Assert.True(second.Started.IsCompletedSuccessfully);
        Assert.False(third.Started.IsCompleted);
        Assert.Equal(1, queue.Active);
    }

    /// <summary>Many runs entering and leaving at once from many threads: every one starts, and the books balance.</summary>
    [Fact]
    public async Task Under_load_every_run_starts_once_and_no_more_than_the_limit_run_together()
    {
        var queue = new RunQueue(maxActive: 3);
        var folders = new[] { A, B, C, A + "-d", A + "-e" };
        var going = 0;
        var most = 0;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            using var ticket = queue.Enqueue(folders[i % folders.Length]);
            await ticket.Started;
            var now = Interlocked.Increment(ref going);
            InterlockedMax(ref most, now);
            await Task.Yield();
            Interlocked.Decrement(ref going);
        })));

        Assert.InRange(most, 1, 3);
        Assert.Equal(0, queue.Active);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    [Fact]
    public void A_queue_with_no_slots_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RunQueue(0));
}

namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Schedules;
using Xunit;

/// <summary>
/// "Is a run of this schedule still going?", asked across processes.
///
/// <para>The interesting case is not the claim, it is the CRASH. A run that is killed leaves its
/// marker behind, and if the file alone meant "running" that schedule would be blocked for ever —
/// with no symptom except a task that quietly stopped happening, which is the hardest kind of
/// defect to attribute to anything.</para>
/// </summary>
public sealed class RunMarkerTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "enactive-markers", Guid.NewGuid().ToString("N"));

    private readonly RunMarkers _markers;

    public RunMarkerTests()
    {
        Directory.CreateDirectory(_folder);
        _markers = new RunMarkers(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
    }

    private string FileFor(Guid id) => Path.Combine(_folder, id.ToString("N") + ".json");

    [Fact]
    public void A_claim_holds_until_it_is_released()
    {
        var id = Guid.NewGuid();

        Assert.False(_markers.IsRunning(id));

        var claim = _markers.Claim(id);
        Assert.NotNull(claim);
        Assert.True(_markers.IsRunning(id));

        claim!.Dispose();
        Assert.False(_markers.IsRunning(id));
    }

    [Fact]
    public void A_second_claim_on_the_same_schedule_is_refused()
    {
        var id = Guid.NewGuid();
        using var first = _markers.Claim(id);

        Assert.Null(_markers.Claim(id));
    }

    [Fact]
    public void Claims_do_not_reach_across_schedules()
    {
        using var one = _markers.Claim(Guid.NewGuid());

        Assert.NotNull(_markers.Claim(Guid.NewGuid()));
    }

    /// <summary>
    /// The crash. A marker naming a process that is gone is not a live claim, and the file is
    /// cleaned up on the way — so a killed run costs one tick rather than every future one.
    /// </summary>
    [Fact]
    public void A_marker_left_by_a_process_that_is_gone_does_not_block_anything()
    {
        var id = Guid.NewGuid();

        // A process id that cannot be running: 0 is the idle process on Windows and is never a
        // match for a lookup by id.
        File.WriteAllText(FileFor(id), JsonSerializer.Serialize(
            new { ProcessId = 0, ProcessStartedAt = DateTimeOffset.UtcNow, ClaimedAt = DateTimeOffset.UtcNow }));

        Assert.False(_markers.IsRunning(id), "A marker from a dead process was believed.");
        Assert.NotNull(_markers.Claim(id));
        Assert.False(File.Exists(FileFor(id) + ".stale"), "no stray files");
    }

    /// <summary>
    /// The nastier version: the process id has been REUSED. The marker names a live process that is
    /// not the one that made it, and believing it would block a schedule permanently on the basis of
    /// what else the machine happened to start.
    /// </summary>
    [Fact]
    public void A_marker_whose_process_id_was_reused_by_something_else_does_not_block_anything()
    {
        var id = Guid.NewGuid();

        // This very process, with a start time that is not this process's. Same id, different
        // process - which is exactly what reuse looks like.
        File.WriteAllText(FileFor(id), JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId,
            ProcessStartedAt = DateTimeOffset.UtcNow.AddDays(-3),
            ClaimedAt = DateTimeOffset.UtcNow.AddDays(-3)
        }));

        Assert.False(_markers.IsRunning(id), "A marker was believed on its process id alone.");
    }

    /// <summary>An unreadable marker claims nothing, rather than blocking a schedule for ever.</summary>
    [Fact]
    public void A_corrupt_marker_does_not_block_anything()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(FileFor(id), "{ this is not json");

        Assert.False(_markers.IsRunning(id));
        Assert.NotNull(_markers.Claim(id));
    }

    /// <summary>Releasing twice is not an error: a finally block and a using are both right to try.</summary>
    [Fact]
    public void Releasing_twice_is_harmless()
    {
        var claim = _markers.Claim(Guid.NewGuid());

        claim!.Dispose();
        claim.Dispose();
    }

    // ---- the same questions, asked by several at once -------------------------------------------

    private const int Claimers = 8;
    private const int Rounds = 150;

    private static string DeadMarker() => JsonSerializer.Serialize(
        new { ProcessId = 0, ProcessStartedAt = DateTimeOffset.UtcNow, ClaimedAt = DateTimeOffset.UtcNow });

    private string LockFor(Guid id) => Path.Combine(_folder, id.ToString("N") + ".lock");

    /// <summary>
    /// Every claimer gets its own <see cref="RunMarkers"/> over the one folder, as separate
    /// processes would, and all of them are let go at the same instant, round after round. Returns
    /// what each got, per round.
    /// </summary>
    private IDisposable?[][] ClaimAtOnce(IReadOnlyList<Guid> ids)
    {
        var got = new IDisposable?[ids.Count][];
        for (var round = 0; round < ids.Count; round++)
            got[round] = new IDisposable?[Claimers];

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var barrier = new Barrier(Claimers);

        var threads = Enumerable.Range(0, Claimers).Select(claimer => new Thread(() =>
        {
            var markers = new RunMarkers(_folder);
            for (var round = 0; round < ids.Count; round++)
            {
                // A claimer that threw must still arrive here, or the others wait for it for ever.
                barrier.SignalAndWait();
                try { got[round][claimer] = markers.Claim(ids[round]); }
                catch (Exception ex) { failures.Enqueue(ex); }
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(failures);
        return got;
    }

    /// <summary>
    /// The overlap rule itself. Two ticks that both find a schedule free must not both run it, and
    /// the claim is the only thing standing between them.
    /// </summary>
    [Fact]
    public void Of_several_claims_made_at_once_exactly_one_holds()
    {
        var ids = Enumerable.Range(0, Rounds).Select(_ => Guid.NewGuid()).ToList();

        var got = ClaimAtOnce(ids);

        for (var round = 0; round < ids.Count; round++)
        {
            Assert.Equal(1, got[round].Count(c => c is not null));
            Assert.True(_markers.IsRunning(ids[round]));
        }

        foreach (var claim in got.SelectMany(g => g))
            claim?.Dispose();

        Assert.All(ids, id => Assert.False(_markers.IsRunning(id)));
    }

    /// <summary>
    /// Recovery is where a second owner would slip in: everybody sees the same dead marker, and
    /// "it is stale, so I may take it" is true for each of them until one has.
    /// </summary>
    [Fact]
    public void A_marker_left_by_a_process_that_is_gone_is_taken_over_by_exactly_one_of_several()
    {
        var ids = Enumerable.Range(0, Rounds).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            File.WriteAllText(FileFor(id), DeadMarker());

        var got = ClaimAtOnce(ids);

        for (var round = 0; round < ids.Count; round++)
        {
            Assert.Equal(1, got[round].Count(c => c is not null));
            Assert.True(_markers.IsRunning(ids[round]));
        }
    }

    /// <summary>
    /// A release is for one claim. If that claim's marker is gone and somebody else has claimed
    /// since, the late release must leave theirs alone - or the schedule reads as free while their
    /// run is still going.
    /// </summary>
    [Fact]
    public void A_release_whose_claim_was_superseded_does_not_clear_the_claim_that_replaced_it()
    {
        var id = Guid.NewGuid();
        var first = _markers.Claim(id);
        Assert.NotNull(first);

        // The first claim's marker is lost without its release having run.
        File.Delete(FileFor(id));

        var other = new RunMarkers(_folder);
        using var second = other.Claim(id);
        Assert.NotNull(second);

        first!.Dispose();

        Assert.True(_markers.IsRunning(id), "A late release cleared somebody else's claim.");
        Assert.Null(other.Claim(id));

        second!.Dispose();
        Assert.False(_markers.IsRunning(id));
    }

    /// <summary>
    /// A marker that does not read is not always rubbish: it may be a claim being written this very
    /// moment. The writer holds the schedule's lock while it writes, so that is how the two are told
    /// apart - and here the test is that writer, stopped half way.
    /// </summary>
    [Fact]
    public void A_marker_caught_half_written_is_neither_deleted_nor_claimed_over()
    {
        var id = Guid.NewGuid();

        // No waiting for the lock: the answer under test is the one given while it is held, and
        // the default patience would only make the test slow.
        var impatient = new RunMarkers(_folder, lockWait: TimeSpan.Zero);

        using (new FileStream(LockFor(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            File.WriteAllText(FileFor(id), "");

            Assert.True(impatient.IsRunning(id), "A claim in the middle of being written read as free.");
            Assert.Null(impatient.Claim(id));
            Assert.True(File.Exists(FileFor(id)), "A reader deleted a marker its writer had not finished.");
        }

        // The same file with no writer behind it is a leftover, and must not block the schedule.
        Assert.False(impatient.IsRunning(id));
        Assert.NotNull(impatient.Claim(id));
    }

    /// <summary>
    /// A release waits its turn like everybody else, and a release that could not get it must not
    /// delete on a guess: what it would delete may no longer be its own.
    /// </summary>
    [Fact]
    public void A_release_that_cannot_take_the_lock_deletes_nothing()
    {
        var id = Guid.NewGuid();
        var impatient = new RunMarkers(_folder, lockWait: TimeSpan.Zero);
        var claim = impatient.Claim(id);
        Assert.NotNull(claim);

        using (new FileStream(LockFor(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            claim!.Dispose();

        Assert.True(File.Exists(FileFor(id)));
    }
}

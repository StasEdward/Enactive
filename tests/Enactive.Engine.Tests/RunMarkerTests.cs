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
}

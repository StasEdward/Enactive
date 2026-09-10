namespace Enactive.Engine.Tests;

using Enactive.Core.Schedules;
using Xunit;

/// <summary>
/// Whether anything is actually asking the schedules what is due.
///
/// <para>Written after the scheduler did not run. Two schedules were created in the window on
/// 2026-09-10, saved correctly with the right times and zones, and nothing happened: no tick had
/// ever been registered on the machine, so nothing had ever asked them. Every rule this project had
/// built was correct and none of it was reachable, and the window said "Next run: ..." the whole
/// time.</para>
///
/// <para>So the product has to be able to answer "is anything waking these" — and it has to answer
/// it from the ASKING rather than from whether a Windows task exists. A registered task can be
/// disabled, can point at a binary that has moved, can fail on every fire; all three answer "yes,
/// registered" and none of them runs anything.</para>
/// </summary>
public sealed class HeartbeatTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), "enactive-tick", Guid.NewGuid().ToString("N"), "tick.txt");

    private static readonly TimeSpan Every5 = TimeSpan.FromMinutes(5);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true); } catch { /* temp */ }
    }

    // ── the rule ────────────────────────────────────────────────────────────

    /// <summary>
    /// Never ticked. This is the case that happened, and the words have to say the schedules will
    /// NOT run — not "the tick may not be configured", which reads as a detail to look at later.
    /// </summary>
    [Fact]
    public void A_machine_that_has_never_ticked_is_reported_as_not_running_anything()
    {
        var health = Heartbeat.Check(null, DateTimeOffset.Now, Every5);

        Assert.False(health.IsHealthy);
        Assert.Contains("will run", health.Words);
        Assert.Contains("Nothing is waking", health.Words);
    }

    /// <summary>
    /// And it says the schedules are kept. Somebody reading this has just set one up; "none of them
    /// will run" without "they are saved" invites deleting and re-creating them.
    /// </summary>
    [Fact]
    public void It_says_the_schedules_themselves_are_still_good()
    {
        Assert.Contains("saved", Heartbeat.Check(null, DateTimeOffset.Now, Every5).Words);
    }

    [Fact]
    public void A_tick_that_ran_a_moment_ago_is_healthy_and_says_nothing()
    {
        var now = DateTimeOffset.Now;
        var health = Heartbeat.Check(now.AddMinutes(-2), now, Every5);

        Assert.True(health.IsHealthy);
        Assert.Equal("", health.Words);
    }

    /// <summary>
    /// One missed wake-up is not a broken scheduler. An alarm that appears whenever a tick is a
    /// minute late is an alarm nobody reads by the second week.
    /// </summary>
    [Fact]
    public void One_missed_tick_is_not_reported()
    {
        var now = DateTimeOffset.Now;

        Assert.True(Heartbeat.Check(now.AddMinutes(-7), now, Every5).IsHealthy);
    }

    [Fact]
    public void Silence_past_the_allowance_is_reported_with_when_it_stopped()
    {
        var now = DateTimeOffset.Now;
        var stopped = now.AddHours(-3);

        var health = Heartbeat.Check(stopped, now, Every5);

        Assert.False(health.IsHealthy);
        Assert.Contains("not running", health.Words);
        Assert.Contains(stopped.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), health.Words);
    }

    /// <summary>The boundary is the allowance, not something near it.</summary>
    [Fact]
    public void The_allowance_is_three_intervals()
    {
        var now = DateTimeOffset.Now;

        Assert.True(Heartbeat.Check(now - Every5 * Heartbeat.MissesAllowed, now, Every5).IsHealthy);
        Assert.False(Heartbeat.Check(
            now - (Every5 * Heartbeat.MissesAllowed) - TimeSpan.FromSeconds(1), now, Every5).IsHealthy);
    }

    // ── the pulse itself ────────────────────────────────────────────────────

    [Fact]
    public void A_stamp_can_be_read_back()
    {
        var at = DateTimeOffset.Now.AddMinutes(-1);

        Heartbeat.Stamp(at, _file);

        Assert.Equal(at.ToUnixTimeSeconds(), Heartbeat.LastSeen(_file)?.ToUnixTimeSeconds());
    }

    /// <summary>
    /// No file is "never ticked", which is the case the whole thing exists for. It must not throw
    /// and it must not be confused with a tick at the epoch.
    /// </summary>
    [Fact]
    public void No_file_reads_as_never()
    {
        Assert.Null(Heartbeat.LastSeen(_file));
    }

    /// <summary>
    /// A damaged file is also "never" rather than an exception. This is read by a window drawing a
    /// list; taking the window down over an unreadable timestamp would be a worse failure than the
    /// one being reported.
    /// </summary>
    [Fact]
    public void An_unreadable_file_reads_as_never()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "not a time");

        Assert.Null(Heartbeat.LastSeen(_file));
    }

    /// <summary>Each tick replaces the last: this is a pulse, not a log.</summary>
    [Fact]
    public void The_newest_stamp_wins()
    {
        var earlier = DateTimeOffset.Now.AddHours(-1);
        var later = DateTimeOffset.Now;

        Heartbeat.Stamp(earlier, _file);
        Heartbeat.Stamp(later, _file);

        Assert.Equal(later.ToUnixTimeSeconds(), Heartbeat.LastSeen(_file)?.ToUnixTimeSeconds());
    }
}

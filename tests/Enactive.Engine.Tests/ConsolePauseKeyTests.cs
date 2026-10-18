namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// The console's "Pause after this step": P, pressed while a run runs (Ctrl+C stays Stop). Read here without a terminal -
/// what a key does and the one line it prints; the console's loop only reads the key and prints the line.
/// </summary>
public sealed class ConsolePauseKeyTests
{
    private static PauseOffer Offered(RunPause pause)
    {
        var offer = new PauseOffer(pause);
        offer.Apply(new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, EventKind.PlanCreated,
            "Report — 2 steps", WorkEventPayload.PlanPayload("Report", ["gather", "write"])));
        return offer;
    }

    [Theory]
    [InlineData('p')]
    [InlineData('P')]
    public void P_asks_the_run_to_pause_and_says_so_in_one_line(char key)
    {
        var pause = new RunPause();

        var said = ConsolePauseKey.Press(key, Offered(pause));

        Assert.True(pause.IsRequested);
        Assert.Equal("  >> Pausing after the steps running now… Ctrl+C still stops the run at once.", said);
    }

    [Fact]
    public void Any_other_key_does_nothing_and_says_nothing()
    {
        var pause = new RunPause();

        Assert.Null(ConsolePauseKey.Press('x', Offered(pause)));
        Assert.Null(ConsolePauseKey.Press('\r', Offered(pause)));

        Assert.False(pause.IsRequested);
    }

    [Fact]
    public void Pressed_again_it_says_the_pause_is_already_asked_for()
    {
        var offer = Offered(new RunPause());
        ConsolePauseKey.Press('p', offer);

        Assert.Equal("  >> The pause is already asked for: the steps running now finish first.", ConsolePauseKey.Press('p', offer));
    }

    /// <summary>Before the plan there is no step boundary: said, and nothing asked - the person can press it again later.</summary>
    [Fact]
    public void Before_the_plan_it_says_why_not()
    {
        var pause = new RunPause();

        var said = ConsolePauseKey.Press('p', new PauseOffer(pause));

        Assert.StartsWith("  >> Not paused: ", said);
        Assert.Contains("no plan yet", said);
        Assert.False(pause.IsRequested);
    }

    /// <summary>The banner says the key is there; a key nobody is told about is not a control.</summary>
    [Fact]
    public void The_banner_names_the_key_and_Stop()
        => Assert.Equal("P pauses the run after the steps running now (then --resume carries it on); Ctrl+C stops it at once",
            ConsolePauseKey.Banner);

    /// <summary>
    /// What "--resume" says of the run it carries on: one paused at a step boundary was paused, not stopped - "stopped"
    /// reads as something that went wrong, for a run somebody paused on purpose.
    /// </summary>
    [Theory]
    [InlineData(true, "paused")]
    [InlineData(false, "stopped")]
    public void Resuming_says_paused_for_a_paused_run_and_stopped_for_one_that_stopped(bool paused, string word)
    {
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now,
            new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero), "count the books", "books", null, null,
            [Step("Count the invoices", "Done"), Step("Write the summary", "Pending")], [], [], [], 1, 0) { Paused = paused };

        Assert.Equal($"Resuming a run {word} on {checkpoint.At.ToLocalTime():yyyy-MM-dd HH:mm}: 1 of 2 step(s) were done.",
            ConsolePauseKey.Resuming(checkpoint));
    }

    private static CheckpointStep Step(string title, string status) => new(Guid.NewGuid(), title, [], "Normal", status);
}

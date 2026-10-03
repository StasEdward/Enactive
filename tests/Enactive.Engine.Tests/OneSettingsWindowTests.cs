namespace Enactive.Engine.Tests;

using Enactive.App.Ui;
using Xunit;

/// <summary>
/// There is one Settings window at a time.
///
/// <para><b>Why.</b> Every press of Settings used to open another window, each holding its own copy
/// of all the settings taken when it opened. Save writes that whole copy over the file and the live
/// settings, so a window left open in the background rolled back whatever another had saved since -
/// silently, including settings neither window had been showing.</para>
///
/// <para>The window itself is Avalonia and cannot be built here; what is under test is the decision
/// the main window asks for - open, or bring forward - with a plain object standing in for it.</para>
/// </summary>
public sealed class OneSettingsWindowTests
{
    private sealed class Window;

    [Fact]
    public void A_second_request_brings_the_open_window_forward_instead_of_opening_another()
    {
        var slot = new OneAtATime<Window>();
        var opened = new List<Window>();
        var broughtForward = new List<Window>();

        slot.Request(() => Open(opened), broughtForward.Add);
        slot.Request(() => Open(opened), broughtForward.Add);
        slot.Request(() => Open(opened), broughtForward.Add);

        var only = Assert.Single(opened);
        Assert.Equal(new[] { only, only }, broughtForward);
    }

    [Fact]
    public void The_first_request_opens_and_brings_nothing_forward()
    {
        var slot = new OneAtATime<Window>();
        var opened = new List<Window>();
        var broughtForward = new List<Window>();

        slot.Request(() => Open(opened), broughtForward.Add);

        Assert.Single(opened);
        Assert.Empty(broughtForward);
    }

    /// <summary>A fresh one: the settings it copies are read when it opens, not when the last one did.</summary>
    [Fact]
    public void After_the_window_closes_the_next_request_opens_a_fresh_one()
    {
        var slot = new OneAtATime<Window>();
        var opened = new List<Window>();
        var broughtForward = new List<Window>();

        slot.Request(() => Open(opened), broughtForward.Add);
        slot.Closed(opened[0]);
        slot.Request(() => Open(opened), broughtForward.Add);

        Assert.Equal(2, opened.Count);
        Assert.NotSame(opened[0], opened[1]);
        Assert.Empty(broughtForward);
    }

    [Fact]
    public void A_close_reported_for_a_window_already_replaced_does_not_let_a_second_one_open()
    {
        var slot = new OneAtATime<Window>();
        var opened = new List<Window>();
        var broughtForward = new List<Window>();

        slot.Request(() => Open(opened), broughtForward.Add);
        slot.Closed(opened[0]);
        slot.Request(() => Open(opened), broughtForward.Add);
        slot.Closed(opened[0]);
        slot.Request(() => Open(opened), broughtForward.Add);

        Assert.Equal(2, opened.Count);
        Assert.Equal(new[] { opened[1] }, broughtForward);
    }

    /// <summary>A window that could not be built is not one that is open.</summary>
    [Fact]
    public void A_window_that_failed_to_open_does_not_block_the_next_request()
    {
        var slot = new OneAtATime<Window>();
        var opened = new List<Window>();

        Assert.Throws<InvalidOperationException>(
            () => slot.Request(() => throw new InvalidOperationException("no window"), _ => { }));
        slot.Request(() => Open(opened), _ => Assert.Fail("nothing is open to bring forward"));

        Assert.Single(opened);
    }

    private static Window Open(List<Window> opened)
    {
        var window = new Window();
        opened.Add(window);
        return window;
    }
}

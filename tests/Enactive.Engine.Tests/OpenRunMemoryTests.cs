namespace Enactive.Engine.Tests;

using Enactive.Core.History;
using Xunit;

/// <summary>
/// What the middle column shows after a workspace switch, and after a start.
///
/// <para>Switching workspace re-scoped the run list and nothing else: the execution feed and the
/// tiles beside it went on showing the previous workspace's run, under the new workspace's name.
/// These tests are about the rule that replaced it - the middle shows what you left open in THIS
/// workspace, or nothing - and the first of them is the defect itself.</para>
/// </summary>
public sealed class OpenRunMemoryTests
{
    private const string Alpha = @"C:\work\alpha";
    private const string Beta = @"C:\work\beta";

    /// <summary>
    /// The decisive one. A run opened in one workspace is not what another workspace comes back
    /// showing - not the same run under a different name, and not anything else either.
    /// </summary>
    [Fact]
    public void A_run_opened_in_one_workspace_is_not_shown_in_another()
    {
        var memory = new OpenRunMemory();
        memory.Leaving(Alpha, Guid.NewGuid());

        Assert.Null(memory.Entering(Beta));
    }

    /// <summary>
    /// And coming back to where you were reading returns you to what you were reading. Without
    /// this the rule is just "clear everything", which loses the thing that made the second option
    /// tempting.
    /// </summary>
    [Fact]
    public void A_workspace_comes_back_showing_what_it_was_left_showing()
    {
        var memory = new OpenRunMemory();
        var run = Guid.NewGuid();

        memory.Leaving(Alpha, run);
        memory.Leaving(Beta, Guid.NewGuid());

        Assert.Equal(run, memory.Entering(Alpha));
    }

    /// <summary>
    /// Pressing Back is a decision, and it survives the switch. A memory that only ever recorded
    /// openings would reopen a run the person had explicitly closed.
    /// </summary>
    [Fact]
    public void A_workspace_left_showing_nothing_comes_back_showing_nothing()
    {
        var memory = new OpenRunMemory();

        memory.Leaving(Alpha, Guid.NewGuid());
        memory.Leaving(Alpha, null);

        Assert.Null(memory.Entering(Alpha));
    }

    /// <summary>
    /// A start. Nothing has been opened anywhere, so the middle is empty and the app opens on the
    /// command bar rather than on somebody's finished run - which is the case that needs no code
    /// of its own, and the reason this shape was chosen.
    /// </summary>
    [Fact]
    public void A_workspace_nobody_has_opened_a_run_in_shows_nothing()
        => Assert.Null(new OpenRunMemory().Entering(Alpha));

    /// <summary>
    /// A deleted run is not something to come back to. Left remembered, the workspace would point
    /// at a record that is gone and come back empty anyway - after a store read that can only fail.
    /// </summary>
    [Fact]
    public void A_forgotten_run_is_not_shown_again()
    {
        var memory = new OpenRunMemory();
        var run = Guid.NewGuid();

        memory.Leaving(Alpha, run);
        memory.Forget(run);

        Assert.Null(memory.Entering(Alpha));
    }

    /// <summary>
    /// One folder spelled two ways is one workspace. Everything else in the app normalises paths;
    /// a memory that did not would answer null for a workspace the person was just in, and the
    /// middle would clear for no reason they could see.
    /// </summary>
    [Fact]
    public void One_folder_spelled_two_ways_is_one_workspace()
    {
        var memory = new OpenRunMemory();
        var run = Guid.NewGuid();

        memory.Leaving(@"C:\Work\Alpha", run);

        Assert.Equal(run, memory.Entering(@"c:\work\alpha"));
    }
}

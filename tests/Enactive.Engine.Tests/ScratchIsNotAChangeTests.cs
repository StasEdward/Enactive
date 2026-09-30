namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// "It changed the workspace" is asked in several places, and the scratch area made them stop meaning the same thing.
/// The stall guard asks "did this step get anywhere" - and for it a scratch write really is progress.
/// </summary>
public sealed class ScratchIsNotAChangeTests
{
    /// <summary>
    /// And the other question keeps its own answer: the stall guard counts a scratch write as
    /// progress, because the step did something it had not done before.
    /// </summary>
    [Fact]
    public void The_stall_guard_still_counts_a_scratch_write_as_progress()
    {
        Assert.Equal(ProgressIdentity.Action, new Enactive.Tools.WriteFileTool().Definition.ProgressIdentity);
    }
}

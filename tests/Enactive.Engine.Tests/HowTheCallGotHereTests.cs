namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// How a tool call reached the engine, recorded beside what it did.
///
/// <para>The engine refuses malformed calls: a turn with one unparseable call executes nothing, and
/// raw fragments are deliberately neither repaired nor replayed. Other engines repair instead, and
/// re-ask the model when repair fails. Both are defensible, and the argument between them has been
/// conducted on preference, because nothing in this project counted how often a model needs either.
/// </para>
///
/// <para>So the journal records the origin. Nothing about what runs changes. What changes is that
/// "this model cannot emit structured calls" and "this model emits them fine" become a number
/// somebody can read, per model, out of runs that already happened.</para>
/// </summary>
public sealed class HowTheCallGotHereTests
{
    /// <summary>
    /// The default has to be the honest one. Every existing call site records a call the provider
    /// returned as a structured call, and a default of anything else would invent a repair history
    /// for runs that never had one.
    /// </summary>
    [Fact]
    public void A_call_recorded_without_an_origin_is_a_native_one()
    {
        var journal = new ExecutionJournal();

        journal.Record(1, "read_file", """{"path":"a.txt"}""", ActionOutcome.Succeeded, "ok");

        Assert.Equal(ToolCallOrigin.Native, Assert.Single(journal.Actions).Origin);
    }

    [Theory]
    [InlineData(ToolCallOrigin.Healed)]
    [InlineData(ToolCallOrigin.Nudged)]
    [InlineData(ToolCallOrigin.Retry)]
    public void The_origin_a_call_was_recorded_with_is_the_origin_it_keeps(ToolCallOrigin origin)
    {
        var journal = new ExecutionJournal();

        journal.Record(1, "read_file", """{"path":"a.txt"}""", ActionOutcome.Succeeded, "ok", origin: origin);

        Assert.Equal(origin, Assert.Single(journal.Actions).Origin);
    }

    /// <summary>
    /// The tally counts refused calls too. A call the engine had to ask for twice and then refused
    /// anyway is the case this exists to make visible; dropping it would flatter the model.
    /// </summary>
    [Fact]
    public void The_tally_counts_every_call_including_the_ones_that_never_ran()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", "{}", ActionOutcome.Succeeded, "ok");
        journal.Record(1, "read_file", "{}", ActionOutcome.Succeeded, "ok");
        journal.Record(1, "write_file", "{}", ActionOutcome.Refused, "no", origin: ToolCallOrigin.Nudged);
        journal.Record(1, "write_file", "{}", ActionOutcome.Failed, "threw", origin: ToolCallOrigin.Healed);

        var tally = journal.OriginTally();

        Assert.Equal(2, tally[ToolCallOrigin.Native]);
        Assert.Equal(1, tally[ToolCallOrigin.Nudged]);
        Assert.Equal(1, tally[ToolCallOrigin.Healed]);
        Assert.False(tally.ContainsKey(ToolCallOrigin.Retry));
        Assert.Equal(journal.Actions.Count, tally.Values.Sum());
    }

    /// <summary>
    /// Origin is not evidence and must not leak into what the reviewer reads. A reviewer told that a
    /// call was "healed" would have a reason to discount the work, and the work is the same work
    /// whichever way the call arrived.
    /// </summary>
    [Fact]
    public void The_origin_does_not_appear_in_the_evidence_a_reviewer_is_shown()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", """{"path":"a.txt"}""", ActionOutcome.Succeeded, "ok",
            origin: ToolCallOrigin.Healed);

        var shown = journal.Describe(0, ExecutionJournal.DefaultBudget).Text;

        Assert.DoesNotContain("Healed", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("origin", shown, StringComparison.OrdinalIgnoreCase);
    }
}

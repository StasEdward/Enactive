namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// The one line a person reads to decide whether a step is worth opening.
///
/// <para>These could not exist before. The sentence was a private method on a view model in
/// <c>Enactive.App.Ui</c> — a WinExe no test project references — so the summary of every step in
/// the product was the one thing about a step nothing checked.</para>
///
/// <para>What it got wrong is the first test here. A scheduled run on 2026-09-11 was stopped six
/// times by the permission policy and reported <c>"14 notes"</c>: the vocabulary had words for what
/// happened and one bucket for prose, and a refused call is neither.</para>
/// </summary>
public sealed class StepTallyTests
{
    [Fact]
    public void A_step_that_was_only_ever_refused_says_so()
    {
        // Exactly the shape of the run that found this: six refusals, thirteen remarks around them,
        // and not one tool that ran.
        var words = new StepTally(Tools: 0, Commands: 0, Files: 0, Refused: 6, Notes: 14).Words();

        Assert.Contains("6 calls refused", words);
    }

    /// <summary>
    /// And it leads with it. A refusal count behind a note count is the failure being fixed: the
    /// number a person needs is the one they see without expanding anything.
    /// </summary>
    [Fact]
    public void The_refusals_come_first_when_nothing_else_happened()
    {
        Assert.StartsWith("6 calls refused", new StepTally(0, 0, 0, 6, 14).Words());
    }

    /// <summary>
    /// A refusal is NOT a tool used. It never ran, and folding it into the tool count would be the
    /// opposite lie to the one this replaces — a step that was stopped reading as a step that
    /// worked.
    ///
    /// <para>Two tools and three refusals, asserted as the whole line rather than as an absence:
    /// "does not contain 'tool'" is satisfied by an empty string, so it would have passed on a
    /// version with no refusal word at all — green for the wrong reason, which is what the
    /// differential caught.</para>
    /// </summary>
    [Fact]
    public void A_refusal_does_not_inflate_the_tool_count()
    {
        Assert.Equal("Used 2 tools, 3 refused",
                     new StepTally(Tools: 2, Commands: 0, Files: 0, Refused: 3, Notes: 0).Words());
    }

    [Fact]
    public void What_ran_comes_before_what_was_stopped()
    {
        var words = new StepTally(Tools: 4, Commands: 1, Files: 2, Refused: 1, Notes: 0).Words();

        Assert.Equal("Used 4 tools, ran 1 command, edited 2 files, 1 refused", words);
    }

    /// <summary>
    /// The line a run with nothing refused produces is byte-for-byte what it produced before this
    /// change. A new word must not reword the common case.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, 0, "Used 1 tool")]
    [InlineData(2, 0, 0, 0, "Used 2 tools")]
    [InlineData(3, 1, 0, 0, "Used 3 tools, ran 1 command")]
    [InlineData(3, 1, 2, 0, "Used 3 tools, ran 1 command, edited 2 files")]
    [InlineData(2, 0, 0, 4, "Used 2 tools · 4 notes")]
    [InlineData(0, 0, 0, 1, "1 note")]
    public void An_ordinary_step_reads_as_it_always_did(
        int tools, int commands, int files, int notes, string expected)
    {
        Assert.Equal(expected, new StepTally(tools, commands, files, Refused: 0, Notes: notes).Words());
    }

    /// <summary>A step that did nothing gets no line at all — the disclosure row is hidden, and a
    /// label under it saying "0 notes" would be a row with nothing in it.</summary>
    [Fact]
    public void A_step_with_nothing_in_it_has_no_summary()
    {
        Assert.Equal("", new StepTally(0, 0, 0, 0, 0).Words());
    }

    [Fact]
    public void One_refusal_is_singular()
    {
        Assert.Equal("1 call refused", new StepTally(0, 0, 0, 1, 0).Words());
    }
}

/// <summary>
/// Whether a resolved decision let the call through, as a VALUE.
///
/// <para>The event carried only a sentence, and the orchestrator writes four of them for the same
/// fact — <c>"git: denied"</c>, <c>"git: blocked by policy"</c>, <c>"git: not available to role
/// 'writer'"</c>, <c>"run_command: kept to the workspace"</c>. Anything that wanted to know whether
/// the call happened had to match on the words, so nothing did, and every refusal reached the step
/// card as a remark.</para>
/// </summary>
public sealed class DecisionPayloadTests
{
    [Fact]
    public void A_refused_call_reads_back_as_refused()
    {
        Assert.True(WorkEventPayload.WasRefusedIn(
            WorkEventPayload.DecisionPayload(1, "git", allowed: false)));
    }

    [Fact]
    public void An_allowed_call_reads_back_as_not_refused()
    {
        Assert.False(WorkEventPayload.WasRefusedIn(
            WorkEventPayload.DecisionPayload(1, "git", allowed: true)));
    }

    /// <summary>
    /// The step number rides along, or the refusal lands on whichever card happened to be current —
    /// the same attribution every other event gets, and the reason replay never guesses from order.
    /// </summary>
    [Fact]
    public void The_decision_carries_its_step()
    {
        var payload = WorkEventPayload.DecisionPayload(3, "run_command", allowed: false);
        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                               EventKind.DecisionResolved, "run_command: denied", payload);

        Assert.Equal(3, ev.StepNo());
        Assert.True(ev.WasRefused());
    }

    /// <summary>
    /// Null, not false. A run recorded before this payload existed did not say the call was allowed
    /// — it said nothing, and a reader that turns silence into "allowed" would quietly report every
    /// old refusal as work that happened.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"step":1}""")]
    [InlineData("not json")]
    public void A_payload_with_no_decision_in_it_says_nothing(string? payload)
    {
        Assert.Null(WorkEventPayload.WasRefusedIn(payload));
    }

    /// <summary>A tool name with a quote or a backslash in it must not break the payload it is
    /// written into — the reason every value here goes through the same quoting.</summary>
    [Fact]
    public void An_awkward_tool_name_survives_the_round_trip()
    {
        var payload = WorkEventPayload.DecisionPayload(null, """we"ird\tool""", allowed: false);

        Assert.True(WorkEventPayload.WasRefusedIn(payload));
    }
}

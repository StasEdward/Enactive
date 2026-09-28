namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// An event payload is valid JSON whatever its reason says.
///
/// <para>Payloads are written by hand, and their string escaping handled only the backslash and the
/// quote. A reason with a line break in it - "Combined review response has structural errors:" and
/// then a list, the most common reason a step did not finish - therefore made the whole payload
/// invalid. Every reader failed to parse it and got null: the step's outcome, its reason, all of it.
/// The window then fell back to searching the summary for "FAILED:", found none in "INCOMPLETE:", and
/// painted the step's card green, "Done". Run 4b3b7457 on 2026-09-27 ended its second step so.</para>
/// </summary>
public sealed class APayloadWithALineBreakTests
{
    private static WorkEvent Ev(string payload)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, EventKind.StepCompleted, "s", payload);

    [Fact]
    public void A_multi_line_reason_keeps_the_payload_valid_and_comes_back_whole()
    {
        const string reason = "Combined review response has structural errors:\n- $.claims[0].shown: expected one of\tyes\r\n- \"quoted\" \\ and a bell\a";

        var payload = WorkEventPayload.StepPayload(2, StepOutcomeKind.Incomplete, reason);

        using (JsonDocument.Parse(payload)) { }   // throws on the old escaping
        var ev = Ev(payload);
        Assert.Equal(StepOutcomeKind.Incomplete, ev.StepOutcome());
        Assert.Equal(reason, ev.OutcomeReason());
    }

    [Fact]
    public void Text_that_is_not_a_control_character_is_left_as_written()
    {
        var payload = WorkEventPayload.StepPayload(1, StepOutcomeKind.DoneUnverified, "ревьюер не вернул вердикт");

        Assert.Contains("ревьюер не вернул вердикт", payload, StringComparison.Ordinal);
        Assert.Equal("ревьюер не вернул вердикт", Ev(payload).OutcomeReason());
    }

    /// <summary>
    /// The fields read by pattern rather than by parser decode exactly what Quote encoded - and a
    /// payload written before Quote escaped control characters still reads as it always did.
    /// </summary>
    [Fact]
    public void Pattern_read_fields_invert_the_quoting_and_old_payloads_still_read()
    {
        var model = "odd\"model\\name\nwith a break";
        var usage = Ev(WorkEventPayload.UsagePayload(1, 2, null, "provider-x", model));

        Assert.Equal("provider-x", usage.ProviderId());
        Assert.Equal(model, usage.ModelName());

        // Recorded by an earlier build: a raw line break inside the string, which JSON forbids.
        var legacy = Ev("{\"provider\":\"old\nprovider\"}");
        Assert.Equal("old\nprovider", legacy.ProviderId());
    }
}

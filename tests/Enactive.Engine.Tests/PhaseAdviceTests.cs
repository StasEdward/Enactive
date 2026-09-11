namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Guidance;
using Xunit;

/// <summary>
/// The advice behind the "?" beside each phase in AI · Phases.
///
/// <para>Four identical drop-downs over the same list of models, and nothing anywhere said which of
/// YOUR models belongs in which. The paragraphs already on that screen explain how the phases work,
/// which is a different question from the one somebody is actually stuck on.</para>
///
/// <para>Tested at all only because the text lives in Core. In <c>Enactive.App.Ui</c> it would be a
/// string in a .axaml inside a WinExe no test project references — which is exactly how the screen
/// came to have two paragraphs of mechanics and no answer.</para>
/// </summary>
public sealed class PhaseAdviceTests
{
    /// <summary>
    /// The load-bearing one. Plan and Review are keyed on the SAME constants the usage events use,
    /// so renaming a phase in the engine cannot quietly orphan the advice for it — a hint that
    /// silently stops appearing is worse than no hint, because nobody notices.
    /// </summary>
    [Fact]
    public void The_phases_advised_on_are_the_engine_s_own_phase_names()
    {
        Assert.Equal(PhaseAdvice.Plan, PhaseAdvice.For(WorkEventPayload.WorkPurpose.Plan));
        Assert.Equal(PhaseAdvice.Review, PhaseAdvice.For(WorkEventPayload.WorkPurpose.Review));
    }

    /// <summary>
    /// Every phase in the list has something to say. An entry added to Phases without text would
    /// put a question mark on screen that opens an empty panel.
    /// </summary>
    [Fact]
    public void Every_phase_has_advice()
    {
        foreach (var phase in PhaseAdvice.Phases)
            Assert.False(string.IsNullOrWhiteSpace(PhaseAdvice.For(phase)), phase);
    }

    /// <summary>
    /// And a phase nobody wrote for returns null rather than an empty string, so the control can
    /// leave the icon out entirely. A "?" that answers nothing invites a click and teaches the
    /// reader to distrust the other three.
    /// </summary>
    [Fact]
    public void A_phase_with_nothing_written_for_it_says_so()
    {
        Assert.Null(PhaseAdvice.For(WorkEventPayload.WorkPurpose.Execute));
        Assert.Null(PhaseAdvice.For("nonsense"));
    }

    /// <summary>
    /// Every one of the four drop-downs offers (none), and for three of them it is a real choice
    /// rather than an omission — no planner, no reviewer, no routing. Advice that ignores the
    /// option sitting in the list it is advising about has skipped the decision the person is
    /// actually making.
    /// </summary>
    [Fact]
    public void Each_one_says_what_choosing_none_means()
    {
        foreach (var phase in PhaseAdvice.Phases)
            Assert.Contains("(none)", PhaseAdvice.For(phase)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Short enough to read standing up.
    ///
    /// <para>This is the way the feature actually fails: somebody pastes in the paragraph from
    /// FIX_PLAN that explains the whole mechanism, and the flyout becomes a page nobody finishes.
    /// A person clicking a question mark beside a drop-down has already decided to spend about
    /// fifteen seconds. The cap is generous — it catches an essay, not a careful paragraph.</para>
    /// </summary>
    [Fact]
    public void None_of_it_is_an_essay()
    {
        foreach (var phase in PhaseAdvice.Phases)
        {
            var text = PhaseAdvice.For(phase)!;
            Assert.True(text.Length < 1200,
                $"'{phase}' advice is {text.Length} characters. A flyout that long is a page, and "
                + "a page beside a drop-down is read by nobody.");
        }
    }

    /// <summary>
    /// It advises; it does not pretend to enforce. Nothing in the engine checks which model is
    /// bound to which phase, and wording that reads like a rule would describe a guard that is not
    /// there — the failure this codebase names most often.
    /// </summary>
    [Fact]
    public void It_reads_as_advice_and_not_as_a_rule()
    {
        foreach (var phase in PhaseAdvice.Phases)
        {
            var text = PhaseAdvice.For(phase)!;

            foreach (var forbidden in new[] { "you must", "not allowed", "is required", "will be rejected" })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

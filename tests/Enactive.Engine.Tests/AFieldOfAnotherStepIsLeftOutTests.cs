namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Runs 56467a and ac439e, 2026-10-03: one hand-over tool is offered to every step of a run, listing the fields
/// of all of them, and a local model filled the ones that belong to later steps as well as its own - "report"
/// and "sent" beside "diskData". The whole submission was refused for it, the model sent it again without them,
/// and each run paid a quarter of a minute for a field nobody asked for. Deliberately not disks: invoices.
/// </summary>
public sealed class AFieldOfAnotherStepIsLeftOutTests
{
    private static readonly StepOutputSchema Totals = new("s1", 1,
        [new StepOutputField("totals", StepOutputFieldType.Text, "the sum per customer")]);
    private static readonly StepOutputSchema Summary = new("s2", 1,
        [new StepOutputField("summary", StepOutputFieldType.Path, "the summary file")]);
    private static readonly StepOutputSchema Posted = new("s3", 1,
        [new StepOutputField("posted", StepOutputFieldType.Boolean, "whether it was posted")]);

    private static StepOutputContract.Verdict Check(string arguments, bool offered = true)
        => StepOutputContract.Check(Totals, arguments, _ => true, _ => true,
            offered: offered ? StepOutputContract.RunTool([Totals, Summary, Posted]) : null);

    [Fact]
    public void A_submission_that_also_fills_a_later_steps_fields_is_accepted_without_them()
    {
        var verdict = Check("""{"totals":"ACME 120; Borg 80","summary":"","posted":false}""");

        Assert.True(verdict.Accepted, string.Join(" ", verdict.Errors));
        Assert.Equal(["totals"], verdict.Values!.Select(v => v.Key));
    }

    [Fact]
    public void The_model_is_told_what_was_left_out_and_why()
    {
        var note = Assert.Single(Check("""{"totals":"ACME 120","summary":"notes.md"}""").Notes);

        Assert.Contains("'summary'", note, StringComparison.Ordinal);
        Assert.Contains("another step", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_no_step_of_the_run_hands_on_is_still_refused()
    {
        var verdict = Check("""{"totals":"ACME 120","total":"ACME 120"}""");

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Errors, e => e.Contains("'total'", StringComparison.Ordinal));
    }

    [Fact]
    public void Its_own_required_field_is_still_required()
    {
        // The result sent under a later step's name is not this step's result.
        var verdict = Check("""{"summary":"ACME 120; Borg 80"}""");

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Errors, e => e.StartsWith("Missing: totals", StringComparison.Ordinal));
    }

    [Fact]
    public void Where_the_step_has_a_tool_of_its_own_nothing_else_is_taken()
    {
        var verdict = Check("""{"totals":"ACME 120","summary":""}""", offered: false);

        Assert.False(verdict.Accepted);
    }
}

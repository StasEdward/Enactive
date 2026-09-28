namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// What the combined review refused on 2026-09-28 that it did not have to - each a way of NAMING
/// something, read the one way it can be read - and what it still refuses, because there it is
/// the reviewer's to say. The real answers are in review-corpus; these are the same shapes, small.
/// </summary>
public sealed class ReviewBookkeepingTests
{
    // ── the call a claim cites ───────────────────────────────────────────────────────────

    /// <summary>
    /// The prompt called call numbers "evidence IDs" - the name of the line labels printed in front
    /// of every evidence line - and the final review cited the labels: 130, 134, for call 55.
    /// </summary>
    [Fact]
    public void The_reviewer_is_told_a_call_is_cited_by_its_number_not_by_a_line_label()
    {
        Assert.DoesNotContain("original evidence IDs", Reviewer.ExecutionGuidance, StringComparison.Ordinal);
        Assert.Contains("never by an [evidence N] line label", Reviewer.ExecutionGuidance, StringComparison.Ordinal);
        Assert.Contains("the [n] that begins the call", Reviewer.ProofGuidance, StringComparison.Ordinal);
    }

    // ── the scope a claim is in ──────────────────────────────────────────────────────────

    private static Plan Plan() => DagPlan.FromSpecs([
        new("Implement", [], DependenciesDeclared: true) { ObligationIds = ["O001"] },
        new("Verify", [0], DependenciesDeclared: true) { ObligationIds = ["O001"] }
    ]);

    private static List<string> Review(Turn answer)
    {
        var obligations = RequestObligations.ForPlan("Implement and verify", Plan()).AtStep(2);
        var journal = new ExecutionJournal();
        journal.Record(2, "run_command", "tests", ActionOutcome.Succeeded, "passed");
        return ReviewCorpus.Validate(ReviewCorpus.Prepare(answer.Text!, obligations), obligations,
            journal.Describe(), new ReviewSources("done"));
    }

    /// <summary>
    /// The prompt shows the plan's requirement-to-step map as lists, and the reviewer copied one into
    /// a claim: "S2,S3" at step 3. A list of declared steps that includes this one, on a positive
    /// claim, says what this step's scope says.
    /// </summary>
    [Theory]
    [InlineData("S2")]
    [InlineData("S1,S2")]
    [InlineData("S1, S2")]
    public void A_positive_claim_scoped_to_the_plans_own_list_of_steps_is_this_steps_claim(string scope)
        => Assert.Empty(Review(Verdicts.Combined(Verdicts.Shown("the tests passed", 1), scope, "O001")));

    /// <summary>
    /// Where the list could mean something else, it is not read at all: a "no" may mean "left to the
    /// other steps", a list without this step is about other steps, and an undeclared step is invented.
    /// </summary>
    [Theory]
    [InlineData("S1,S2", "no")]
    [InlineData("S1,S3", "yes")]
    public void A_list_that_could_mean_something_else_is_still_refused(string scope, string shown)
    {
        var errors = Review(shown == "no"
            ? Verdicts.Combined(Verdicts.NotShown("not run"), scope, "O001")
            : Verdicts.Combined(Verdicts.Shown("the tests passed", 1), scope, "O001"));
        Assert.Contains(errors, e => e.Contains("unknown scope", StringComparison.Ordinal));
    }

    // ── the finding a repair corrects ────────────────────────────────────────────────────

    private static JsonNode Failing(int requirements = 1)
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content checked"), "run").Text!)!;
        var claim = answer["claims"]![0]!;
        var parts = claim["requirements"]!.AsArray();
        parts[0]!["verification"]!["verdict"] = "fail";
        for (var i = 1; i < requirements; i++) parts.Add(parts[0]!.DeepClone());
        answer["verdict"] = "fail";
        return answer;
    }

    private static IReadOnlyList<string> Errors(JsonNode answer, params JsonObject[] repairs)
    {
        answer["repairs"] = new JsonArray(repairs);
        return ReviewRepairContract.Errors(answer.ToJsonString(), new ReviewSources("done"),
            RequestObligations.Create("Write one record"));
    }

    /// <summary>
    /// The final review named <c>$.claims[1]</c> and <c>$.claims[1].requirements[0]</c> where the
    /// guidance asks for <c>...requirements[0].verification</c>. With one failed verdict inside,
    /// there is only one thing either can mean.
    /// </summary>
    [Theory]
    [InlineData("$.claims[0]")]
    [InlineData("$.claims[0].requirements[0]")]
    [InlineData("$.claims[0].requirements[0].verification")]
    public void A_repair_may_name_its_finding_by_what_holds_it(string finding)
        => Assert.Empty(Errors(Failing(), Verdicts.Repair(finding, "Wrong count", "Count the records")));

    /// <summary>With two failed verdicts inside, a repair naming the container could be meant for either, so it is not taken to cover both.</summary>
    [Fact]
    public void A_container_holding_several_failed_findings_is_not_resolved()
    {
        var errors = Errors(Failing(requirements: 2), Verdicts.Repair("$.claims[0]", "Wrong count", "Count the records"));
        Assert.Contains(errors, e => e.Contains("$.claims[0] is not a failed finding", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("fail has no concrete repair", StringComparison.Ordinal));
    }

    /// <summary>One requirement can need two corrections - "revert the source file" and "prove each test by breaking it" - and they are not a duplicate. The same correction twice is.</summary>
    [Fact]
    public void Two_different_corrections_of_one_finding_are_not_a_duplicate_and_the_same_one_twice_is()
    {
        const string finding = "$.claims[0].requirements[0].verification";
        Assert.Empty(Errors(Failing(),
            Verdicts.Repair(finding, "A source file was changed", "Revert the source file"),
            Verdicts.Repair(finding, "No test was proven", "Break each behaviour once and restore it")));

        Assert.Contains(Errors(Failing(),
                Verdicts.Repair(finding, "A source file was changed", "Revert the source file"),
                Verdicts.Repair("$.claims[0]", "Source changed", "revert the source file ")),
            e => e.Contains("duplicate repair", StringComparison.Ordinal));
    }
}

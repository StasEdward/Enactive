namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Run bf24a5, 2026-10-04: the request said "Test command: verify-all" and, a line later, "Run the tests with THAT
/// command and no other." The planner returned a three-step plan and one check, quoting the second sentence for
/// it - a real passage of the request that does not itself contain the command. The whole answer was thrown away
/// as "not a plan", the planner was told only that, sent the same plan again, and the run went on as one
/// unplanned action. A check quoted loosely is not a reason to lose the plan it came with.
/// </summary>
public sealed class APlanSurvivesACheckQuotedLooselyTests
{
    private const string Request = "Sort the invoices by customer.\n\nCheck command: verify-all\n\nCheck with THAT command and no other.";

    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Read the invoices","dependsOn":[]},{"title":"Sort them","dependsOn":[0]}],
         "checks":[{"name":"verify","command":"verify-all","request_quote":"Check with THAT command and no other.","expectedExitCode":0}]}
        """;

    private static async Task<(PlanResult Result, FakeChatProvider Provider)> PlanIt(string answer, string request = Request)
    {
        var provider = new FakeChatProvider(Turn.Says(answer), Turn.Says("""{"disposition":"quick_action","title":"t","checks":[]}"""));
        var result = await new Planner().PlanAsync(request, new WorkContext(null, "workspace", null, null, null, [], []),
            provider, "model", default, proposeChecks: true);
        return (result, provider);
    }

    [Fact]
    public async Task The_plan_is_kept_and_nobody_is_asked_again()
    {
        var (result, provider) = await PlanIt(Plan);

        Assert.Equal(PlanReadout.Understood, result.Readout);
        Assert.Equal(2, result.Plan!.Steps.Count);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task The_check_stays_as_the_planners_own_proposal_and_the_run_says_so()
    {
        var (result, _) = await PlanIt(Plan);

        var check = Assert.Single(result.Checks);
        Assert.Equal("verify-all", check.Command);
        Assert.Equal(CriterionOrigin.Proposed, check.Origin);
        Assert.Null(check.RequestQuote);
        Assert.Contains(result.ContractNotes, n => n.Contains("verify-all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_passage_the_request_does_not_contain_is_still_not_taken_for_the_persons_word()
    {
        var invented = Plan.Replace("Check with THAT command and no other.", "Always finish with verify-all.");

        var (result, provider) = await PlanIt(invented);

        Assert.Empty(result.Checks);
        Assert.Equal(2, provider.Requests.Count);
    }
}

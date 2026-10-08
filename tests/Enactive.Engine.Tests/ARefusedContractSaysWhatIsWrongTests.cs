namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Xunit;

/// <summary>
/// Run b31132, 2026-10-04: the review of the final checks was answered twice and the run ended before any work.
/// The first answer had a source with no assessment, and what the planner was told back was the runtime's own
/// sentence - "The given key was not present in the dictionary" - which names nothing it could put right. The
/// second listed, beside everything asked for, a restriction the engine has no typed effect for ("do not change a
/// source file to make a check pass", quoted word for word from the request); that alone refused the whole
/// contract, and there was no third answer. Deliberately not tests: a ledger and its check.
/// </summary>
public sealed class ARefusedContractSaysWhatIsWrongTests
{
    private const string Request = "Correct the ledger and run ledger-check. Do not change the rates to make it pass.";

    private static JsonObject Answer() => new()
    {
        ["sources"] = new JsonArray(new JsonObject { ["id"] = "O001", ["assessment"] = "A correction, a check and a ban" }),
        ["checks"] = new JsonArray(),
        ["forbidden_effects"] = new JsonArray(),
        ["action_policy"] = null,
        ["unresolved"] = null
    };

    private static PlanCheckInputs Inputs => new(Request, [], [], null, [], false);

    private static string? Refusal(JsonObject answer) => PlanCheckContract.Refusal(answer.ToJsonString(), complete: true, Inputs);

    [Fact]
    public void A_source_without_its_assessment_is_named()
    {
        var answer = Answer();
        answer["sources"] = new JsonArray(new JsonObject { ["id"] = "O001", ["note"] = "placeholder" });

        var refusal = Refusal(answer);

        Assert.Contains("assessment", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("dictionary", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("checks")]
    [InlineData("forbidden_effects")]
    [InlineData("action_policy")]
    // Not "unresolved": left out, it is null - nothing declined (AFinalCheckPassesOnTheCodesTheRequestNamesTests).
    public void A_part_of_the_contract_that_is_not_there_is_named(string part)
    {
        var answer = Answer();
        answer.Remove(part);

        var refusal = Refusal(answer);

        Assert.Contains(part, refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("dictionary", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_ban_the_engine_has_no_typed_effect_for_does_not_refuse_the_contract_and_is_said()
    {
        var answer = Answer();
        answer["forbidden_effects"] = new JsonArray(new JsonObject
            { ["effect"] = "changing-the-rates", ["source_quote"] = "Do not change the rates to make it pass." });

        var contract = PlanCheckContract.Validate(answer.ToJsonString(), complete: true, Inputs);

        Assert.Empty(contract.Restrictions);                       // nothing the engine would claim to enforce
        Assert.Contains(contract.Notes, n => n.Contains("changing-the-rates", StringComparison.Ordinal));
    }

    [Fact]
    public void A_ban_quoted_from_nowhere_is_still_refused()
    {
        var answer = Answer();
        answer["forbidden_effects"] = new JsonArray(new JsonObject
            { ["effect"] = "changing-the-rates", ["source_quote"] = "Never touch the rates." });

        Assert.Contains("absent from request", Refusal(answer), StringComparison.Ordinal);
    }
}

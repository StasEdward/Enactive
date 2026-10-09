namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Xunit;

/// <summary>
/// Benchmark wiki-drift, 2026-10-09: a request naming no command to check by, a contract review that rightly left the
/// checks empty - and wrote why into "unresolved". The engine reads unresolved as "do not start", and the run ended
/// before its first step. The review is told unresolved is for a conflict only, never an explanation of an empty list.
/// </summary>
public sealed class AnEmptyContractIsNotARefusalTests
{
    [Fact]
    public async Task The_contract_review_is_told_unresolved_never_explains_an_empty_list()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"compare","checks":[]}"""),
            Turn.Says(JsonSerializer.Serialize(new
            {
                sources = new[] { new { id = "O001", assessment = "a comparison a person reads; nothing to run" } },
                checks = Array.Empty<object>(), forbidden_effects = Array.Empty<object>(), action_policy = (object?)null, unresolved = (string?)null
            })));
        var worker = new FakeChatProvider(Turn.Says("Compared."));

        await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), "Compare the notes with the code and say what differs.");

        var review = planner.Requests.Select(r => r.Messages[0].Content ?? "")
            .FirstOrDefault(c => c.StartsWith("Review the planner's FINAL verification criteria", StringComparison.Ordinal));
        Assert.NotNull(review);
        Assert.Contains("unresolved is ONLY for such a conflict, and never explains an empty checks list", review, StringComparison.Ordinal);
    }
}

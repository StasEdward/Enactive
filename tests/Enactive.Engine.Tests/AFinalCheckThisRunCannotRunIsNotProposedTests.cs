namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run 0a2be9, 2026-10-05: a run whose permissions denied every command - the work was to be done with a tool of
/// an MCP server - was given a final check "Get-Process | Sort-Object ..." by the review of the final checks. It
/// was shown every registered tool, the denied ones among them. The check could not be tried before the work,
/// warned about it, and ended NOT CHECKED: two lines about a check that could never have run. Deliberately not
/// processes: invoices, with a command proposed to check them.
/// </summary>
public sealed class AFinalCheckThisRunCannotRunIsNotProposedTests
{
    private const string Request = "Sort the invoices by customer and say which customer owes the most.";

    private static string Contract(params (string Name, string Command, string? Quote)[] checks) => JsonSerializer.Serialize(new
    {
        sources = new[] { new { id = "O001", assessment = "A sort and a question" } },
        checks = checks.Select(c => new
        {
            name = c.Name, command = c.Command, origin = c.Quote is null ? "proposed" : "requested", request_quote = c.Quote,
            expectedExitCode = 0, reason = "checks the result"
        }).ToArray(),
        action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = (string?)null
    });

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Planner)> Run(string contract, string request = Request)
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"invoices"}"""), Turn.Says(contract));
        var worker = new FakeChatProvider(Turn.Says("ACME owes the most."));
        var policy = PermissionPolicy.PermissiveDefault with { Deny = ["run_command", "run_powershell"] };
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            policy: policy, router: Routers.WithPlannerOn()), request);
        return (events, planner);
    }

    private static string[] Inventory(FakeChatProvider planner)
    {
        var body = planner.Requests[1].Messages[^1].Content!;
        using var doc = JsonDocument.Parse(body[body.IndexOf('{', body.IndexOf("Plan and draft final criteria:", StringComparison.Ordinal))..]);
        return doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("Name").GetString()!).ToArray();
    }

    [Fact]
    public async Task The_review_is_shown_only_the_tools_this_run_may_use()
    {
        var (_, planner) = await Run(Contract());

        var shown = Inventory(planner);
        Assert.DoesNotContain("run_command", shown);
        Assert.DoesNotContain("run_powershell", shown);
        Assert.Contains("read_file", shown);
    }

    [Fact]
    public async Task A_proposed_command_check_this_run_cannot_run_is_dropped_and_said_once()
    {
        var (events, _) = await Run(Contract(("sorted", "verify-invoices --sorted", null)));

        Assert.DoesNotContain(events, e => e.Summary.Contains("could not be tried before the work", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("verify-invoices", StringComparison.Ordinal));
        Assert.Single(events, e => e.Summary.Contains("verify-invoices --sorted", StringComparison.Ordinal)
                                   && e.Summary.Contains("may not run commands", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_check_the_person_asked_for_is_kept_and_reported_as_not_checked()
    {
        const string asked = Request + " Then run verify-invoices --sorted.";

        var (events, _) = await Run(Contract(("sorted", "verify-invoices --sorted", "Then run verify-invoices --sorted.")), asked);

        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("verify-invoices", StringComparison.Ordinal));
    }
}

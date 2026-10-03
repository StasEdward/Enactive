namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// 2026-10-03: "report on the disks and mail it" stopped at a question before any work - the review of the final
/// checks said the request "lacks an e-mail recipient/SMTP details". Both were in Settings, and the tool that
/// sends said so in its description, where the worker reads it. The review was shown the tools by name, kind and
/// command policy only, so what a tool says is already set up looked like something the request forgot.
/// Deliberately not mail: a tool that posts to a channel somebody chose.
/// </summary>
public sealed class TheContractReviewSeesWhatAToolSaysOfItselfTests
{
    private const string Answer =
        """{"sources":[{"id":"O001","assessment":"a notice"}],"checks":[],"forbidden_effects":[],"action_policy":null,"unresolved":null}""";

    private static async Task<FakeChatProvider> Review(params ToolDefinition[] tools)
    {
        var step = new PlanStep(Guid.NewGuid(), "post the notice", StepStatus.Pending, []);
        var planner = new FakeChatProvider(Turn.Says(Answer));
        var result = await PlanCheckReview.RunAsync(
            new PlanResult(IntentDisposition.Task, "notice", new Plan(Guid.NewGuid(), [step])),
            "Post a notice that the archive is ready.", new WorkContext(null, "workspace", null, null, null, [], []),
            planner, "strong", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, tools: tools);
        Assert.Null(result.IncompleteReason);
        return planner;
    }

    private static JsonElement[] ToolsShown(FakeChatProvider planner)
    {
        var body = planner.Requests[0].Messages[^1].Content!;
        using var doc = JsonDocument.Parse(body[body.IndexOf('{', body.IndexOf("Plan and draft final criteria:", StringComparison.Ordinal))..]);
        return doc.RootElement.GetProperty("tools").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task The_review_is_shown_each_tools_own_description()
    {
        var planner = await Review(
            new ToolDefinition("post_notice", "Posts to the channel set in Settings: #archive. The channel cannot be chosen freely.", "{}"),
            new ToolDefinition("read_file", "Reads a file of the workspace.", "{}"));

        var shown = ToolsShown(planner);
        Assert.Equal("post_notice", shown[0].GetProperty("Name").GetString());
        Assert.Contains("#archive", shown[0].GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("Reads a file of the workspace.", shown[1].GetProperty("description").GetString());
    }

    [Fact]
    public async Task The_review_is_told_that_what_a_tool_has_set_up_is_not_missing_from_the_request()
    {
        var planner = await Review(new ToolDefinition("post_notice", "Posts to the channel set in Settings.", "{}"));

        Assert.Contains("already configured", planner.Requests[0].Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_description_of_any_length_takes_a_bounded_place_in_the_review()
    {
        var planner = await Review(new ToolDefinition("external", "Start. " + new string('x', 20_000), "{}"));

        var description = ToolsShown(planner)[0].GetProperty("description").GetString()!;
        Assert.StartsWith("Start. ", description, StringComparison.Ordinal);
        Assert.True(description.Length <= PlanCheckReview.MaxToolDescription + 1, $"{description.Length} characters were sent.");
    }
}

namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Core.Events;
using Xunit;

public sealed class MixedRequirementReviewTests
{
    [Theory]
    [InlineData("constraint-no-op", true)]
    [InlineData("constraint-no-op", false)]
    [InlineData("aggregate-no-op", true)]
    [InlineData("aggregate-no-op", false)]
    public async Task Mixed_source_unit_errors_are_corrected_without_undoing_work(string error, bool corrected)
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"write then verify","steps":[{"title":"Write","dependsOn":[]},{"title":"Verify","dependsOn":[0]}]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"keep"}"""), Turn.Says("written"),
            Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("verified"));
        var good = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("written", 1), "S1").Text!)!;
        var parts = good["claims"]![0]!["requirements"]!.AsArray();
        parts[0]!["requirement"] = "No git or deletion";
        parts[0]!["global"] = true;
        parts[0]!["what"] = "Only permitted write_file was used";
        var deferred = parts[0]!.DeepClone();
        deferred["requirement"] = "Verify later";
        deferred["global"] = false;
        deferred["scope"] = "S2";
        deferred["shown"] = "no";
        deferred["calls"] = new JsonArray();
        deferred["what"] = "Deferred to S2";
        parts.Add(deferred);
        var bad = good.DeepClone();
        var expectedPath = "$.claims[0].shown";
        if (error == "constraint-no-op")
        {
            bad["claims"]![0]!["shown"] = "not-by-any-call";
            bad["claims"]![0]!["calls"] = new JsonArray();
            bad["claims"]![0]!["requirements"]![0]!["shown"] = "nothing-to-do";
            expectedPath = "$.claims[0].requirements[0].shown";
        }
        else bad["claims"]![0]!["shown"] = "nothing-to-do";
        var reviewer = new FakeChatProvider(Turn.Says(bad.ToJsonString()),
            Turn.Says((corrected ? good : bad).ToJsonString()),
            Verdicts.Combined(Verdicts.Shown("verified", 2), "S2"),
            Verdicts.Combined(Verdicts.Shown("whole request verified", 1, 2), "run"));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            reviewContent: false, checkSoundness: true, reviewRetries: 2), "Write result.txt; verify later; no git or deletion.");
        Assert.Equal(corrected ? RunOutcomeKind.Completed : RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal("keep", fx.Read("result.txt"));
        Assert.Equal(corrected ? 5 : 3, worker.Requests.Count);
        Assert.Equal(corrected ? 4 : 2, reviewer.Requests.Count);
        Assert.Contains(expectedPath, reviewer.Requests[1].Messages.Last().Content!);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ArtifactReverted or EventKind.ReviewFailed);
        Assert.Equal(corrected ? 2 : 1, events.Count(e => e.Kind == EventKind.StepStarted));
    }
}

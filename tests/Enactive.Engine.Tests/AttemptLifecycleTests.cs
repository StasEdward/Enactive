namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Core.Workers;
using Xunit;

public sealed class AttemptLifecycleTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Fallback_preserves_review_attempts_and_only_terminal_rejection_reverts(bool dag, bool accepted)
    {
        using var fx = new EngineFixture();
        var plan = new FakeChatProvider(Turn.Says(dag
            ? """{"disposition":"task","title":"write","steps":[{"title":"write","dependsOn":[]}]}"""
            : """{"disposition":"quick_action","title":"write","steps":[]}"""));
        var backup = new FakeChatProvider(
            Turn.Calls1("write_file", """{"path":"result.txt","content":"draft"}"""),
            Turn.Says("first answer"),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"fixed"}"""),
            Turn.Says("second answer"));
        var review = new FakeChatProvider(Verdicts.Fail("repair the draft"),
            accepted ? Verdicts.Pass() : Verdicts.Fail("still wrong"));
        var worker = EngineFixture.WorkerWith("write_file") with
        {
            ModelPolicy = new ModelPolicy(new ModelRef("primary", "m"), new ModelRef("backup", "m"))
        };
        var router = new ModelRouter(new ModelResolver(), new Dictionary<ModelPurpose, ModelRef>
        {
            [ModelPurpose.Plan] = new("planner", "m"),
            [ModelPurpose.Review] = new(Verdicts.ProviderId, Verdicts.Model)
        });
        var engine = fx.Build(new MapProviderFactory(plan, ("planner", plan),
            ("primary", new ThrowingChatProvider("offline")), ("backup", backup),
            (Verdicts.ProviderId, review)), worker: worker, router: router, reviewRetries: 1);
        var events = await fx.RunAsync(engine, "Write result.txt");
        Assert.Single(events, e => e.Kind == EventKind.Routed && e.Summary.Contains("fallback"));
        Assert.Equal(2, review.Requests.Count);
        Assert.Contains(backup.Requests[2].Messages, m => m.Content?.Contains("repair the draft") == true);
        Assert.Contains(backup.Requests[2].Messages, m => m.Content == "first answer");
        Assert.Equal(accepted, fx.Exists("result.txt"));
        if (accepted)
        {
            Assert.Equal("fixed", fx.Read("result.txt"));
            Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
            Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
        }
        else Assert.Contains(events, e => e.Kind == EventKind.ArtifactReverted);
    }
}

namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Intents;
using Xunit;

/// <summary>
/// The step review is shown what the application gave the worker - the workspace, the git branch, the host - as the
/// worker's prompt has it. Told only the calls, the review failed reports for stating the machine's name and OS "with no
/// call behind them", when the engine had put them in the worker's instructions: bench disk-report, 2026-10-29, the first
/// rejection in 7 runs of 8 (plans/aidlc-adoption-bench.md, "Phase 5").
/// </summary>
public sealed class TheStepReviewIsShownWhatTheApplicationGaveTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the disk report"}""";

    [Fact]
    public async Task The_review_is_given_the_host_line_the_worker_was_given_word_for_word()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"report.md","content":"Machine: BENCH-PC-7"}""", "w1"),
            Turn.Says("Wrote the report."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var host = new HostInfo("Microsoft Windows 10.0.26300 (X64)", "X64", "BENCH-PC-7", "bench", 32, null, null);
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, "main", [], [],
            new EnvironmentInfo(Guid.NewGuid(), fx.Workspace.Id, host, [], [], [], []));
        var engine = fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var _ in engine.SubmitIntentAsync(
                           new Intent(Guid.NewGuid(), "write the disk report", IntentSource.CommandBar, context, DateTimeOffset.UtcNow), cts.Token)) { }

        var review = string.Join("\n", Assert.Single(reviewer.Requests).Messages.Select(m => m.Content));
        Assert.Contains("GIVEN BY THE APPLICATION", review, StringComparison.Ordinal);
        Assert.Contains("repeats what the application", review, StringComparison.Ordinal);
        var given = engine.GivenFacts(context);
        Assert.Contains("machine BENCH-PC-7", given, StringComparison.Ordinal);
        Assert.Contains("Git branch: main", given, StringComparison.Ordinal);
        Assert.Contains(given.TrimEnd(), review, StringComparison.Ordinal);
        // One text: what the review is shown is what the worker's prompt said.
        Assert.Contains(given, string.Join("\n", worker.Requests[1].Messages.Select(m => m.Content)), StringComparison.Ordinal);
    }
}

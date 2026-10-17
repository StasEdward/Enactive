namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The checks a person declared, on the service's run page as on the window's (Plan Phase 3.5): a step's on its card with
/// PASS / FAIL / NOT RUN and why, and Open for the file that holds the whole output - read through the service - and the
/// run's own (a wave's, the run's end) under the steps. The words are the window's projection (CheckLines).
/// </summary>
public sealed class CheckLinesOnCardsTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-check-cards", Guid.NewGuid().ToString("N"));

    public CheckLinesOnCardsTests()
    {
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        Services.AddSingleton(new LiveRunClock(null));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private string Known(string name)
    {
        var root = Path.Combine(_folders, name);
        Directory.CreateDirectory(root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(root);
        return root;
    }

    private static RunEventRecord Checked(DateTimeOffset at, CheckFiring firing, string outcome, int? exit, string summary)
        => new(at, nameof(EventKind.CriterionEvaluated), summary, firing.Step,
            WorkEventPayload.CriterionPayload(firing.CheckId, outcome, true, exit, "Declared", firing));

    /// <summary>A finished run of two steps: lint failed on step 1 (its output kept), audit could not run at the run's end.</summary>
    private static async Task<RunRecord> RecordedAsync(string root)
    {
        var runId = Guid.NewGuid();
        var detail = $".enactive/checks/runs/{runId:D}/step-1-lint.txt";
        Directory.CreateDirectory(Path.Combine(root, ".enactive", "checks", "runs", runId.ToString("D")));
        File.WriteAllText(Path.Combine(root, detail), "src/a.ts: 3 problems");

        var at = DateTimeOffset.Now.AddHours(-1);
        var run = new RunRecord(runId, Guid.NewGuid(), "tidy the docs", "qwen", at, at.AddSeconds(5), "Failed",
            [
                new RunEventRecord(at, nameof(EventKind.PlanCreated), "Docs — 2 steps: write | link", null,
                    WorkEventPayload.PlanPayload("tidy the docs", ["write", "link"])),
                new RunEventRecord(at, nameof(EventKind.StepStarted), "[1/2] write", 1, "{\"step\":1}"),
                Checked(at, new CheckFiring("lint", CheckBoundary.Step) { Step = 1, DetailPath = detail }, "Failed", 2,
                    "FAIL (exit 2) — lint (step 1): npm run lint\n    3 problems"),
                new RunEventRecord(at, nameof(EventKind.StepStarted), "[2/2] link", 2, "{\"step\":2}"),
                Checked(at, new CheckFiring("audit", CheckBoundary.Run), "Unknown", null,
                    "NOT CHECKED — audit (the run): npm audit\n    not run: needs permission - the policy does not let the engine run it"),
                new RunEventRecord(at, nameof(EventKind.TaskFailed), "Failed", null,
                    WorkEventPayload.OutcomePayload(RunOutcomeKind.Failed, "lint failed"))
            ],
            [], []);
        await RunStoreFactory.Create(WorkspaceInfo.For(root)).SaveAsync(run, CancellationToken.None);
        return run;
    }

    [Fact]
    public async Task A_step_card_shows_its_checks_and_opens_the_detail_file()
    {
        var root = Known("project");
        var past = await RecordedAsync(root);
        SignIn("vera");
        var screen = Render<RunsScreen>(p => p.Add(s => s.WorkspaceId, WorkspaceInfo.For(root).Id).Add(s => s.PastRunId, past.RunId));
        screen.WaitForAssertion(() => Assert.Equal(2, screen.FindAll(".past-run .card.step").Count));

        var cards = screen.FindAll(".past-run .card.step");
        var line = Assert.Single(cards[0].QuerySelectorAll(".check-line"));
        Assert.Equal("FAIL", line.QuerySelector(".check-word")!.TextContent.Trim());
        Assert.Contains("tone-failed", line.ClassName);
        Assert.Contains("lint — at the step's end", line.QuerySelector(".check-text")!.TextContent);
        Assert.Equal("exit 2", line.QuerySelector(".check-reason")!.TextContent.Trim());
        Assert.Empty(cards[1].QuerySelectorAll(".check-line"));

        // The run's own, under the steps: not run, and why.
        var run = Assert.Single(screen.FindAll(".past-run .run-checks .check-line"));
        Assert.Equal("NOT RUN", run.QuerySelector(".check-word")!.TextContent.Trim());
        Assert.Equal("not run: needs permission", run.QuerySelector(".check-reason")!.TextContent.Trim());
        // A check that kept no file offers nothing to open.
        Assert.Null(run.QuerySelector(".check-open"));

        // Open reads the file through the service, even for a Viewer: it is the run's record.
        cards[0].QuerySelector(".check-open")!.Click();
        screen.WaitForAssertion(() => Assert.Contains("src/a.ts: 3 problems", screen.Find("[role=dialog]").TextContent));
    }
}

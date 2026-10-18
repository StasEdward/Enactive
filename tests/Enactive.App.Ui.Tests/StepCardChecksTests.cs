namespace Enactive.App.Ui.Tests;

using Enactive.App.Ui.ViewModels;
using Enactive.Application.Projections;
using Enactive.Core.Checks;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// The window's step card shows the declared checks that fired for its step (Plan Phase 3.5), redrawn as the fold changes -
/// a write check's latest in place of its earlier one - with Open for the file of the one that kept its output.
/// </summary>
public sealed class StepCardChecksTests
{
    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent Wrote(string outcome, int exit, string path)
        => Ev(EventKind.CriterionEvaluated, $"Write-time check 'spell': {outcome} (exit {exit}) - run after writing a.md.",
            WorkEventPayload.CriterionPayload("spell", outcome == "PASS" ? "Passed" : "Failed", false, exit, "Declared",
                new CheckFiring("spell", CheckBoundary.Write) { Step = 1, DetailPath = path }));

    [Fact]
    public void A_card_shows_its_checks_as_they_come_and_opens_the_latest_file()
    {
        var feed = new RunFeed("docs");
        feed.Apply(Ev(EventKind.PlanCreated, "Docs — 1 steps: write", WorkEventPayload.PlanPayload("Docs", ["write"])));
        feed.Apply(Ev(EventKind.StepStarted, "[1/1] write", "{\"step\":1}"));
        string? opened = null;
        var card = new StepCardViewModel(feed.Cards[0], path => opened = path);
        Assert.False(card.Checks.HasLines);

        feed.Apply(Wrote("FAIL", 1, "w1.txt"));
        card.Sync();
        Assert.Equal(CheckVerdict.Fail, Assert.Single(card.Checks.Lines).Verdict);

        feed.Apply(Wrote("PASS", 0, "w2.txt"));
        card.Sync();
        var line = Assert.Single(card.Checks.Lines);
        Assert.Equal(("PASS", "after a write (the latest of 2)"), (line.Word, line.Where));
        Assert.True(line.CanOpen);

        line.OpenDetailCommand.Execute(null);
        Assert.Equal("w2.txt", opened);
    }
}

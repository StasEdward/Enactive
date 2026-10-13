namespace Enactive.Engine.Tests;

using Enactive.Application.Projections;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A run's panel figures (routing, tokens, where the work ran, artifacts) folded in one place. The desktop window
/// folded them in a switch of its own, so a second host would have had to copy that switch to show the same figures.
/// </summary>
public sealed class RunPanelTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static ModelReach ReachOf(string? id) => id == "ollama" ? ModelReach.Local : ModelReach.Cloud;

    private static WorkEvent Event(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UnixEpoch, kind, summary, payload);

    [Fact]
    public void A_routed_event_names_the_phase_s_model()
    {
        var panel = new RunPanel(ReachOf);

        var changed = panel.Apply(Event(EventKind.Routed, "Worker -> model anthropic/sonnet",
            WorkEventPayload.RoutePayload("worker", "anthropic", "sonnet")));

        Assert.True(changed);
        var row = Assert.Single(panel.Routing.Rows);
        Assert.Equal(("worker", "anthropic/sonnet"), (row.Phase, row.Model));
    }

    [Fact]
    public void Usage_counts_tokens_and_where_they_ran()
    {
        var panel = new RunPanel(ReachOf);

        Assert.True(panel.Apply(Event(EventKind.UsageReported, "usage",
            WorkEventPayload.UsagePayload(100, 20, 1, providerId: "ollama"))));
        Assert.True(panel.Apply(Event(EventKind.UsageReported, "usage",
            WorkEventPayload.UsagePayload(300, 40, 1, providerId: "anthropic"))));

        Assert.Equal(400, panel.PromptTokens);
        Assert.Equal(60, panel.CompletionTokens);
        Assert.Equal(new[] { ModelReach.Local, ModelReach.Cloud }, panel.Work.Rows.Select(r => r.Reach));
    }

    [Fact]
    public void Each_artifact_produced_counts_once()
    {
        var panel = new RunPanel(ReachOf);
        var made = Event(EventKind.ArtifactProduced, "report.md",
            WorkEventPayload.ArtifactPayload("document", "report.md"));

        Assert.True(panel.Apply(made));
        Assert.True(panel.Apply(Event(EventKind.ArtifactProduced, "notes.md",
            WorkEventPayload.ArtifactPayload("document", "notes.md"))));

        Assert.Equal(2, panel.Artifacts);
    }

    [Fact]
    public void A_file_written_twice_counts_once_as_in_the_window()
    {
        var panel = new RunPanel(ReachOf);
        Func<string, WorkEvent> wrote = path => Event(EventKind.ArtifactProduced, "Wrote: " + path,
            WorkEventPayload.ArtifactPayload("document", path));

        Assert.True(panel.Apply(wrote("report.md")));
        Assert.False(panel.Apply(wrote("report.md")));
        Assert.False(panel.Apply(wrote("REPORT.md")));
        // A run recorded before the payload named a path: the window reads it from the summary.
        Assert.True(panel.Apply(Event(EventKind.ArtifactProduced, "Wrote: old.md")));
        Assert.False(panel.Apply(Event(EventKind.ArtifactProduced, "Wrote: old.md")));

        Assert.Equal(2, panel.Artifacts);
    }

    [Fact]
    public void Other_events_change_nothing()
    {
        var panel = new RunPanel(ReachOf);

        Assert.False(panel.Apply(Event(EventKind.IntentReceived, "started")));
        // A Routed event naming no phase says nothing: it must not claim a change a panel would redraw for.
        Assert.False(panel.Apply(Event(EventKind.Routed, "Something else")));
        // Usage without a readable payload carries no tokens.
        Assert.False(panel.Apply(Event(EventKind.UsageReported, "usage")));

        Assert.Empty(panel.Routing.Rows);
        Assert.Empty(panel.Work.Rows);
        Assert.Equal((0, 0, 0), (panel.PromptTokens, panel.CompletionTokens, panel.Artifacts));
    }

    [Fact]
    public void A_completed_call_reaches_the_performance_figures()
    {
        var panel = new RunPanel(ReachOf);
        panel.Performance.ShowRun(Run);

        panel.Add(new ModelCallMetrics(ProviderId: "anthropic", Model: "sonnet", PromptTokens: 100, CompletionTokens: 50,
            CachedPromptTokens: null, RequestSeconds: 2, FirstDeltaSeconds: 0.5, Timings: null, RunId: Run));

        Assert.Single(panel.Performance.Rows);
    }

    /// <summary>
    /// The files themselves, for a host's Artifacts tab: one per path, in the order they were first written. With only a
    /// count a second host could show the tile but not the list behind it, and would have folded the events again for it.
    /// </summary>
    [Fact]
    public void The_artifacts_are_listed_once_each_in_the_order_first_written()
    {
        var panel = new RunPanel(ReachOf);
        Func<string, WorkEvent> wrote = path => Event(EventKind.ArtifactProduced, "Wrote: " + path,
            WorkEventPayload.ArtifactPayload("document", path));

        panel.Apply(wrote("report.md"));
        panel.Apply(wrote("notes.md"));
        panel.Apply(wrote("REPORT.md"));

        Assert.Equal(["report.md", "notes.md"], panel.Files.Written);
    }

    /// <summary>
    /// A staging run's ArtifactProduced is a proposal, not a file it wrote - as the window takes it (AddStagedArtifact).
    /// Read as written, the service's page offered Review of the unchanged file on disk and Delete of the person's own.
    /// Every event is told as a proposal, a repeated path too: the window shows one card per change the store kept.
    /// </summary>
    [Fact]
    public void A_staging_run_s_files_are_proposed_not_written()
    {
        var panel = new RunPanel(ReachOf);
        panel.Files.Stages = true;
        Func<string, WorkEvent> made = path => Event(EventKind.ArtifactProduced, "Wrote: " + path,
            WorkEventPayload.ArtifactPayload("document", path));

        Assert.Equal(new FileNews.Staged("notes.md"), panel.Files.Apply(made("notes.md")));
        Assert.Equal(new FileNews.Staged("notes.md"), panel.Files.Apply(made("notes.md")));
        Assert.True(panel.Apply(Event(EventKind.ArtifactProduced, "Wrote: old.md")));

        Assert.Empty(panel.Files.Written);
        Assert.Equal(["notes.md", "old.md"], panel.Files.Staged);
        Assert.Equal(2, panel.Artifacts);
    }

    /// <summary>
    /// A rejected step's files are put back: the written ones its event names are marked, as the window marks their cards,
    /// and only those - the paths are the payload's values, never read out of the sentence. They stay among the files
    /// written (the card is still there, saying put back), and a later write to the same path makes no second card.
    /// </summary>
    [Fact]
    public void Files_a_rejected_step_wrote_are_put_back()
    {
        var panel = new RunPanel(ReachOf);
        Func<string, WorkEvent> wrote = path => Event(EventKind.ArtifactProduced, "Wrote: " + path,
            WorkEventPayload.ArtifactPayload("document", path));
        panel.Apply(wrote("report.md"));
        panel.Apply(wrote("notes.md"));

        var news = panel.Files.Apply(Event(EventKind.ArtifactReverted, "put back: REPORT.md; NOT put back: notes.md",
            WorkEventPayload.RevertPayload(1, ["REPORT.md", "elsewhere.md"], ["notes.md"])));

        Assert.Equal(["REPORT.md", "elsewhere.md"], Assert.IsType<FileNews.Reverted>(news).Paths);
        Assert.Equal(["report.md"], panel.Files.Reverted);
        Assert.True(panel.Files.IsReverted("report.md"));
        Assert.False(panel.Files.IsReverted("notes.md"));
        Assert.Equal(["report.md", "notes.md"], panel.Files.Written);
        Assert.IsType<FileNews.None>(panel.Files.Apply(wrote("report.md")));
    }
}

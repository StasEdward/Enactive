namespace Enactive.Engine.Tests;

using Enactive.Application.Projections;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// Small rules for showing a run that the window kept inside itself and the service shows too - how long it has taken, what
/// a line of a diff does, which icon a step's action carries. Each lives once, in Enactive.Application, so the two do not
/// say the same run two ways.
/// </summary>
public sealed class RunFiguresTests
{
    [Theory]
    [InlineData(5, "5s")]
    [InlineData(123, "2m 3s")]
    [InlineData(3723, "1h 2m 3s")]
    [InlineData(90061, "25h 1m 1s")]
    public void Elapsed_is_said_as_the_window_s_tile_says_it(int seconds, string said)
        => Assert.Equal(said, RunElapsed.Format(TimeSpan.FromSeconds(seconds)));

    // The kind by name: DiffLineKind is internal, and a public test method cannot take it as a parameter.
    [Theory]
    [InlineData("+added line", "Added")]
    [InlineData("-removed line", "Removed")]
    [InlineData(" context", "Context")]
    [InlineData("@@ -1,2 +1,3 @@", "Context")]
    [InlineData("", "Context")]
    public void A_diff_line_is_known_by_its_first_character(string line, string kind)
        => Assert.Equal(Enum.Parse<DiffLineKind>(kind), DiffLines.Kind(line));

    [Fact]
    public void Every_kind_of_step_action_has_its_icon()
    {
        Assert.Equal(["⌘", "📄", "🛠", "🔔", "🛇"], Enum.GetValues<FeedEntryKind>().Select(StepEntries.Icon));
    }

    /// <summary>
    /// A token count reads the same whatever the machine's culture: the service's tiles are drawn by a process whose culture
    /// is the server's, not the reader's, and "1,5k" on one box and "1.5k" on another is one count said two ways.
    /// </summary>
    [Fact]
    public void A_token_count_reads_the_same_in_every_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal(("1.5k", "2.3M", "999"), (TokenCounts.Compact(1_500), TokenCounts.Compact(2_300_000), TokenCounts.Compact(999)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// The TOKENS tile and its tooltip, for a live run (counts so far) and a finished one (its recorded usage): an em dash
    /// and "nothing reported" where nothing was reported - a different fact from zero - else the total and the split.
    /// </summary>
    [Fact]
    public void Tokens_are_said_as_the_tile_says_them()
    {
        Assert.Null(RunFigures.Reported(0, 0));
        Assert.Equal(("—", "nothing reported"), (RunFigures.Tokens(null), RunFigures.TokensDetail(null)));
        var used = RunFigures.Reported(1200, 300);
        Assert.Equal(("1.5k", "1.2k in · 300 out"), (RunFigures.Tokens(used), RunFigures.TokensDetail(used)));
    }

    [Theory]
    [InlineData(0, 0, "—")]
    [InlineData(1, 3, "1 / 3 steps")]
    [InlineData(3, 3, "3 / 3 steps")]
    public void Steps_are_said_as_the_status_column_says_them(int done, int total, string said)
        => Assert.Equal(said, RunFigures.Steps(done, total));

    [Theory]
    [InlineData(0, "Artifacts")]
    [InlineData(2, "Artifacts (2)")]
    public void The_artifacts_tab_carries_its_count_only_when_there_is_one(int count, string said)
        => Assert.Equal(said, RunFigures.ArtifactsTab(count));

    [Fact]
    public void The_log_tab_says_which_run_it_is_filtered_to()
    {
        Assert.Equal("No run yet.", RunFigures.LogHeader(null));
        Assert.Equal("Filtered to run 0f1e2d", RunFigures.LogHeader(Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")));
    }

    /// <summary>The model figures read the same whatever the server's culture, as the token counts do.</summary>
    [Fact]
    public void The_model_figures_read_the_same_in_every_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            var run = Guid.NewGuid();
            var performance = new ModelPerformance();
            performance.ShowRun(run);
            performance.Add(new Enactive.Core.Chat.ModelCallMetrics("ollama", "qwen", 12_345, 2_500, null, 2.5, 0.25, null, run));

            var row = Assert.Single(performance.Rows);
            Assert.Equal("≈ 1,000.0 T/s", row.Speed);
            Assert.Equal("Tokens  12,345 in / 2,500 out", row.Tokens);
            Assert.Equal("Request avg  2.50 s   ·   1 calls", row.Requests);
            Assert.Equal("First delta avg  0.25 s", row.First);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// Run again re-resolves the template as it stands now, so it is offered only while the template is in the library -
    /// a button that fails when pressed is worse than none - and its tooltip says which version against which. The window
    /// worked this out inside its past-run view model; the service's past run offers the same button.
    /// </summary>
    [Fact]
    public void Run_again_is_offered_only_while_the_template_is_in_the_library()
    {
        var spec = new Enactive.Core.Templates.ResolvedTaskSpec("digest", 2, "Digest", Guid.NewGuid(), "project", @"C:\project",
            "digest the inbox", new Dictionary<string, string>(), new Enactive.Core.Permissions.PermissionPolicy(Enactive.Core.Permissions.PermissionLevel.Autonomous, ["*"], []), [],
            Enactive.Core.Templates.ExecutionLimits.None, null, false);

        Assert.Equal(new RunAgainOffer(false, "This run did not come from a template."), RunAgainOffer.Of(null, null));
        Assert.Equal(new RunAgainOffer(false, "Template 'digest' is no longer in this library."), RunAgainOffer.Of(spec, null));
        Assert.Equal(new RunAgainOffer(true, "Re-resolve 'Morning digest' v3 — this run used v2."),
            RunAgainOffer.Of(spec, new Enactive.Core.Templates.TaskTemplate("digest", "Morning digest", "digest the inbox", Version: 3)));
    }

    /// <summary>
    /// A past run whose record rebuilds no step cards opens on its timeline, which is what such a record holds - on
    /// Execution it showed an empty tab that read as a run that did nothing.
    /// </summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    public void A_past_run_with_no_steps_opens_on_its_timeline(int steps, int tab)
        => Assert.Equal(tab, PastRunTabs.Opening(steps));
}

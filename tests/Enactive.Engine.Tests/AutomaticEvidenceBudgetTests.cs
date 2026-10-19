namespace Enactive.Engine.Tests;

using System.Globalization;
using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Guidance;
using Enactive.Settings;
using Xunit;

/// <summary>
/// The evidence budget follows the reviewer's context window unless a person set one (ReviewEvidence).
///
/// <para>It was one number, 6,000 characters, tuned for a weak local reviewer and used for every reviewer: in run 2eedd5
/// (2026-10-20) claude-haiku-5-5, with a 1,000,000-token window, was shown about 1,000 of the 14,500 characters of one
/// read and failed a correct step for files it said were never read.</para>
/// </summary>
public sealed class AutomaticEvidenceBudgetTests
{
    // What the step review of run 2eedd5 cost before its evidence (about 5,200 tokens with 6,000 characters of it), and
    // what it asked to answer in (2,048 tokens and 8,192 of reasoning).
    private const int PromptTokens = 3_200;
    private const int AnswerTokens = 10_240;

    // ── the rule ────────────────────────────────────────────────────────────

    /// <summary>
    /// A local model's 131,072-token window and a cloud model's 1,000,000 both give more than the old fixed number, the
    /// larger never less, and neither past the ceiling: past it the evidence only dilutes the reviewer's attention. A
    /// smaller window gives less again - the order is the window's.
    /// </summary>
    [Fact]
    public void A_larger_window_never_gives_less_and_every_one_stays_between_the_floor_and_the_ceiling()
    {
        int Auto(int window)
        {
            var budget = ReviewEvidence.For(null, window, PromptTokens, AnswerTokens);
            Assert.True(budget.Automatic);
            Assert.InRange(budget.Chars, ExecutionJournal.MinimumBudget, ReviewEvidence.Ceiling);
            return budget.Chars;
        }

        var (small, local, cloud) = (Auto(32_768), Auto(131_072), Auto(1_000_000));

        Assert.True(small < local, $"{small} against {local}");
        Assert.True(local <= cloud, $"{local} against {cloud}");
        Assert.True(local > ExecutionJournal.DefaultBudget, $"{local}: a 131K reviewer is shown no more than the old fixed number");
        Assert.Equal(ReviewEvidence.Ceiling, cloud);
        // Every window in between keeps the order.
        var windows = new[] { 8_192, 16_384, 32_768, 65_536, 131_072, 200_000, 1_000_000 };
        var budgets = windows.Select(Auto).ToArray();
        Assert.Equal(budgets.Order(), budgets);
    }

    /// <summary>The room is what is left after the review's prompt and answer, so a prompt that fills the window gets the floor.</summary>
    [Fact]
    public void A_prompt_that_fills_the_window_leaves_the_floor()
    {
        Assert.Equal(ExecutionJournal.MinimumBudget, ReviewEvidence.For(null, 8_192, 7_000, 2_048).Chars);
        Assert.True(ReviewEvidence.For(null, 32_768, 20_000, 2_048).Chars < ReviewEvidence.For(null, 32_768, 2_000, 2_048).Chars);
    }

    /// <summary>A reviewer whose window nobody stated is shown what every reviewer was: there is nothing to derive from.</summary>
    [Fact]
    public void A_window_nobody_stated_keeps_the_old_default()
    {
        Assert.Equal(new ReviewEvidence.Budget(ExecutionJournal.DefaultBudget, true, null), ReviewEvidence.For(null, null, PromptTokens, AnswerTokens));
        Assert.Equal(ExecutionJournal.DefaultBudget, ReviewEvidence.For(null, 0, PromptTokens, AnswerTokens).Chars);
        Assert.Equal(ExecutionJournal.DefaultBudget, ReviewEvidence.UpTo(null));
    }

    /// <summary>A person's number is used whatever the window - raised to the floor as it always was, never held to the ceiling.</summary>
    [Fact]
    public void A_number_a_person_set_overrides_the_window()
    {
        Assert.Equal(new ReviewEvidence.Budget(9_000, false, 1_000_000), ReviewEvidence.For(9_000, 1_000_000, PromptTokens, AnswerTokens));
        Assert.Equal(6_000, ReviewEvidence.For(6_000, 1_000_000, PromptTokens, AnswerTokens).Chars);
        Assert.Equal(100_000, ReviewEvidence.For(100_000, 8_192, PromptTokens, AnswerTokens).Chars);
        Assert.Equal(ExecutionJournal.MinimumBudget, ReviewEvidence.For(500, null, PromptTokens, AnswerTokens).Chars);
    }

    /// <summary>The settings' advice says the rule in numbers; they are written in Core, which cannot see the rule's own.</summary>
    [Fact]
    public void The_advice_says_the_numbers_the_rule_uses()
    {
        var advice = SettingsAdvice.EvidenceBudget;
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        Assert.Contains($"between {N(ExecutionJournal.MinimumBudget)} and {N(ReviewEvidence.Ceiling)} characters", advice);
        Assert.Contains($"{N(ExecutionJournal.DefaultBudget)} when", advice);
        Assert.Contains("a quarter of the room", advice);
        Assert.Equal(4, ReviewEvidence.ShareOfRoom);
    }

    /// <summary>
    /// The room the budget keeps for the answer is the review's own answer limit (StepVerdictReview.AnswerTokens), and an
    /// answer cut off at that limit is refused - a cut JSON that happens to read as a pass included - never taken as a verdict.
    /// </summary>
    [Fact]
    public async Task A_review_cut_off_at_its_answer_limit_is_refused_not_read()
    {
        const string pass = """{"verdict":"pass","reason":"done","calls":[],"files":[]}""";
        var reviewer = new FakeChatProvider(new Turn(pass, FinishReason: "length"), new Turn(pass, FinishReason: "length"));

        var result = await StepVerdictReview.RunAsync(
            new StepVerdictInput("do it", "the step", 1, [], "done", null, [], EvidenceView.Empty), reviewer, "m", null, CancellationToken.None);

        Assert.IsType<ReviewVerdict.Unavailable>(result.Verdict);
        Assert.NotEmpty(reviewer.Requests);
        Assert.All(reviewer.Requests, r => Assert.Equal(StepVerdictReview.AnswerTokens, r.OutputTokenLimit));
    }

    // ── in a run ────────────────────────────────────────────────────────────

    private const string TwoStepPlan = """
        {"disposition":"task","title":"look at the parts",
         "steps":[{"title":"Read the six parts","dependsOn":[]},
                  {"title":"Say what they hold","dependsOn":[0]}]}
        """;

    /// <summary>
    /// A two-step run whose first step reads six files of about 7,800 characters each - about 47,000 characters of
    /// output, more than six times the old fixed budget - reviewed by <paramref name="reviewer"/>.
    /// </summary>
    private static async Task<List<WorkEvent>> RunReadingSixFiles(FakeChatProvider reviewer, int? evidenceBudget)
    {
        using var fx = new EngineFixture();
        var reads = new List<Turn> { Turn.Says(TwoStepPlan) };
        for (var part = 1; part <= 6; part++)
        {
            fx.Write($"part{part}.txt", string.Concat(Enumerable.Range(1, 300).Select(i => $"part {part}, line {i,4} of 300\n")));
            reads.Add(Turn.Calls1("read_file", $$"""{"path":"part{{part}}.txt"}""", $"r{part}"));
        }
        reads.Add(Turn.Says("read them"));
        var worker = new FakeChatProvider(reads.ToArray()) { WhenExhausted = Turn.Says("done") };
        return await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer, reviewRetries: 0, evidenceBudget: evidenceBudget),
            "look at the parts");
    }

    private static int LongestReview(FakeChatProvider reviewer)
        => reviewer.Requests.Where(Verdicts.IsStepVerdict)
            .Max(r => r.Messages.Sum(m => m.Content?.Length ?? 0));

    private static string[] SaidBudgets(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.ContextAssembled && e.Summary.Contains("Evidence for the review:"))
            .Select(e => e.Summary).ToArray();

    /// <summary>
    /// Unset, the budget is the reviewer's: one with a 1,000,000-token window is shown the six reads whole,
    /// where one that states no window is shown the old 6,000. The run's log says which, once per review.
    /// </summary>
    [Fact]
    public async Task Unset_the_reviewer_with_a_large_window_is_shown_more_of_what_was_read()
    {
        var unstated = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());
        var unstatedEvents = await RunReadingSixFiles(unstated, evidenceBudget: null);
        var large = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass()) { Window = 1_000_000 };
        var largeEvents = await RunReadingSixFiles(large, evidenceBudget: null);

        Assert.True(LongestReview(large) - LongestReview(unstated) > 30_000,
            $"{LongestReview(large)} against {LongestReview(unstated)}: the window did not reach the evidence");
        var said = SaidBudgets(largeEvents);
        Assert.Equal(large.Requests.Count(Verdicts.IsStepVerdict), said.Length);
        Assert.All(said, s => Assert.Contains("72,000 characters, automatic - from the review model's 1,000,000-token window", s));
        Assert.All(SaidBudgets(unstatedEvents), s => Assert.Contains("6,000 characters, automatic - the review model states no context window", s));
    }

    /// <summary>A number a person set is what every review is shown, whatever its reviewer's window.</summary>
    [Fact]
    public async Task A_number_a_person_set_is_what_the_reviewer_is_shown()
    {
        var unstated = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());
        await RunReadingSixFiles(unstated, evidenceBudget: null);
        var large = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass()) { Window = 1_000_000 };
        var events = await RunReadingSixFiles(large, evidenceBudget: ExecutionJournal.DefaultBudget);

        Assert.InRange(LongestReview(large) - LongestReview(unstated), -200, 200);
        Assert.All(SaidBudgets(events), s => Assert.Contains("6,000 characters, as set in the settings", s));
    }

    /// <summary>
    /// Per review, not per run: each review asks its own reviewer for its window when it is assembled. Here the reviewer's
    /// window is stated only from the second review on, as it would be for a step routed to another model.
    /// </summary>
    [Fact]
    public async Task Each_review_asks_its_reviewer_for_the_window()
    {
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());
        reviewer.Answering = request =>
        {
            if (Verdicts.IsStepVerdict(request)) reviewer.Window = 131_072;
            return null;
        };

        var said = SaidBudgets(await RunReadingSixFiles(reviewer, evidenceBudget: null));

        Assert.Equal(2, said.Length);
        Assert.Contains("states no context window", said[0]);
        Assert.Contains("from the review model's 131,072-token window", said[1]);
    }

    // ── the settings ────────────────────────────────────────────────────────

    private static void Edit(string path, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    /// <summary>A file as a build before schema 9 wrote it: its version, and the evidence budget in both places it kept it.</summary>
    private static string OlderFile(EngineFixture fx, int evidenceBudget)
    {
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));
        Edit(path, root =>
        {
            root["SchemaVersion"] = 8;
            root["Engine"]!["EvidenceBudget"] = evidenceBudget;
            root["EvidenceBudget"] = evidenceBudget;
        });
        return path;
    }

    /// <summary>
    /// Every build before 9 wrote its default out, so an older file's 6000 is nobody's choice: read as automatic, it does
    /// not hold every reviewer to the old number for good. Saved again, the file is version 9 and says nothing of it.
    /// </summary>
    [Fact]
    public void An_older_file_s_written_out_default_is_read_as_automatic()
    {
        using var fx = new EngineFixture();
        var path = OlderFile(fx, 6_000);

        var loaded = AppSettings.Load(path);

        Assert.Null(loaded.Engine.EvidenceBudget);
        Assert.True(loaded.Save(path));
        Assert.Null(AppSettings.Load(path).Engine.EvidenceBudget);
    }

    /// <summary>Any other number in an older file was typed by somebody, and is kept.</summary>
    [Fact]
    public void An_older_file_s_own_number_is_kept()
    {
        using var fx = new EngineFixture();

        Assert.Equal(9_000, AppSettings.Load(OlderFile(fx, 9_000)).Engine.EvidenceBudget);
    }

    /// <summary>From 9 on, 6000 is a number like any other: a person who types it keeps it.</summary>
    [Fact]
    public void A_6000_set_from_schema_9_on_is_kept()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings { Engine = EngineOptions.Default with { EvidenceBudget = 6_000 } }.Save(path));

        var loaded = AppSettings.Load(path);

        Assert.Equal(6_000, loaded.Engine.EvidenceBudget);
        Assert.True(loaded.Save(path));
        Assert.Equal(6_000, AppSettings.Load(path).Engine.EvidenceBudget);
    }

    /// <summary>
    /// Automatic is written as nothing, in the section and in the top-level copy alike: a build from before it reads the
    /// budget as a number, and a null there would leave that build unable to read the file at all. Without it, that build
    /// runs on its own default, as it always did.
    /// </summary>
    [Fact]
    public void Automatic_is_left_out_of_the_file()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.False(saved["Engine"]!.AsObject().ContainsKey("EvidenceBudget"));
        Assert.False(saved.ContainsKey("EvidenceBudget"));
        Assert.Equal(AppSettings.CurrentSchemaVersion, (int)saved["SchemaVersion"]!);
        Assert.Null(AppSettings.Load(path).Engine.EvidenceBudget);
        Assert.Null(EngineOptions.Default.EvidenceBudget);
    }

    /// <summary>The box: blank is automatic, both ways; a number is a number; what is not one, or is below the floor, is automatic.</summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("auto", null)]
    [InlineData("1499", null)]
    [InlineData("1500", 1500)]
    [InlineData(" 9000 ", 9000)]
    public void The_box_reads_blank_as_automatic(string typed, int? expected)
    {
        var switches = EngineSwitches.Of(EngineOptions.Default);
        Assert.Equal("", switches.EvidenceBudget);
        switches.EvidenceBudget = typed;

        Assert.Equal(expected, switches.ApplyTo(EngineOptions.Default).EvidenceBudget);
        Assert.Equal("9000", EngineSwitches.Of(EngineOptions.Default with { EvidenceBudget = 9000 }).EvidenceBudget);
    }
}

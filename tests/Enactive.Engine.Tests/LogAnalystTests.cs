namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Xunit;

/// <summary>
/// "AI Analyze" in the log window: hand the log to a model and get back what went wrong, in prose.
///
/// <para>A log is the one artifact here that is complete and unreadable at once — the answer to "why
/// did that fail" is in twelve thousand lines, and finding it means knowing what to look for, which
/// is what somebody asking does not have.</para>
///
/// <para>The whole risk of the feature is the same one this codebase keeps meeting: a log that does
/// not fit gets cut, and if the cut is silent the answer is confidently about the wrong half. So the
/// excerpt is tested harder than the request is.</para>
/// </summary>
public sealed class LogAnalystTests
{
    private static string[] Log(int lines)
        => Enumerable.Range(1, lines).Select(i => $"17:33:{i:D2}  INF  Orchestrator  line {i}").ToArray();

    // ── the excerpt ─────────────────────────────────────────────────────────

    [Fact]
    public void A_log_that_fits_is_sent_whole_and_unmarked()
    {
        var lines = Log(20);

        var excerpt = LogAnalyst.Excerpt(lines, 100_000, out var sent);

        Assert.Equal(20, sent);
        Assert.DoesNotContain(LogAnalyst.Gap, excerpt);
        Assert.Equal(string.Join("\n", lines), excerpt);
    }

    /// <summary>
    /// The one that decides whether the feature is useful or actively misleading. Taking the first N
    /// characters is the obvious implementation and it reliably hands over the part of a log with no
    /// failures in it, and then asks what failed.
    /// </summary>
    [Fact]
    public void The_end_of_the_log_survives_because_that_is_where_the_failure_is()
    {
        var lines = Log(4_000).ToList();
        lines[0] = "THE REQUEST";
        lines[^1] = "TaskFailed: Incomplete";
        lines[^2] = "ERR the thing that actually broke";

        var excerpt = LogAnalyst.Excerpt(lines, 4_000, out var sent);

        Assert.Contains("THE REQUEST", excerpt);
        Assert.Contains("TaskFailed: Incomplete", excerpt);
        Assert.Contains("the thing that actually broke", excerpt);
        Assert.True(sent < lines.Count);
    }

    /// <summary>The cut says where it is, and how much went — silence here is the whole failure mode.</summary>
    [Fact]
    public void The_missing_middle_announces_itself_and_says_how_much()
    {
        var excerpt = LogAnalyst.Excerpt(Log(4_000), 4_000, out var sent);

        Assert.Contains(LogAnalyst.Gap, excerpt);
        Assert.Contains($"({4_000 - sent:N0} lines)", excerpt);
    }

    [Fact]
    public void The_excerpt_stays_inside_the_budget()
    {
        foreach (var budget in new[] { 1_000, 4_000, 20_000 })
        {
            var excerpt = LogAnalyst.Excerpt(Log(10_000), budget, out _);
            Assert.True(excerpt.Length <= budget + LogAnalyst.Gap.Length + 40,
                $"budget {budget} produced {excerpt.Length} characters");
        }
    }

    /// <summary>Both ends are real: a budget that only fits the tail must not silently drop the head.</summary>
    [Fact]
    public void Both_ends_are_kept_not_just_one()
    {
        var lines = Log(2_000).ToList();
        lines[0] = "FIRST LINE";
        lines[^1] = "LAST LINE";

        var excerpt = LogAnalyst.Excerpt(lines, 3_000, out _);

        Assert.Contains("FIRST LINE", excerpt);
        Assert.Contains("LAST LINE", excerpt);
    }

    [Fact]
    public void An_empty_log_is_not_a_crash()
    {
        Assert.Equal("", LogAnalyst.Excerpt(Array.Empty<string>(), 1_000, out var sent));
        Assert.Equal(0, sent);
    }

    // ── what the model is told ──────────────────────────────────────────────

    /// <summary>
    /// A model that is not told the log was cut will report the cut as the failure: the text ends
    /// mid-line, so something must have crashed. It has to be told, in the prompt, every time.
    /// </summary>
    [Fact]
    public async Task An_excerpt_says_so_in_the_prompt()
    {
        var provider = new FakeChatProvider(Turn.Says("Nothing went wrong."));

        var result = await new LogAnalyst().AnalyseAsync(
            string.Join("\n", Log(20_000)), provider, "a-model", contextWindowTokens: 4_000,
            CancellationToken.None);

        var prompt = provider.Requests.Single().Messages.Last().Content ?? "";

        Assert.Contains("EXCERPT", prompt);
        Assert.Contains(LogAnalyst.Gap, prompt);
        Assert.True(result.WasExcerpt);
        Assert.Contains("read", result.Provenance);
    }

    /// <summary>And a whole log is not announced as an excerpt, or the notice stops meaning anything.</summary>
    [Fact]
    public async Task A_whole_log_is_not_announced_as_an_excerpt()
    {
        var provider = new FakeChatProvider(Turn.Says("All fine."));

        var result = await new LogAnalyst().AnalyseAsync(
            string.Join("\n", Log(10)), provider, "a-model", contextWindowTokens: 100_000,
            CancellationToken.None);

        var prompt = provider.Requests.Single().Messages.Last().Content ?? "";

        Assert.Contains("the whole log", prompt);
        Assert.DoesNotContain("EXCERPT", prompt);
        Assert.False(result.WasExcerpt);
        Assert.Equal(10, result.LinesSent);
    }

    /// <summary>
    /// The instructions carry the two clauses that stop an analyst inventing findings — the same
    /// failure the content reviewer had on 2026-09-07, when it named a specific closing tag at a
    /// specific line of a file that was correct.
    /// </summary>
    [Fact]
    public void The_instructions_forbid_inventing_and_permit_a_short_answer()
    {
        Assert.Contains("Only report what you can point at", LogAnalyst.SystemPrompt);
        Assert.Contains("never invent", LogAnalyst.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("If nothing went wrong", LogAnalyst.SystemPrompt);
        Assert.Contains("excerpt", LogAnalyst.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An empty reply is a failed request, not a verdict that the log is clean. Saying "no problems
    /// found" because the model returned nothing is the exact shape of the bug that made an
    /// unreachable reviewer count as a pass.
    /// </summary>
    [Fact]
    public async Task A_model_that_answered_nothing_has_not_said_the_log_is_clean()
    {
        var result = await new LogAnalyst().AnalyseAsync(
            "one line", new FakeChatProvider(Turn.Says("")), "a-model", 100_000, CancellationToken.None);

        Assert.Contains("failed request", result.Answer);
        Assert.DoesNotContain("no problems", result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_analysis_reports_what_it_cost()
    {
        var result = await new LogAnalyst().AnalyseAsync(
            "one line", new FakeChatProvider(Turn.Says("Fine.").Reporting(prompt: 900, completion: 120)),
            "a-model", 100_000, CancellationToken.None);

        Assert.Equal(900, result.PromptTokens);
        Assert.Equal(120, result.CompletionTokens);
        Assert.Equal("a-model", result.Model);
    }

    // ── the budget ──────────────────────────────────────────────────────────

    /// <summary>
    /// A window nobody declared still gets a workable budget, and a tiny one still leaves room to
    /// read something - a model with a small context is exactly the one whose logs need reading.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(2_048)]
    [InlineData(131_072)]
    public void There_is_always_room_for_some_of_the_log(int? window)
        => Assert.True(LogAnalyst.BudgetChars(window) >= 4_000);

    [Fact]
    public void A_bigger_window_reads_more_of_the_log()
        => Assert.True(LogAnalyst.BudgetChars(131_072) > LogAnalyst.BudgetChars(8_192));
}

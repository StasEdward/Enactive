namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;

public sealed class SavedCommandReportTests
{
    private const string Quote = "Initial run: exit 0.";
    private static JsonNode Answer(int call, string occurrence, int exit = 0, string quote = Quote)
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("report written", 3), "S2").Text!)!;
        answer["command_reports"] = new JsonArray(new JsonObject {
            ["source_id"] = ReviewSources.FileId("REPORT.md"), ["source_type"] = "saved-file", ["fragment_id"] = "F1", ["call"] = call, ["exit_code"] = exit,
            ["scope"] = "S2", ["occurrence"] = occurrence
        });
        return answer;
    }

    private static EvidenceView Evidence()
    {
        var journal = new ExecutionJournal();
        journal.Record(2, "run_command", """{"command":"dotnet run","expectedExitCodes":[0,1]}""",
            ActionOutcome.Failed, "one test failed", exitCode: 1);
        journal.Record(2, "run_command", """{"command":"dotnet run"}""",
            ActionOutcome.Succeeded, "22 passed", exitCode: 0);
        journal.Record(2, "write_file", "REPORT.md", ActionOutcome.Succeeded, "saved");
        return journal.Describe();
    }
    private static Task<ReviewResult> Review(FakeChatProvider reviewer, string content = Quote)
        => new Reviewer().ReviewWithProofAsync("Report", "done", Evidence(), ["REPORT.md"],
            [new("REPORT.md", content)], RequestObligations.Create("Report actual command outcomes",
                step: 2, steps: ["implement", "report"]), reviewer, "model", default);

    [Theory]
    [InlineData(1, "first", false)] // Wrong exit code.
    
    [InlineData(2, "last", true)]
    public async Task Later_success_does_not_rewrite_initial_failure(int call, string occurrence, bool pass)
    {
        var quote = occurrence == "last" ? "Final run: exit 0." : Quote;
        var reviewer = new FakeChatProvider(Turn.Says(Answer(call, occurrence, quote: quote).ToJsonString()));
        var result = await Review(reviewer, quote);
        Assert.Equal(pass, result.Pass);
        Assert.Null(result.IncompleteReason);
        Assert.Single(reviewer.Requests);
        if (!pass) Assert.Contains("REPORT.md", result.Notes);
    }

    [Fact]
    public async Task False_quote_is_repaired_by_reviewer_not_worker()
    {
        const string actual = "Final run: exit 0.";
        var bad = Answer(2, "last"); bad["command_reports"]![0]!["fragment_id"] = "F999";
        var reviewer = new FakeChatProvider(Turn.Says(bad.ToJsonString()),
            Turn.Says(Answer(2, "last", quote: actual).ToJsonString()));
        var result = await Review(reviewer, actual);
        Assert.True(result.Pass);
        Assert.Contains("$.command_reports[0].fragment_id", reviewer.Requests[1].Messages.Last().Content!);
    }

    [Fact]
    public async Task Report_with_both_initial_failure_and_final_success_passes()
    {
        var answer = Answer(1, "first", 1, "Initial run: exit 1.");
        var final = Answer(2, "last")["command_reports"]![0]!.DeepClone(); final["fragment_id"] = "F2";
        answer["command_reports"]!.AsArray().Add(final);
        var result = await Review(new FakeChatProvider(Turn.Says(answer.ToJsonString())),
            "Initial run: exit 1.\nFinal run: exit 0.");
        Assert.True(result.Pass);
        Assert.True(result.Soundness!.Sound);
    }

    [Fact]
    public async Task Scope_and_missing_source_are_repaired_in_one_clarification()
    {
        var invalid = Answer(2, "last");
        invalid["claims"]![0]!["scope"] = "unknown-step";
        invalid["command_reports"]![0]!["source_id"] = "agent report";
        var reviewer = new FakeChatProvider(Turn.Says(invalid.ToJsonString()),
            Turn.Says(Answer(2, "last").ToJsonString()));
        var result = await Review(reviewer);
        Assert.True(result.Pass);
        Assert.Equal(2, reviewer.Requests.Count);
        var clarification = reviewer.Requests[1].Messages.Last().Content!;
        Assert.Contains("$.claims[0].scope", clarification);
        Assert.Contains("$.command_reports[0].source_id", clarification);
        Assert.Contains("evidence_id", clarification);
        Assert.DoesNotContain(ReviewSources.FileId("REPORT.md"), clarification);
        Assert.DoesNotContain("$.command_reports[0].fragment_id:", clarification); // No source => cannot validate quote.
    }

    [Fact]
    public async Task Worker_message_is_a_valid_separate_source_without_saved_files()
    {
        var answer = Answer(2, "last");
        answer["command_reports"]![0]!["source_id"] = "worker-report";
        answer["command_reports"]![0]!["source_type"] = "worker-report";
        var reviewer = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("Report", Quote, Evidence(), [], [],
            RequestObligations.Create("Report actual command outcomes", step: 2, steps: ["implement", "report"]),
            reviewer, "model", default);
        Assert.True(result.Pass);
        Assert.Single(reviewer.Requests);
        Assert.Contains("worker message, not a file", reviewer.Requests[0].Messages[1].Content!);
    }

    [Fact]
    public async Task Malformed_report_fields_do_not_hide_other_semantic_errors()
    {
        var invalid = Answer(2, "last");
        invalid["command_reports"]![0]!["fragment_id"] = 42;
        invalid["command_reports"]![0]!["call"] = 999;
        invalid["command_reports"]![0]!["source_type"] = "worker-report";
        invalid["claims"]![0]!["scope"] = "unknown-step";
        var reviewer = new FakeChatProvider(Turn.Says(invalid.ToJsonString()),
            Turn.Says(Answer(2, "last").ToJsonString()));
        Assert.True((await Review(reviewer)).Pass);
        var clarification = reviewer.Requests[1].Messages.Last().Content!;
        foreach (var field in new[] { "fragment_id", "call", "source_type" })
            Assert.Contains("$.command_reports[0]." + field, clarification);
        Assert.Contains("$.claims[0].scope", clarification);
    }

    [Fact]
    public async Task Hidden_file_text_is_not_accepted_as_a_visible_quote()
    {
        var invalid = Answer(2, "last"); invalid["command_reports"]![0]!["fragment_id"] = "F999";
        var corrected = Answer(2, "last");
        corrected["command_reports"] = new JsonArray();
        var reviewer = new FakeChatProvider(Turn.Says(invalid.ToJsonString()), Turn.Says(corrected.ToJsonString()));
        var content = new string('a', 9000) + "Hidden command succeeded." + new string('b', 9000);
        Assert.True((await Review(reviewer, content)).Pass);
        Assert.Contains("select an existing nonempty numbered fragment", reviewer.Requests[1].Messages.Last().Content!);
        Assert.DoesNotContain("Hidden command succeeded.", reviewer.Requests[0].Messages[1].Content!);
    }

    [Fact]
    public async Task Repeated_source_error_is_incomplete_not_a_worker_rejection()
    {
        var invalid = Answer(2, "last");
        invalid["command_reports"]![0]!["source_id"] = "agent report";
        var reviewer = new FakeChatProvider(Turn.Says(invalid.ToJsonString()), Turn.Says(invalid.ToJsonString()));
        var result = await Review(reviewer);
        Assert.False(result.Pass);
        Assert.NotNull(result.IncompleteReason);
        Assert.Equal(2, reviewer.Requests.Count);
    }

    [Fact]
    public async Task Fragment_reference_preserves_markdown_without_copying_the_quote()
    {
        const string actual = "**Final** run: `exit 0`.";
        var reviewer = new FakeChatProvider(Turn.Says(Answer(2, "last").ToJsonString()));
        Assert.True((await Review(reviewer, actual)).Pass);
        Assert.Single(reviewer.Requests);
        Assert.Matches(@"\[evidence \d+\] " + System.Text.RegularExpressions.Regex.Escape(actual), reviewer.Requests[0].Messages[1].Content!);
    }

    [Fact]
    public async Task Invented_first_annotation_is_corrected_without_rejecting_worker()
    {
        var reviewer = new FakeChatProvider(Turn.Says(Answer(2, "first").ToJsonString()),
            Turn.Says(Answer(2, "specific").ToJsonString()));
        var result = await Review(reviewer, "Tests now pass, exit 0.");
        Assert.True(result.Pass);
        Assert.Null(result.IncompleteReason);
        Assert.Equal(2, reviewer.Requests.Count);
        Assert.Contains("$.command_reports[0].occurrence", reviewer.Requests[1].Messages.Last().Content!);
    }

    [Fact]
    public void Occurrence_uses_step_and_ignores_expected_exit_declarations()
    {
        Assert.Equal(1, Evidence().CommandOccurrence(2, 2, false));
        Assert.Equal(2, Evidence().CommandOccurrence(1, 2, true));
        Assert.Null(Evidence().CommandOccurrence(2, 1, false));
    }
}


namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// A refused combined review is corrected by the parts the refusal names; the parts it got right
/// stand (amendment G).
///
/// <para>A refusal used to be answered with the whole review again, and a second whole answer could
/// break what the first had right - which the corpus shows it doing.</para>
/// </summary>
public sealed class AReviewIsCorrectedByPartsTests(ITestOutputHelper output)
{
    private static JsonNode Answer(params string[] ids)
        => JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content checked"), "run", ids).Text!)!;

    // ── which parts ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_claims_error_asks_for_the_claims_the_verdict_and_the_repairs_that_point_into_them()
    {
        var parts = ReviewSectionRepair.For(Answer("O001").ToJsonString(), ["$.claims: missing obligation ID 'O002'"])!;

        Assert.Equal(["verdict", "notes", "claims", "repairs"], parts.Requested);
        Assert.Contains("command_reports", parts.Kept);
        Assert.Contains("assessments", parts.Kept);
    }

    [Fact]
    public void An_error_in_a_section_repairs_do_not_point_into_leaves_the_repairs_alone()
        => Assert.Equal(["verdict", "notes", "command_reports"],
            ReviewSectionRepair.For(Answer("O001").ToJsonString(), ["$.command_reports[0].occurrence: selected first command is call 34, not 37"])!.Requested);

    /// <summary>What is not about a part is not corrected by parts: the answer as a whole, a field no review has.</summary>
    [Theory]
    [InlineData("$: expected a combined JSON object")]
    [InlineData("$.reason: unknown field")]
    public void An_error_about_the_whole_answer_asks_for_the_whole_answer(string error)
        => Assert.Null(ReviewSectionRepair.For(Answer("O001").ToJsonString(), [error]));

    [Fact]
    public void No_answer_to_keep_parts_of_means_no_parts()
        => Assert.Null(ReviewSectionRepair.For("""{"verdict":"fail","claims":[""", ["$.claims: invalid"]));

    // ── the merge ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_parts_given_replace_their_old_selves_and_nothing_else_changes()
    {
        var original = Answer("O001");
        original["command_reports"] = new JsonArray(new JsonObject { ["call"] = 7 });
        var parts = ReviewSectionRepair.For(original.ToJsonString(), ["$.claims: missing obligation ID 'O002'"])!;
        var fixedClaims = Answer("O001", "O002")["claims"]!.DeepClone();

        var merged = JsonNode.Parse(parts.Merge(new JsonObject
        {
            ["verdict"] = "pass", ["notes"] = "checked", ["claims"] = fixedClaims, ["repairs"] = new JsonArray()
        }.ToJsonString()))!;

        Assert.Equal(2, merged["claims"]!.AsArray().Count);
        Assert.Equal(7, merged["command_reports"]![0]!["call"]!.GetValue<int>());     // kept, not re-asked
    }

    [Fact]
    public void A_field_no_review_has_is_not_a_part()
    {
        var parts = ReviewSectionRepair.For(Answer("O001").ToJsonString(), ["$.claims: missing obligation ID 'O002'"])!;
        Assert.Throws<FormatException>(() => parts.Merge("""{"claims":[],"reason":"x"}"""));
        Assert.Throws<FormatException>(() => parts.Merge("""{"proof":{"shown":"no","calls":[],"what":"x"}}"""));
    }

    // ── the whole loop ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: refused for a missing claim, asked for the claims alone (with the
    /// schema narrowed to them), answered with the claims alone - and passed, on the merged answer.
    /// </summary>
    [Fact]
    public async Task A_refused_review_is_passed_on_the_parts_corrected_alone()
    {
        var first = Answer("O001");
        var patch = new JsonObject
        {
            ["verdict"] = "pass", ["notes"] = "checked",
            ["claims"] = Answer("O001", "O002")["claims"]!.DeepClone(), ["repairs"] = new JsonArray()
        };
        var provider = new FakeChatProvider(Turn.Says(first.ToJsonString()), Turn.Says(patch.ToJsonString()));

        var result = await new Reviewer().ReviewWithProofAsync("write", "Wrote both records", new ExecutionJournal().Describe(),
            [], [], RequestObligations.Create("Write one record\nWrite another record"), provider, "review", default);

        Assert.True(result.Pass, result.Notes);
        Assert.Equal(2, provider.Requests.Count);
        var retry = provider.Requests[1];
        Assert.Contains("Correct ONLY these parts", retry.Messages.Last().Content!, StringComparison.Ordinal);
        var schema = JsonNode.Parse(retry.ResponseSchema!)!;
        Assert.Equal(["verdict", "notes", "claims", "repairs"],
            schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
    }

    // ── the corpus ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the recorded refusals would have been asked for instead of a whole answer. A measurement
    /// as much as a test: every refusal whose errors are about parts must be corrected by parts.
    /// </summary>
    [Fact]
    public void Recorded_refusals_about_parts_are_corrected_by_parts()
    {
        var folder = CorpusRatchet.RepositoryFolder("review-corpus");
        var byParts = 0;
        var cases = Directory.GetFiles(folder, "2026*.json").Order(StringComparer.Ordinal).ToArray();
        foreach (var path in cases)
        {
            var recorded = ReviewCorpus.Read(path);
            var parts = ReviewSectionRepair.For(recorded.Answer, recorded.Errors);
            var aboutParts = recorded.Errors.All(e => e.StartsWith("$.", StringComparison.Ordinal)
                && ReviewSectionRepair.Sections.Contains(e[2..].Split('.', '[', ':', ' ')[0]));
            Assert.Equal(aboutParts, parts is not null);
            if (parts is not null) byParts++;
            output.WriteLine($"{Path.GetFileName(path)}: " + (parts is null
                ? "whole answer again"
                : $"parts {string.Join(", ", parts.Requested)}; kept {string.Join(", ", parts.Kept)}"));
        }
        output.WriteLine($"\n{byParts}/{cases.Length} recorded refusals are corrected by parts.");
    }
}

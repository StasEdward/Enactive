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

    // ── claims one by one ────────────────────────────────────────────────────────────────

    private static readonly RequestObligations Four = RequestObligations.Create("one\ntwo\nthree\nfour");

    [Fact]
    public void A_refusal_that_names_claims_asks_for_those_claims_alone()
    {
        var answer = Answer("O001", "O002", "O003").ToJsonString();
        var parts = ReviewSectionRepair.For(answer,
            ["$.claims[1].requirements[0].verification: required field is missing", "$.claims: missing obligation ID 'O004'"], Four)!;

        Assert.Equal(["O002", "O004"], parts.ClaimIds);
        Assert.Contains("ONLY the claims for O002, O004", parts.Instruction("refused"), StringComparison.Ordinal);
        Assert.Contains("$.claims[3]=O004", parts.Instruction("refused"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$.claims: expected array")]
    [InlineData("$.claims[9].scope: out of range")]
    public void A_refusal_about_the_claims_as_a_whole_asks_for_all_of_them(string error)
        => Assert.Null(ReviewSectionRepair.For(Answer("O001", "O002").ToJsonString(), [error], Four)!.ClaimIds);

    [Fact]
    public void Every_claim_named_is_the_whole_section()
        => Assert.Null(ReviewSectionRepair.For(Answer("O001").ToJsonString(),
            ["$.claims: missing obligation ID 'O002'", "$.claims: missing obligation ID 'O003'", "$.claims: missing obligation ID 'O004'",
             "$.claims[0].scope: unknown scope 'S9'"], Four)!.ClaimIds);

    /// <summary>The claims asked for replace theirs; the others are the originals, untouched; the list follows the obligations.</summary>
    [Fact]
    public void Claims_given_replace_only_the_ones_asked_for_in_the_obligations_order()
    {
        var original = Answer("O003", "O001", "O002");
        original["claims"]![0]!["what"] = "kept three";
        original["claims"]![1]!["what"] = "kept one";
        var parts = ReviewSectionRepair.For(original.ToJsonString(),
            ["$.claims[2].scope: unknown scope 'S9'", "$.claims: missing obligation ID 'O004'"], Four)!;

        var fresh = Answer("O002", "O004", "O001")["claims"]!.AsArray();
        fresh[2]!["what"] = "not asked for";
        var merged = JsonNode.Parse(parts.Merge(new JsonObject
        {
            ["verdict"] = "pass", ["notes"] = "n", ["claims"] = fresh.DeepClone(), ["repairs"] = new JsonArray()
        }.ToJsonString()))!["claims"]!.AsArray();

        Assert.Equal(["O001", "O002", "O003", "O004"], merged.Select(c => c!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("kept one", merged[0]!["what"]!.GetValue<string>());
        Assert.Equal("kept three", merged[2]!["what"]!.GetValue<string>());
    }

    // ── a cut answer ─────────────────────────────────────────────────────────────────────

    /// <summary>An answer cut inside its second claim: everything before that claim is whole.</summary>
    private static string CutInsideSecondClaim(params string[] ids)
    {
        var text = Answer(ids).ToJsonString();
        var second = text.IndexOf("\"id\":\"O002\"", StringComparison.Ordinal);
        return text[..(second + 20)];
    }

    [Fact]
    public void What_closed_before_the_cut_is_kept_and_only_the_rest_is_asked_for()
    {
        var prefix = ReviewSectionRepair.CompletePrefix(CutInsideSecondClaim("O001", "O002", "O003"))!;
        var kept = JsonNode.Parse(prefix)!.AsObject();
        Assert.Equal(["verdict", "notes", "proof", "assessments", "claims"], kept.Select(p => p.Key).ToArray());
        Assert.Equal("O001", Assert.Single(kept["claims"]!.AsArray())!["id"]!.GetValue<string>());

        var rest = ReviewSectionRepair.ForCut(CutInsideSecondClaim("O001", "O002", "O003"),
            RequestObligations.Create("one\ntwo\nthree"), s => s)!;
        Assert.Equal(["verdict", "notes", "claims", "need_evidence", "command_reports", "report_checks", "repairs"], rest.Requested);
        Assert.Equal(["O002", "O003"], rest.ClaimIds);
    }

    [Theory]
    [InlineData("{\"verdict\":\"pass\",\"notes\":\"the work is do")]
    [InlineData("no json at all")]
    public void A_cut_with_nothing_but_the_verdict_before_it_keeps_nothing(string cut)
        => Assert.Null(ReviewSectionRepair.ForCut(cut, Four, s => s));

    [Fact]
    public void A_whole_answer_is_not_a_cut_one()
        => Assert.Null(ReviewSectionRepair.CompletePrefix(Answer("O001").ToJsonString()));

    /// <summary>
    /// THE ONE THAT MATTERS for the cut: the answer stops at the output limit inside its second
    /// claim; the reviewer is asked for the rest alone, and the review passes on the merge.
    /// </summary>
    [Fact]
    public async Task A_review_cut_by_the_output_limit_is_finished_by_asking_for_the_rest()
    {
        var obligations = RequestObligations.Create("Write one record\nWrite another record\nWrite a third");
        var whole = Answer("O001", "O002", "O003");
        var rest = new JsonObject
        {
            ["verdict"] = "pass", ["notes"] = "checked",
            ["claims"] = new JsonArray(whole["claims"]![1]!.DeepClone(), whole["claims"]![2]!.DeepClone()),
            ["need_evidence"] = new JsonArray(), ["command_reports"] = new JsonArray(),
            ["report_checks"] = new JsonArray(), ["repairs"] = new JsonArray()
        };
        var provider = new FakeChatProvider(
            Turn.Says(CutInsideSecondClaim("O001", "O002", "O003")) with { FinishReason = "max_tokens" },
            Turn.Says(rest.ToJsonString()));

        var result = await new Reviewer().ReviewWithProofAsync("write", "Wrote three records", new ExecutionJournal().Describe(),
            [], [], obligations, provider, "review", default);

        Assert.True(result.Pass, result.Notes);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("cut off by the output limit", provider.Requests[1].Messages.Last().Content!, StringComparison.Ordinal);
        Assert.Contains("ONLY the claims for O002, O003", provider.Requests[1].Messages.Last().Content!, StringComparison.Ordinal);
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
            // With the obligations, the claims too are asked for one by one.
            var fine = ReviewSectionRepair.For(recorded.Answer, recorded.Errors, recorded.Obligations);
            output.WriteLine($"{Path.GetFileName(path)}: " + (parts is null
                ? "whole answer again"
                : $"parts {string.Join(", ", parts.Requested)}; kept {string.Join(", ", parts.Kept)}"
                  + (fine?.ClaimIds is { } ids ? $"; claims only {string.Join(", ", ids)} of {recorded.Obligations.Items.Count}" : "")));
        }
        output.WriteLine($"\n{byParts}/{cases.Length} recorded refusals are corrected by parts.");
    }
}

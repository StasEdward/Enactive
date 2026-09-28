namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;

public sealed class ReviewReferenceTests
{
    private static JsonNode Answer() => JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content checked"), "run").Text!)!;
    private static JsonObject Check(int id, string verdict = "pass") => new() {
        ["evidence_id"] = id, ["kind"] = "requirement-map", ["verdict"] = verdict, ["reason"] = "Compared with original requirement",
        ["calls"] = new JsonArray(), ["obligation_ids"] = new JsonArray("O001")
    };

    [Fact]
    public void Numeric_schema_preserves_full_enums_and_removes_compound_source_references()
    {
        Assert.DoesNotContain("source_id", Reviewer.CombinedWireSchema);
        Assert.DoesNotContain("fragment_id", Reviewer.CombinedWireSchema);
        var schema = JsonNode.Parse(Reviewer.CombinedWireSchema)!;
        Assert.Equal("integer", schema["properties"]!["report_checks"]!["items"]!["properties"]!["evidence_id"]!["type"]!.GetValue<string>());
        Assert.Equal(JsonNode.Parse(Reviewer.CombinedSchema)!["properties"]!["verdict"]!.ToJsonString(), schema["properties"]!["verdict"]!.ToJsonString());
    }

    [Fact]
    public void Expansion_does_not_reassign_numbers_and_old_hidden_text_cannot_be_cited()
    {
        var refs = new ReviewReferences();
        var source = new ReviewSource("internal-file", "saved-file", "file.cs", "old\nsame");
        refs.BeginView(); refs.Render(source);
        var old = refs.Visible.Single(e => e.Text == "old").Id;
        var same = refs.Visible.Single(e => e.Text == "same").Id;
        refs.BeginView(); refs.Render(new("internal-file", "saved-file", "file.cs", "new\nsame"));
        Assert.Equal(same, refs.Visible.Single(e => e.Text == "same").Id);
        Assert.DoesNotContain(refs.Visible, e => e.Id == old);
        Assert.Throws<FormatException>(() => refs.Decode($$"""{"report_checks":[{"evidence_id":{{old}}}]}"""));
        Assert.Equal("F2", JsonNode.Parse(refs.Decode($$"""{"assertions":[{"evidence_id":{{same}}}]}"""))!["assertions"]![0]!["fragment_id"]!.GetValue<string>());
        Assert.True(refs.Visible.Single(e => e.Text == "new").Id > same);
    }

    [Theory]
    [InlineData("""{"assertions":[{"evidence_id":0}]}""")]
    [InlineData("""{"assertions":[{"evidence_id":999}]}""")]
    [InlineData("""{"assertions":[{"evidence_id":"1"}]}""")]
    [InlineData("""{"assertions":[{"evidence_id":1,"source_id":"worker-report"}]}""")]
    public void Invalid_or_ambiguous_ids_are_refused(string text)
    {
        var refs = new ReviewReferences();
        refs.Render(new("worker-report","worker-report","message","visible"));
        Assert.Throws<FormatException>(() => refs.Decode(text));
    }

    [Fact]
    public void Work_repair_zero_is_resolved_without_inventing_a_source()
    {
        var decoded = new ReviewReferences().Decode("""{"repairs":[{"target":"work","evidence_id":0}]}""");
        Assert.Equal("", JsonNode.Parse(decoded)!["repairs"]![0]!["source_id"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_only_merge_preserves_existing_findings_and_remaps_patch_repair_indices()
    {
        var refs = new ReviewReferences();
        var sources = new ReviewSources("O001: first\nO001: second", refs);
        sources.RenderReport();
        var ids = refs.Visible.Select(e => e.Id).ToArray();
        var original = Answer();
        original["report_checks"] = new JsonArray(JsonNode.Parse(refs.Decode(new JsonObject {
            ["report_checks"] = new JsonArray(Check(ids[0]))
        }.ToJsonString()))!["report_checks"]![0]!.DeepClone());
        var pending = new ReviewMappingCompletion(original.ToJsonString(), refs);
        var patch = new JsonObject { ["report_checks"] = new JsonArray(Check(ids[1], "fail")),
            ["repairs"] = new JsonArray(new JsonObject {
                ["findings"] = new JsonArray("$.report_checks[0]"), ["target"] = "source",
                ["evidence_id"] = ids[1], ["defect"] = "Wrong mapping", ["change"] = "Correct the mapping",
                ["obligation_ids"] = new JsonArray("O001")
            }) };
        var merged = JsonNode.Parse(pending.Merge(refs.Decode(patch.ToJsonString())))!;
        Assert.Equal(original["claims"]!.ToJsonString(), merged["claims"]!.ToJsonString());
        Assert.Equal(2, merged["report_checks"]!.AsArray().Count);
        Assert.Equal("$.report_checks[1]", merged["repairs"]![0]!["findings"]![0]!.GetValue<string>());
        Assert.NotNull(SemanticReviewAudit.Outcome(merged.ToJsonString()));
        Assert.Empty(ReviewRepairContract.Errors(merged.ToJsonString(), sources, RequestObligations.Create("Write a report")));
        patch["report_checks"] = new JsonArray(Check(ids[0]));
        Assert.Throws<FormatException>(() => pending.Merge(refs.Decode(patch.ToJsonString())));
        patch["report_checks"] = new JsonArray(Check(ids[1]), Check(ids[1]));
        Assert.Throws<FormatException>(() => pending.Merge(refs.Decode(patch.ToJsonString())));
        patch["report_checks"] = new JsonArray();
        Assert.Throws<FormatException>(() => pending.Merge(refs.Decode(patch.ToJsonString())));
    }

    [Fact]
    public async Task Reviewer_requests_only_missing_assessments_and_revalidates_merged_response()
    {
        // The empty-journal notice is evidence 1; the worker fragment is evidence 2.
        var initial = Answer();
        var provider = new FakeChatProvider(Turn.Says(initial.ToJsonString()),
            Turn.Says(new JsonObject { ["report_checks"] = new JsonArray(Check(2)), ["repairs"] = new JsonArray() }.ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("review", "O001: inspected content",
            new ExecutionJournal().Describe(), [], [],
            RequestObligations.Create("Inspect content"), provider, "model", default);
        Assert.True(result.Pass, result.IncompleteReason ?? result.Notes);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(ReviewMappingCompletion.Schema, provider.Requests[1].ResponseSchema);
        Assert.Contains("Complete ONLY", provider.Requests[1].Messages.Last().Content!);
        Assert.Contains("[evidence 2]", provider.Requests[0].Messages[1].Content!);
        Assert.DoesNotContain("file-", provider.Requests[1].Messages.Last().Content!);
    }
}

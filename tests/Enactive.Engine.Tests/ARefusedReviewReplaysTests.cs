namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// A review refused by validation is written down with exactly what it was checked against, and
/// replaying that record reproduces the refusal - offline, in a second, without a model.
///
/// <para><b>Why.</b> The current blocker is structural refusals of the combined review: fifteen
/// across 2026-09-27 and 28, each costing a run of several minutes to see once. Three of the four
/// kinds seen depend on what the reviewer was SHOWN - the request's obligations, the displayed
/// evidence, the saved files - and those exist only in memory at review time. A record of the answer
/// alone could replay one kind. So the inputs themselves are recorded, and this file proves the
/// record is faithful: a replay that disagreed with the review that produced it would be measuring
/// something that never ran.</para>
/// </summary>
public sealed class ARefusedReviewReplaysTests(ITestOutputHelper output)
{
    private static JsonNode Good() => JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content checked"), "run").Text!)!;

    /// <summary>
    /// The first claim, and its requirement, cite these calls under this kind of showing. Refused by
    /// the schema validator against the EVIDENCE: whether the call exists, and what its recorded
    /// tool, outcome and exit code were - all three printed in the refusal, so a record that lost any
    /// of them would replay to different words.
    /// </summary>
    private static JsonNode Citing(string shown, params int[] calls)
    {
        var answer = Good();
        foreach (var node in new[] { answer["claims"]![0]!, answer["claims"]![0]!["requirements"]![0]! })
        {
            node["shown"] = shown;
            node["calls"] = new JsonArray(calls.Select(c => (JsonNode)c).ToArray());
        }
        return answer;
    }

    /// <summary>A repair that contradicts its own check - the shapes ReviewRepairContract refuses.</summary>
    private static JsonNode Bad(string defect)
    {
        if (defect == "phantom-call") return Citing("yes", 5);
        if (defect == "zero-exit-as-failure") return Citing("expected-failure", 2);

        var answer = Good();
        answer["report_checks"] = new JsonArray(new JsonObject {
            ["source_id"] = "worker-report", ["fragment_id"] = "F1", ["kind"] = "inferred",
            ["verdict"] = "fail", ["reason"] = "The count in the summary is wrong",
            ["calls"] = new JsonArray(), ["obligation_ids"] = new JsonArray()
        });
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.report_checks[0]", "Incorrect count",
            "State one record, not two", "worker-report", "F1"));
        switch (defect)
        {
            case "missing":
                answer["report_checks"]![0]!["reason"] = "This statement is correct; retained for completeness";
                answer["repairs"] = new JsonArray(); break;
            case "wrong-target": answer["repairs"]![0]!["target"] = "work"; break;
            case "no-op": answer["repairs"]![0]!["change"] = "Two records"; break;
            case "unknown-id": answer["repairs"]![0]!["obligation_ids"] = new JsonArray("O999"); break;
        }
        return answer;
    }

    private static EvidenceView Restore(ReviewCorpusEvidence e)
        => new(e.Text, e.Actions.ToArray(), e.VisibleActionIds, e.OutputsTruncated, e.ArgumentsTruncated,
            e.HasPriorTranscript);

    private static List<string> Replay(ReviewCorpusCase recorded)
        => ReviewCorpus.Validate(ReviewCorpus.Prepare(recorded.Answer, recorded.Obligations), recorded.Obligations,
            Restore(recorded.Evidence),
            ReviewCorpus.RestoreSources(recorded.Sources));

    /// <summary>
    /// THE ONE THAT MATTERS. A real review, refused by the real validators on its first answer, is
    /// recorded by the real recording path; the record, read back and replayed, produces the same
    /// errors, in the same order. The evidence carries calls, so the view's hidden and visible calls
    /// are both exercised, not just its text.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-target")]
    [InlineData("no-op")]
    [InlineData("unknown-id")]
    [InlineData("phantom-call")]
    [InlineData("zero-exit-as-failure")]
    public async Task A_recorded_refusal_replays_to_the_same_errors(string defect)
    {
        var root = Directory.CreateTempSubdirectory("review-corpus").FullName;
        try
        {
            var journal = new ExecutionJournal();
            journal.Record(1, "read_file", """{"path":"records.txt"}""", ActionOutcome.Succeeded, "one record");
            journal.Record(1, "run_command", """{"command":"count"}""", ActionOutcome.Succeeded, "1", exitCode: 0);

            var provider = new FakeChatProvider(Turn.Says(Bad(defect).ToJsonString()), Turn.Says(Good().ToJsonString()));
            await new Reviewer().ReviewWithProofAsync("write", "Two records", journal.Describe(), [], [],
                RequestObligations.Create("Write one record"), provider, "review-model", default, workspaceRoot: root);

            // The refusal, and only the refusal: the corrected answer that followed passed, and an
            // accepted review is not kept.
            var file = Assert.Single(Directory.GetFiles(Path.Combine(root, ReviewCorpus.Folder), "*.json"));
            var recorded = ReviewCorpus.Read(file);

            Assert.NotEmpty(recorded.Errors);
            Assert.Equal("review-model", recorded.Model);
            Assert.Equal(2, recorded.Evidence.Actions.Count);
            Assert.Equal(recorded.Errors, Replay(recorded));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// The corpus that lives in the repository: refusals copied in from real runs, replayed against
    /// the validators as they are NOW and held to <c>expected.json</c> - a case that was accepted and
    /// is refused again fails the build, and so does one that started passing without the manifest
    /// saying so. See <see cref="CorpusRatchet"/>.
    /// </summary>
    [Fact]
    public void Every_refusal_in_the_corpus_still_replays()
        => CorpusRatchet.Hold(CorpusRatchet.RepositoryFolder("review-corpus"), path =>
        {
            var now = Replay(ReviewCorpus.Read(path));
            return now.Count == 0 ? null : string.Join(" | ", now);
        }, output);

    /// <summary>
    /// A command report whose evidence_id points at tool output is told so, about the evidence_id it
    /// wrote - not about a source_type the engine filled in (run 4f1d97: 4 of 5 refusals, and a
    /// correction by parts sent the same id back).
    /// </summary>
    [Fact]
    public void A_command_report_citing_tool_output_is_told_so_about_its_evidence_id()
    {
        var cases = Directory.GetFiles(CorpusRatchet.RepositoryFolder("review-corpus"), "2026*.json").Select(ReviewCorpus.Read)
            .Where(c => c.Answer.Contains("\"source_type\":\"execution-evidence\"", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(cases);
        foreach (var recorded in cases)
        {
            var now = Replay(recorded);
            Assert.Contains(now, e => e.Contains(".evidence_id: it cites displayed tool output, not a report", StringComparison.Ordinal));
            Assert.DoesNotContain(now, e => e.Contains("source_type: expected one of", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Run 4f1d97, step 9: the first refusal named the proof's citations, a correction by parts fixed
    /// them, and only then did report_checks - kept as it was - get refused, with no round left. All the
    /// validators now run at once, and the first refusal names both.
    /// </summary>
    [Fact]
    public void A_first_refusal_names_what_used_to_surface_only_after_a_correction()
    {
        var first = ReviewCorpus.Read(Path.Combine(CorpusRatchet.RepositoryFolder("review-corpus"), "20260928-112433-9ddb535bf12f.json"));
        var now = Replay(first);
        Assert.Contains(now, e => e.StartsWith("$.proof.calls[0]", StringComparison.Ordinal));
        Assert.Contains(now, e => e.StartsWith("$.report_checks[0].calls: cite only displayed evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Recording_never_fails_the_review_it_observes()
    {
        var recorded = new ReviewCorpusCase(DateTimeOffset.UtcNow, "m", "{}", RequestObligations.Create("x"),
            new ReviewCorpusEvidence("", [], [], false, false, false), [new("worker-report", "worker-report", "r", "r")], ["e"]);

        // No workspace, and a path nothing can be written under: both are silent.
        ReviewCorpus.Record(null, recorded);
        ReviewCorpus.Record("", recorded);
        ReviewCorpus.Record(Path.Combine(Path.GetTempPath(), "no\0such"), recorded);
    }

    [Fact]
    public void The_corpus_keeps_its_newest_and_drops_the_rest()
    {
        var root = Directory.CreateTempSubdirectory("review-corpus-cap").FullName;
        try
        {
            var start = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < ReviewCorpus.Keep + 3; i++)
                ReviewCorpus.Record(root, new ReviewCorpusCase(start.AddSeconds(i), "m", "{\"n\":" + i + "}",
                    RequestObligations.Create("x"), new ReviewCorpusEvidence("", [], [], false, false, false),
                    [new("worker-report", "worker-report", "r", "r")], ["e"]));

            var kept = Directory.GetFiles(Path.Combine(root, ReviewCorpus.Folder), "*.json");
            Assert.Equal(ReviewCorpus.Keep, kept.Length);
            // The three oldest went, not three at random.
            Assert.DoesNotContain(kept, f => ReviewCorpus.Read(f).RecordedAt < start.AddSeconds(3));
        }
        finally { Directory.Delete(root, true); }
    }
}

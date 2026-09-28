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
        => ReviewCorpus.Validate(recorded.Answer, recorded.Obligations, Restore(recorded.Evidence),
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
    /// the validators as they are NOW. A case that has started to pass is a refusal a change has
    /// fixed; one that still fails says so, with its current reason. This is the number the next
    /// change to the reviewer is measured by - not a prediction, a count.
    /// </summary>
    [Fact]
    public void Every_refusal_in_the_corpus_still_replays()
    {
        var folder = CorpusFolder();
        var cases = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : [];

        var fixedNow = 0;
        foreach (var path in cases)
        {
            var recorded = ReviewCorpus.Read(path);
            var now = Replay(recorded);
            if (now.Count == 0) fixedNow++;
            output.WriteLine($"{Path.GetFileName(path)} [{recorded.Model}]: "
                + (now.Count == 0 ? "ACCEPTED NOW" : "still refused: " + string.Join(" | ", now)));
        }
        output.WriteLine($"\n{fixedNow}/{cases.Length} recorded refusals are accepted by today's validators.");
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

    private static string CorpusFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Enactive.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "Enactive.Engine.Tests", "review-corpus");
    }
}

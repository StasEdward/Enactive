namespace Enactive.Engine.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class NegativeEvidenceAndConstraintsTests
{
    [Theory]
    [InlineData(ActionOutcome.Failed, 1, true)]
    [InlineData(ActionOutcome.Succeeded, 1, true)] // Declared expectedExitCodes.
    [InlineData(ActionOutcome.Failed, null, false)] // Timeout / failed launch.
    [InlineData(ActionOutcome.Refused, 1, false)]
    [InlineData(ActionOutcome.Succeeded, 0, false)] // Surviving mutation.
    public void Negative_proof_requires_a_real_nonzero_exit(ActionOutcome outcome, int? exit, bool sound)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", "test", outcome, "observed result", exitCode: exit);
        var claim = new ProofClaim(ProofClaimKind.ExpectedFailure, [1], "Touching test detects the requested mutation");
        Assert.Equal(sound, ProofAudit.Check(claim, journal.Describe()).Sound);
        Assert.False(ProofAudit.Check(claim with { Calls = [] }, journal.Describe()).Sound);
        Assert.False(ProofAudit.Check(claim with { Calls = [2] }, journal.Describe()).Sound);
        if (outcome == ActionOutcome.Failed)
            Assert.False(ProofAudit.Check(claim with { Kind = ProofClaimKind.Shown }, journal.Describe()).Sound);
        Assert.Equal(outcome, journal.Actions[0].Outcome);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrected")]
    [InlineData("uncorrected")]
    public async Task Failed_mutation_then_restored_pass_does_not_restart_worker(string response)
    {
        using var fx = new EngineFixture();
        var script = OperatingSystem.IsWindows() ? "check.cmd" : "check.sh";
        var command = OperatingSystem.IsWindows() ? "check.cmd" : "sh check.sh";
        Turn Write(int code) => Turn.Calls1("write_file", JsonSerializer.Serialize(new {
            path = script, content = OperatingSystem.IsWindows() ? $"@echo off\r\nexit /b {code}\r\n" : $"exit {code}\n"
        }));
        Turn Run() => Turn.Calls1("run_command", JsonSerializer.Serialize(new { command }));
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"negative test and restore","steps":[]}"""),
            Write(1), Run(), Write(0), Run(), Turn.Says("Observed requested failure, restored and passed"));
        var answer = Answer();
        var bad = answer.DeepClone();
        bad["claims"]![0]!["requirements"]![1]!["calls"] = new JsonArray(1, 2, 3, 4);
        var reviewer = response == "valid"
            ? new FakeChatProvider(Turn.Says(answer.ToJsonString()))
            : new FakeChatProvider(Turn.Says(bad.ToJsonString()),
                Turn.Says((response == "corrected" ? answer : bad).ToJsonString()));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewContent: false, reviewRetries: 1),
            "Demonstrate a failing command, restore it and run successfully.");
        Assert.Equal(response == "uncorrected" ? RunOutcomeKind.Incomplete : RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(6, worker.Requests.Count);
        Assert.Equal(response == "valid" ? 1 : 2, reviewer.Requests.Count);
        if (response != "valid")
        {
            var correction = reviewer.Requests[1].Messages.Last().Content!;
            foreach (var index in new[] { 0, 2, 3 })
                Assert.Contains($"$.claims[0].requirements[1].calls[{index}]", correction);
            Assert.DoesNotContain("$.claims[0].requirements[1].calls[1]", correction);
        }
        Assert.Contains("exit", fx.Read(script)); // The worker's restored file survives an incomplete review.
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ReviewFailed or EventKind.ArtifactReverted);
        Assert.Contains("[process exit=1]", reviewer.Requests[0].Messages[1].Content!);
    }

    private static JsonNode Answer()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("restored tests pass", 4), "run").Text!)!;
        var parts = answer["claims"]![0]!["requirements"]!.AsArray();
        var negative = parts[0]!.DeepClone();
        negative["requirement"] = "Observe requested negative test";
        negative["shown"] = "expected-failure";
        negative["calls"] = new JsonArray(2);
        negative["what"] = "The requested negative command returned exit 1 before restoration";
        parts.Add(negative);
        return answer;
    }

    [Fact]
    public async Task Historical_constraint_violation_cannot_disappear_in_a_retry()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"restore","steps":[]}"""),
            Turn.Calls1("write_file", """{"path":"a.txt","content":"original"}"""),
            Turn.Calls1("run_command", JsonSerializer.Serialize(new {
                command = OperatingSystem.IsWindows() ? "del a.txt" : "rm a.txt"
            })),
            Turn.Calls1("write_file", """{"path":"a.txt","content":"restored"}"""), Turn.Says("Restored"));
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("Restoration was necessary", 3), "run").Text!)!;
        var part = answer["claims"]![0]!["requirements"]![0]!;
        part["requirement"] = "No file deletion";
        part["global"] = true;
        part["prohibitions"] = new JsonArray("file-deletion");
        var reviewer = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewContent: false, reviewRetries: 2), "Restore a.txt without deleting files.");
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(5, worker.Requests.Count);
        Assert.Single(reviewer.Requests);
        Assert.Equal("restored", fx.Read("a.txt"));
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ArtifactReverted);
    }

    [Theory]
    [InlineData("delete_file", "{\"path\":\"Intervals.cs\"}", true)]
    [InlineData("delete_file", "{\"path\":\".enactive/scratch/backup.cs\"}", true)]
    [InlineData("run_command", "{\"command\":\"del Intervals.cs\"}", true)]
    [InlineData("run_command", "{\"command\":\"rm Intervals.cs\"}", true)]
    [InlineData("run_powershell", "{\"script\":\"Remove-Item -LiteralPath Intervals.cs\"}", true)]
    [InlineData("run_powershell", "{\"script\":\"Remove-Item Intervals.cs -WhatIf\"}", false)]
    [InlineData("run_command", "{\"command\":\"echo del Intervals.cs\"}", false)]
    [InlineData("run_command", "{\"command\":\"del /?\"}", false)]
    [InlineData("run_command", "{\"command\":\"rm --help\"}", false)]
    [InlineData("write_file", "{\"path\":\"Intervals.cs\",\"content\":\"del Intervals.cs\"}", false)]
    public async Task Approval_and_restoration_do_not_override_recorded_deletion(string tool, string arguments, bool deletion)
    {
        var tools = new ToolRegistry(EngineFixture.ShippedTools());
        var journal = new ExecutionJournal();
        var accounting = new ToolResultAccounting(tools, new(tools.Definitions), new(tools.Definitions),
            new(), journal, new(), false, 1, ToolCallOrigin.Native);
        accounting.Record(new("delete", tool, arguments), new(ToolResults.Ok("operation completed"), 0, 1));
        Assert.Equal(deletion, journal.Actions[0].FileDeletion);
        journal.Record(1, "write_file", "restore", ActionOutcome.Succeeded, "restored");
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("Restoration makes the deletion acceptable", 2), "run").Text!)!;
        var part = answer["claims"]![0]!["requirements"]![0]!;
        part["requirement"] = "Do not delete files";
        part["global"] = true;
        part["prohibitions"] = new JsonArray("file-deletion");
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("restore", "done", journal.Describe(), [], [],
            RequestObligations.Create("Do not delete files."), provider, "strong", default);
        Assert.Equal(deletion, result.IncompleteReason is not null);
        Assert.Equal(!deletion, result.Soundness!.Sound);
        if (deletion) Assert.Contains("prohibition", result.Soundness.Reason);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public void Hidden_deletion_still_violates_constraint_but_refusal_does_not()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "delete_file", "old.cs", ActionOutcome.Succeeded, "deleted", fileDeletion: true);
        for (var i = 0; i < 100; i++) journal.Record(1, "read_file", "a.cs", ActionOutcome.Succeeded, "contents");
        var evidence = journal.Describe(maxChars: 1200);
        Assert.DoesNotContain(1, evidence.VisibleActionIds);
        var content = new ProofClaim(ProofClaimKind.NotByAnyCall, [], "Constraint respected");
        var claims = new[] { new ObligationClaim("O001", "run", content) { Requirements = [
            new("No file deletion", "run", true, content) { Prohibitions = ["file-deletion"] }
        ] } };
        Assert.False(ObligationAudit.Check(RequestObligations.Create("No file deletion"), claims, evidence).Sound);
        var refused = new ExecutionJournal();
        refused.Record(1, "delete_file", "old.cs", ActionOutcome.Refused, "denied", fileDeletion: true);
        Assert.True(ObligationAudit.Check(RequestObligations.Create("No file deletion"), claims, refused.Describe()).Sound);
    }
}

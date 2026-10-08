namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The planner's verification contract, refused by validation, is written down with what it was
/// checked against, and replaying that record reproduces the refusal.
///
/// <para><b>Why.</b> Twice on 2026-09-28 a run ended before any work started with "Invalid
/// verification contract: Locked criterion omitted or changed" - after two answers from the planner,
/// each taking seconds, and after the plan before them. Only the combined review kept its refusals;
/// this one left a log line. A refusal that can be replayed offline can be measured, and a fix to
/// it can be held to by the build.</para>
/// </summary>
public sealed class ARefusedPlanContractReplaysTests(ITestOutputHelper output)
{
    private const string Request = "Run verify and do not delete any file.";

    private static JsonObject Answer() => new()
    {
        ["sources"] = new JsonArray(new JsonObject { ["id"] = "O001", ["assessment"] = "Final verification and a ban" }),
        ["checks"] = new JsonArray(),
        ["forbidden_effects"] = new JsonArray(),
        ["action_policy"] = null,
        ["unresolved"] = null
    };

    private static JsonObject Check(string command, string origin, string? quote) => new()
    {
        ["name"] = "verify", ["command"] = command, ["origin"] = origin, ["request_quote"] = quote,
        ["expectedExitCode"] = 0, ["reason"] = "Final check"
    };

    /// <summary>A defect per input the validator reads, so a record that lost any one of them replays differently.</summary>
    private static (PlanResult Plan, bool Preserve, Turn Turn) Case(string defect)
    {
        var plan = new PlanResult(IntentDisposition.QuickAction, "work", null);
        var answer = Answer();
        var preserve = false;
        var finish = "stop";
        switch (defect)
        {
            case "missing-source": answer["sources"] = new JsonArray(); break;                 // the request
            case "invented-quote": answer["checks"] = new JsonArray(Check("invented", "requested", "Run invented.")); break;
            case "truncated": finish = "length"; break;                                         // completeness
            case "locked-changed":                                                              // the plan's checks, locked
                plan = plan with { Checks = [new SuccessCriterionDefinition("verify", "verify")] };
                preserve = true;
                answer["checks"] = new JsonArray(Check("other", "declared", null));
                break;
            case "restriction-removed":                                                         // the plan's restrictions
                plan = plan with { Restrictions = [new(ForbiddenTaskEffect.FileDeletion, "do not delete any file")] };
                break;
            case "unknown-tool":                                                                // the tool inventory
                answer["action_policy"] = new JsonObject {
                    ["allowed_tools"] = new JsonArray("no_such_tool"), ["command_prefixes"] = new JsonArray("verify"),
                    ["source_quote"] = "Run verify", ["reason"] = "only verify" };
                break;
            case "policy-changed":                                                              // the plan's action policy
                plan = plan with { ActionPolicy = new(["read_file"], [], "Run verify", "only reading") };
                break;
        }
        return (plan, preserve, Turn.Says(answer.ToJsonString()) with { FinishReason = finish });
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A real contract review, refused by the real validation, is recorded by
    /// the real recording path; each record, read back and replayed, produces the refusal it was
    /// recorded with - and that refusal is what the run ended with.
    /// </summary>
    [Theory]
    [InlineData("missing-source")]
    [InlineData("invented-quote")]
    [InlineData("truncated")]
    [InlineData("locked-changed")]
    [InlineData("restriction-removed")]
    [InlineData("unknown-tool")]
    [InlineData("policy-changed")]
    public async Task A_recorded_refusal_replays_to_the_same_refusal(string defect)
    {
        var root = Directory.CreateTempSubdirectory("plan-check-corpus").FullName;
        try
        {
            var (plan, preserve, turn) = Case(defect);
            var result = await PlanCheckReview.RunAsync(plan, Request, new WorkContext(null, "workspace", null, null, null, [], []),
                new FakeChatProvider(turn, turn), "plan-model", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default,
                preserveCriteria: preserve, tools: EngineFixture.ShippedTools().Select(t => t.Definition).ToArray(),
                workspaceRoot: root);

            var files = Directory.GetFiles(Path.Combine(root, PlanCheckCorpus.Folder), "*.json");
            Assert.Equal(2, files.Length);   // both answers were refused, and each is a case
            foreach (var file in files)
            {
                var recorded = PlanCheckCorpus.Read(file);
                Assert.Equal("plan-model", recorded.Model);
                Assert.Equal(recorded.Refusal, PlanCheckCorpus.Replay(recorded));
                Assert.Equal("The verification contract could not be used: " + recorded.Refusal, result.IncompleteReason);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>An accepted contract - and one the planner declined with a reason - is not a refusal, and is not kept.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_accepted_contract_is_not_recorded(bool unresolved)
    {
        var root = Directory.CreateTempSubdirectory("plan-check-corpus").FullName;
        try
        {
            var answer = Answer();
            if (unresolved) answer["unresolved"] = "the request conflicts with itself";
            await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null), Request,
                new WorkContext(null, "workspace", null, null, null, [], []), new FakeChatProvider(Turn.Says(answer.ToJsonString())),
                "plan-model", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, workspaceRoot: root);

            Assert.False(Directory.Exists(Path.Combine(root, PlanCheckCorpus.Folder)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// The corpus in the repository, held to its <c>expected.json</c>: refusals taken from real
    /// runs, replayed against today's validation. See <see cref="CorpusRatchet"/>.
    /// </summary>
    [Fact]
    public void Every_refusal_in_the_corpus_still_replays()
        => CorpusRatchet.Hold(CorpusRatchet.RepositoryFolder("plan-check-corpus"),
            path => PlanCheckCorpus.Replay(PlanCheckCorpus.Read(path)), output);
}

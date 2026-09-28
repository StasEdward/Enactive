namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class ToolResultAccountingTests
{
    [Fact]
    public void Failure_evidence_keeps_output_and_success_closes_it_without_gating_the_current_batch()
    {
        var tools = new ToolRegistry(EngineFixture.ShippedTools());
        var failures = new OpenFailures(tools.Definitions);
        var progress = new StepProgress(tools.Definitions);
        var journal = new ExecutionJournal();
        var accounting = new ToolResultAccounting(tools, progress, failures, new(), journal, new(), true, 2,
            ToolCallOrigin.Native);
        var call = new ToolCall("id", "run_command", """{"command":"build"}""");
        progress.BeginTurn();
        var failure = accounting.Record(call, new(ToolResults.Fail("exit 1", "compiler diagnostic"), 3, 3));
        Assert.Equal("exit 1\ncompiler diagnostic", failure);
        Assert.Equal(failure, Assert.Single(journal.Actions).Output);
        Assert.Equal(ActionOutcome.Failed, journal.Actions[0].Outcome);
        Assert.Equal(1, failures.Count);
        accounting.Record(call, new(ToolResults.Ok("built") with { WorkspaceEffect = WorkspaceEffect.None }, 3, 3));
        Assert.Equal(0, failures.Count);
        Assert.False(progress.AlreadyRanExactly(call, 3));
        progress.BeginTurn();
        Assert.True(progress.AlreadyRanExactly(call, 3));
        Assert.False(progress.AlreadyRanExactly(call, 4));
        Assert.Equal(new[] { ActionOutcome.Failed, ActionOutcome.Succeeded }, journal.Actions.Select(a => a.Outcome));
    }

    [Fact]
    public async Task Read_result_reaches_the_partial_write_guard()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "one\ntwo\nthree");
        var tools = new ToolRegistry(EngineFixture.ShippedTools());
        var reads = new ReadLedger();
        var journal = new ExecutionJournal();
        var accounting = new ToolResultAccounting(tools, new(tools.Definitions), new(tools.Definitions),
            reads, journal, new(), false, null, ToolCallOrigin.Native);
        var call = new ToolCall("read", "read_file", """{"path":"a.txt","offset":1,"limit":1}""");
        var result = await fx.Invoke(new ReadFileTool(), call.ArgumentsJson);
        Assert.True(result.Success, result.Error);
        accounting.Record(call, new(result, 1, 1));
        Assert.NotNull(reads.Refuse(new("write", "write_file", """{"path":"a.txt","content":"one"}"""),
            "a.txt", tools.DefinitionOf("write_file"), true));
        Assert.Equal(result.Output, Assert.Single(journal.Actions).Output);
    }
}

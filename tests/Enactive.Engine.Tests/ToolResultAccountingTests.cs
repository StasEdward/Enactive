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
        using var fx = new EngineFixture();
        var tools = new ToolRegistry(EngineFixture.ShippedTools());
        var journal = new ExecutionJournal();
        var frame = new StepFrame(Guid.NewGuid(), Guid.NewGuid(), 2, fx.Workspace, tools.Definitions, [], journal, new(), fx.Artifacts.BeginStep());
        var failures = frame.Open;
        var progress = frame.Progress;
        var accounting = new ToolResultAccounting(frame, tools, new(), true, ToolCallOrigin.Native);
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
        var frame = new StepFrame(Guid.NewGuid(), Guid.NewGuid(), null, fx.Workspace, tools.Definitions, [], journal, reads, fx.Artifacts.BeginStep());
        var accounting = new ToolResultAccounting(frame, tools, new(), false, ToolCallOrigin.Native);
        var call = new ToolCall("read", "read_file", """{"path":"a.txt","offset":1,"limit":1}""");
        var result = await fx.Invoke(new ReadFileTool(), call.ArgumentsJson);
        Assert.True(result.Success, result.Error);
        accounting.Record(call, new(result, 1, 1));
        Assert.NotNull(reads.Refuse(new("write", "write_file", """{"path":"a.txt","content":"one"}"""),
            "a.txt", tools.DefinitionOf("write_file"), true));
        Assert.Equal(result.Output, Assert.Single(journal.Actions).Output);
    }

    /// <summary>
    /// A file a call created inside a step's boundary is the step's from then on - told to the boundary by the accounting of
    /// the call's result, where the tool loop used to do it itself beside the accounting.
    /// </summary>
    [Fact]
    public async Task A_file_a_call_created_is_the_step_s_once_its_result_is_recorded()
    {
        using var fx = new EngineFixture();
        var tools = new ToolRegistry(EngineFixture.ShippedTools());
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundary = new WriteBoundary(fx.Root, [], ["wiki/a.md"], () => owned, p => owned.Add(p));
        var frame = new StepFrame(Guid.NewGuid(), Guid.NewGuid(), 1, fx.Workspace, tools.Definitions, [], new(), new(),
            fx.Artifacts.BeginStep(), boundary: boundary);
        var create = new ToolCall("n1", "write_file", """{"path":"notes/a.md","content":"x"}""");
        Assert.Null(boundary.Refuse(create, tools.DefinitionOf("write_file"), _ => false));

        new ToolResultAccounting(frame, tools, new(), false, ToolCallOrigin.Native)
            .Record(create, new(await fx.Invoke(new WriteFileTool(), create.ArgumentsJson), 1, 1));

        Assert.Contains("notes/a.md", owned);
    }
}

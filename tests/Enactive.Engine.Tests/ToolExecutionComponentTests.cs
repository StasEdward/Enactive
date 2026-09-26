namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;

public sealed class ToolExecutionComponentTests
{
    private static readonly ToolCall Call = new("id", "probe", "{}");
    private static readonly ToolOffer Offered = new(["probe"], []);

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void Parallel_admission_preserves_role_approval_and_withholding(bool approval, bool withheld, bool emptyRole, bool expected)
    {
        var tools = new ToolRegistry([new Probe(approval)]);
        var access = new ToolAccess(tools, new PermissionEngine());
        var worker = EngineFixture.Role("developer") with { ToolAllowlist = emptyRole ? [] : ["probe"] };
        var offer = withheld ? new ToolOffer([], [new("probe", "unattended")]) : Offered;
        Assert.Equal(expected, access.CanRunRead(Call, worker, PermissionPolicy.PermissiveDefault, offer));
        Assert.Equal(withheld ? PermissionDecision.Deny : approval ? PermissionDecision.Ask : PermissionDecision.Allow,
            access.Evaluate(PermissionPolicy.PermissiveDefault, "probe", offer));
    }

    [Fact]
    public void Approval_binds_the_complete_call_and_shell_approval_is_session_only()
    {
        var access = new ToolAccess(new ToolRegistry([new RunCommandTool()]), new PermissionEngine());
        var call = new ToolCall("call", "run_command", "{\"command\":\"" + new string('x', 5000) + "TAIL\"}");
        var run = Guid.NewGuid();
        var task = Guid.NewGuid();
        var request = access.Approval(call, task, run, "workspace");
        Assert.Equal(task, request.TaskId);
        Assert.Equal(new BoundAction(run, call.Id, call.Name, call.ArgumentsJson, "workspace"), request.Action);
        Assert.Contains("TAIL", request.FullText);
        Assert.True(request.SessionOnly);
        Assert.Equal("run_command", request.Subject);
        var alwaysAsk = new ToolAccess(new ToolRegistry([new Probe(true)]), new PermissionEngine());
        Assert.Null(alwaysAsk.Approval(Call, task, run, "workspace").Subject);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_release_another_approval_and_failure_releases_its_own_gate()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource();
        var handler = new Handler();
        var request = new DecisionRequest(Guid.NewGuid(), "topic", "detail", [], null);
        var waiting = ToolAccess.AskAsync(handler, gate, request, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.CurrentCount);
        Assert.Equal(0, handler.Calls);
        gate.Release();
        await Assert.ThrowsAsync<IOException>(() => ToolAccess.AskAsync(handler, gate, request, default));
        Assert.Equal(1, gate.CurrentCount);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Invocation_reports_failures_but_propagates_cancellation()
    {
        using var fx = new EngineFixture();
        var failed = await ToolInvocation.ExecuteAsync(Call, new ToolRegistry([new Probe(false, new IOException("broken"))]), fx.ContextFor(), default);
        Assert.False(failed.Value.Success);
        Assert.Contains("probe threw: broken", failed.Value.Error);
        Assert.True(failed.After > failed.Before); // An exception has unknown effects; the registry invalidates cached state.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolInvocation.ExecuteAsync(Call,
            new ToolRegistry([new Probe(false, new OperationCanceledException())]), fx.ContextFor(), default));
    }

    [Fact]
    public async Task Invocation_captures_revision_before_and_after_a_real_write()
    {
        using var fx = new EngineFixture();
        var tools = new ToolRegistry([new WriteFileTool()]);
        var result = await ToolInvocation.ExecuteAsync(new("write", "write_file", """{"path":"a.txt","content":"A"}"""),
            tools, fx.ContextFor(), default);
        Assert.True(result.Value.Success, result.Value.Error);
        Assert.True(result.After > result.Before);
        Assert.Equal("A", fx.Read("a.txt"));
    }

    private sealed class Handler : IDecisionHandler
    {
        public int Calls;
        public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        { Calls++; throw new IOException("handler failed"); }
    }
    private sealed class Probe(bool approval, Exception? error = null) : ITool
    {
        public ToolDefinition Definition => new("probe", "read", "{}", WorkspaceEffect: WorkspaceEffect.None, ParallelRead: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public bool RequiresApproval => approval;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
            => error is null ? Task.FromResult(ToolResults.Ok("ok")) : Task.FromException<ToolResult>(error);
    }
}

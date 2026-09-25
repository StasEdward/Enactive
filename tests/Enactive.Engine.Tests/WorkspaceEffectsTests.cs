namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Diagnostics;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Tools.Mcp;

public sealed class WorkspaceEffectsTests
{
    private const string Quick = """{"disposition":"quick_action","title":"check files"}""";
    private static Turn Observe(string id) => Turn.Calls1("git", """{"args":["status"]}""", id);

    private static Observer InstallObserver(EngineFixture fx)
    {
        var observer = new Observer();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "git").Append(observer).ToArray();
        return observer;
    }

    [Theory]
    [InlineData("delete_file", false)]
    [InlineData("delete_file", true)]
    [InlineData("copy_file", false)]
    [InlineData("copy_file", true)]
    [InlineData("run_command", false)]
    [InlineData("run_command", true)]
    public async Task A_mutation_invalidates_an_earlier_observation(string tool, bool sameBatch)
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";
        fx.Write("source.txt", "changed");
        if (tool != "copy_file") fx.Write("note.txt", "old");
        var observer = InstallObserver(fx);
        var args = tool switch
        {
            "delete_file" => """{"path":"note.txt"}""",
            "copy_file" => """{"from":"source.txt","to":"note.txt"}""",
            _ => """{"command":"echo changed>note.txt"}"""
        };
        var turns = new List<Turn> { Turn.Says(Quick), Observe("o1") };
        var mutation = new ToolCall("m1", tool, args);
        if (sameBatch) turns.Add(new Turn(Calls: new[] { mutation, Observe("o2").Calls![0] }));
        else
        {
            turns.Add(new Turn(Calls: new[] { mutation }));
            turns.Add(Observe("o2"));
        }
        turns.Add(Turn.Says("Done."));
        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(turns.ToArray()), EngineFixture.Role("developer")), "check files");

        Assert.Equal(new[] { tool == "copy_file" ? "missing" : "old", tool == "delete_file" ? "missing" : "changed" }, observer.Seen);
        Assert.DoesNotContain(events, e => e.Summary.Contains("refused — an exact repeat"));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_known_observer_is_gated_unless_forced(bool force)
    {
        using var fx = new EngineFixture();
        var observer = InstallObserver(fx);
        var provider = new FakeChatProvider(Turn.Says(Quick), Observe("o1"),
            Turn.Calls1("git", force ? """{"args":["status"],"force":true}""" : """{"args":["status"]}""", "o2"),
            Turn.Says("Done."));
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check files");
        Assert.Equal(force ? 2 : 1, observer.Seen.Count);
        Assert.Equal(force ? 0 : 1, events.Count(e => e.Summary.Contains("refused — an exact repeat")));
    }

    [Fact]
    public async Task Unknown_shell_effects_do_not_manufacture_stall_progress()
    {
        using var fx = new EngineFixture();
        var repeat = Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""");
        var provider = new FakeChatProvider(Turn.Says(Quick), repeat) { WhenExhausted = repeat };
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check files");
        Assert.Equal(3, events.Count(e => e.Kind == EventKind.ToolInvoked));
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("repeating tool calls"));
    }

    [Fact]
    public async Task Alternating_copy_and_delete_does_not_manufacture_endless_progress()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";
        fx.Write("source.txt", "content");
        InstallObserver(fx);
        var repeat = new Turn(Calls: new[]
        {
            new ToolCall("c", "copy_file", """{"from":"source.txt","to":"note.txt"}"""),
            new ToolCall("d", "delete_file", """{"path":"note.txt"}"""),
            Observe("o").Calls![0]
        });
        var provider = new FakeChatProvider(Turn.Says(Quick), repeat) { WhenExhausted = repeat };
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check files");
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("repeating tool calls"));
        Assert.InRange(events.Count(e => e.Kind == EventKind.ToolInvoked), 3, 15);
    }

    [Fact]
    public async Task Results_in_a_batch_only_gate_later_model_turns()
    {
        using var fx = new EngineFixture();
        var observer = InstallObserver(fx);
        var provider = new FakeChatProvider(Turn.Says(Quick),
            new Turn(Calls: new[] { Observe("o1").Calls![0], Observe("o2").Calls![0] }),
            Observe("o3"), Turn.Says("Done."));
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check files");
        Assert.Equal(2, observer.Seen.Count);
        Assert.Single(events, e => e.Summary.Contains("refused — an exact repeat"));
    }

    [Fact]
    public async Task Failed_shell_writes_also_invalidate_observations()
    {
        using var fx = new EngineFixture();
        fx.Write("note.txt", "old");
        var observer = InstallObserver(fx);
        var provider = new FakeChatProvider(Turn.Says(Quick), Observe("o1"),
            Turn.Calls1("run_command", """{"command":"echo changed>note.txt & exit /b 1"}"""),
            Observe("o2"), Turn.Says("Failed."));
        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check files");
        Assert.Equal(new[] { "old", "changed" }, observer.Seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Versions_are_shared_across_calls_and_unavailable_during_unknown_effects(bool throws)
    {
        using var fx = new EngineFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new PendingTool(entered, release, throws);
        var registry = new ToolRegistry(new[] { tool });
        var context = fx.ContextFor();
        var before = registry.WorkspaceVersion(context.WorkspaceId);
        var running = registry.InvokeAsync(new("p", "pending", "{}"), context, CancellationToken.None);
        await entered.Task;
        Assert.Null(registry.WorkspaceVersion(context.WorkspaceId));
        Assert.Equal(0L, registry.WorkspaceVersion(Guid.NewGuid()));
        release.SetResult();
        if (throws)
            await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        else
            Assert.Equal(WorkspaceEffect.Unknown, (await running).WorkspaceEffect);
        Assert.True(registry.WorkspaceVersion(context.WorkspaceId) > before);
    }

    [Fact]
    public async Task Logging_and_Mcp_wrappers_preserve_the_shared_revision_and_effects()
    {
        using var fx = new EngineFixture();
        fx.Write("note.txt", "content");
        var registry = new ToolRegistry(EngineFixture.ShippedTools());
        var logged = new LoggingToolRegistry(registry, NullLogSink.Instance);
        await using var combined = await McpRunTools.ConnectAsync(logged, [], fx.Root, CancellationToken.None);
        var context = fx.ContextFor();
        var before = combined.WorkspaceVersion(context.WorkspaceId);
        var read = await combined.InvokeAsync(new("r", "read_file", """{"path":"note.txt"}"""), context, CancellationToken.None);
        Assert.Equal(WorkspaceEffect.None, read.WorkspaceEffect);
        Assert.Equal(before, combined.WorkspaceVersion(context.WorkspaceId));
        var deleted = await combined.InvokeAsync(new("d", "delete_file", """{"path":"note.txt"}"""), context, CancellationToken.None);
        Assert.True(deleted.Success);
        Assert.Equal(WorkspaceEffect.Changed, deleted.WorkspaceEffect);
        Assert.True(combined.WorkspaceVersion(context.WorkspaceId) > before);
        Assert.Equal(registry.WorkspaceVersion(context.WorkspaceId), logged.WorkspaceVersion(context.WorkspaceId));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("move_file")]
    [InlineData("create_directory")]
    [InlineData("copy_file")]
    [InlineData("delete_file")]
    public void File_mutators_declare_effects_separately_from_stall_identity(string name)
        => Assert.Equal(WorkspaceEffect.Changed, EngineFixture.ToolNamed(name).Definition.WorkspaceEffect);

    private sealed class Observer : ITool
    {
        public List<string> Seen { get; } = new();
        public ToolDefinition Definition { get; } = new("git", "A controlled workspace observer", "{}", WorkspaceEffect.None);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            var file = Path.Combine(ctx.WorkspaceRoot, "note.txt");
            var value = File.Exists(file) ? File.ReadAllText(file).Trim() : "missing";
            Seen.Add(value);
            return Task.FromResult(ToolResults.Ok(value));
        }
    }

    private sealed class PendingTool(TaskCompletionSource entered, TaskCompletionSource release, bool throws) : ITool
    {
        public ToolDefinition Definition { get; } = new("pending", "unknown effects", "{}");
        public PermissionLevel RequiredLevel => PermissionLevel.Execute;
        public async Task<ToolResult> InvokeAsync(string args, ToolContext ctx, CancellationToken ct)
        {
            entered.SetResult();
            await release.Task;
            if (throws) throw new InvalidOperationException("failed after possible effects");
            return ToolResults.Ok();
        }
    }
}

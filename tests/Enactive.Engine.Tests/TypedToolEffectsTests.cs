namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class TypedToolEffectsTests
{
    [Theory]
    [InlineData("plugin_mutation", WorkspaceEffect.Changed, false)]
    [InlineData("write_file", WorkspaceEffect.None, true)]
    [InlineData("run_command", WorkspaceEffect.Unknown, true)]
    public async Task Recorded_effect_not_tool_name_decides_project_change(string name, WorkspaceEffect effect, bool sound)
    {
        using var fx = new EngineFixture();
        var registry = new ToolRegistry(new[] { new EffectTool(name, effect) });
        var result = await registry.InvokeAsync(new("id", name, "{}"), fx.ContextFor(), default);
        var journal = new ExecutionJournal();
        journal.Record(1, name, "{}", ActionOutcome.Succeeded, "ok", result.WorkspaceEffect!.Value, result.ChangedPaths);
        var verdict = ProofAudit.Check(new(ProofClaimKind.NothingToDo, new[] { 1 }, "nothing to do"),
            journal.Describe(), fx.Root);
        Assert.Equal(sound, verdict.Sound);
        Assert.Equal(effect == WorkspaceEffect.None ? 0 : 2, registry.WorkspaceVersion(fx.Workspace.Id));
    }

    [Theory]
    [InlineData(".enactive/scratch/helper.txt", false)]
    [InlineData("project.txt", true)]
    public async Task Declared_destination_paths_drive_scope_independently_of_name(string destination, bool projectChanged)
    {
        using var fx = new EngineFixture();
        var tool = new EffectTool("custom_copy", WorkspaceEffect.Changed);
        var registry = new ToolRegistry(new[] { tool });
        var args = System.Text.Json.JsonSerializer.Serialize(new { from = "unrelated.txt", to = destination });
        var result = await registry.InvokeAsync(new("id", "custom_copy", args), fx.ContextFor(), default);
        Assert.Equal(new[] { destination }, result.ChangedPaths);
        Assert.Equal(projectChanged, ToolEffects.ChangesProject(result.WorkspaceEffect!.Value, result.ChangedPaths, fx.Root));
        Assert.Equal(ProgressIdentity.Observation, tool.Definition.ProgressIdentity);
        Assert.False(tool.Definition.RepairsFileFailures);
    }

    [Fact]
    public void Failed_but_explicitly_changed_result_cannot_claim_nothing_to_do()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "reader", "{}", ActionOutcome.Succeeded, "ok", WorkspaceEffect.None);
        journal.Record(1, "partial_write", "{}", ActionOutcome.Failed, "failed after writing", WorkspaceEffect.Changed);
        Assert.False(ProofAudit.Check(new(ProofClaimKind.NothingToDo, new[] { 1 }, "unchanged"), journal.Describe()).Sound);
    }

    private sealed class EffectTool(string name, WorkspaceEffect effect) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "test", "{}", effect, ChangedPathArguments: ["to"]);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResults.Ok("ok"));
    }
}

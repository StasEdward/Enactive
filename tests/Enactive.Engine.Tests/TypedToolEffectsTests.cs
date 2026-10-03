namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class TypedToolEffectsTests
{

    [Theory]
    [InlineData(".enactive/scratch/helper.txt")]
    [InlineData("project.txt")]
    public async Task Declared_destination_paths_are_reported_independently_of_name(string destination)
    {
        using var fx = new EngineFixture();
        var tool = new EffectTool("custom_copy", WorkspaceEffect.Changed);
        var registry = new ToolRegistry(new[] { tool });
        var args = System.Text.Json.JsonSerializer.Serialize(new { from = "unrelated.txt", to = destination });
        var result = await registry.InvokeAsync(new("id", "custom_copy", args), fx.ContextFor(), default);
        Assert.Equal(new[] { destination }, result.ChangedPaths);
        Assert.Equal(WorkspaceEffect.Changed, result.WorkspaceEffect);
        Assert.Equal(ProgressIdentity.Observation, tool.Definition.ProgressIdentity);
        Assert.False(tool.Definition.RepairsFileFailures);
    }

    private sealed class EffectTool(string name, WorkspaceEffect effect) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "test", "{}", effect, ChangedPathArguments: ["to"]);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResults.Ok("ok"));
    }
}

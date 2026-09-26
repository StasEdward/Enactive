namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Providers;
using Enactive.Settings;
using Xunit;

public sealed class RolePromptTests
{
    [Theory]
    [InlineData("reviewer", false, false)]
    [InlineData("writer", false, true)]
    [InlineData("ops", true, false)]
    [InlineData("developer", true, true)]
    public void Built_in_roles_receive_only_relevant_operational_guidance(string id, bool commands, bool writes)
    {
        var worker = DefaultWorkers.Build(new ModelRef("test", "model")).Single(w => w.Id == id);
        var text = worker.Instructions;
        Assert.Contains("Never invent", text);
        Assert.Contains("go TOGETHER", text);
        Assert.Equal(commands, text.Contains("Read command results before describing them"));
        Assert.Equal(writes, text.Contains("After writing a file"));
        Assert.Equal(commands && writes, text.Contains("To save requested command output"));
        if (id == "reviewer")
        {
            Assert.DoesNotContain("run_powershell", text);
            Assert.DoesNotContain(".enactive/scratch", text);
            Assert.True(Words(text) < 250, $"Read-only role grew to {Words(text)} words.");
        }
    }

    [Fact]
    public void Saved_custom_role_uses_tools_not_role_name_and_preserves_user_instructions()
    {
        const string instructions = "Мои правила\r\nKeep this verbatim.";
        const string global = "Global: \"quotes\" and пустые строки\n\n";
        var config = new WorkerConfig { Id = "developer", Role = "Custom", Instructions = instructions,
            Tools = ["read_file"], Model = "test/model" };
        var settings = new AppSettings { Workers = [config], GlobalInstructions = global, VerifyWrites = true };
        settings.Providers.Clear();
        settings.Providers.Add(new ProviderConfig { Id = "test", Models = ["model"] });
        var worker = EngineComposition.Workers(settings).Default;
        Assert.StartsWith(instructions, worker.Instructions);
        Assert.EndsWith(global, worker.Instructions);
        Assert.DoesNotContain("run_powershell", worker.Instructions);
        Assert.DoesNotContain("After writing a file", worker.Instructions);
        Assert.Equal(instructions, config.Instructions);
        Assert.Equal(new[] { "read_file" }, config.Tools);
    }

    [Fact]
    public void Empty_or_mcp_only_allowlist_does_not_get_builtin_operational_advice()
    {
        foreach (string[] tools in new[] { Array.Empty<string>(), new[] { "mcp__*" } })
        {
            var text = DefaultWorkers.Augment("custom", tools: tools);
            Assert.DoesNotContain("run_command", text);
            Assert.DoesNotContain("read_file", text);
            Assert.DoesNotContain("After writing a file", text);
            Assert.Contains("Never invent", text);
        }
    }

    [Fact]
    public void Wildcard_and_implied_tools_keep_write_verification_and_full_log_advice()
    {
        foreach (string[] tools in new[] { new[] { "*" }, new[] { "read_file", "write_file", "run_command" } })
        {
            var text = DefaultWorkers.Augment("custom", tools: tools);
            Assert.Contains("To save requested command output", text);
            Assert.Contains("copy_file", text);
            Assert.Contains("After writing a file", text);
            Assert.DoesNotContain("After writing a file",
                DefaultWorkers.Augment("custom", verifyWrites: false, tools: tools));
        }
    }

    [Fact]
    public void Planning_and_execution_review_have_bounded_instruction_overhead()
    {
        var planner = Planner.SystemPromptFor(12, turnCeiling: 250);
        Assert.DoesNotContain("STRONGLY prefer", planner);
        Assert.True(Words(planner) < 350, $"Planner grew to {Words(planner)} words.");
        Assert.True(Words(Reviewer.ExecutionGuidance) < 330,
            $"Execution reviewer grew to {Words(Reviewer.ExecutionGuidance)} words.");
        // Output protocol stays separate from the shared guidance used by combined review.
        Assert.Contains("\"verdict\"", Reviewer.ExecutionSystemPrompt);
        Assert.DoesNotContain("\"verdict\"", Reviewer.ExecutionGuidance);
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

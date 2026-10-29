namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Bench test-coverage, 2026-10-29 (run 20261029-112041): the read-only "measure coverage" step swapped the project's
/// coverage package with <c>dotnet remove/add package</c> - a command, the one thing a read-only step's file tools cannot
/// refuse. The review failed it for the change; the step was refused when it edited the file back, being read-only; and
/// the rejected step left the project changed for the rest of the run. Now what a command changed of the files that were
/// there when the step began is put back right after it, as the request's limit is held after the fact - and what the
/// step made itself, and what a build rewrites, is left. Deliberately not code: invoices.
/// </summary>
public sealed class AReadOnlyStepsCommandIsPutBackTests
{
    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Check the invoice totals","dependsOn":[],"readOnly":true},
                  {"title":"Correct the wrong totals","dependsOn":[0]}]}
        """;

    /// <summary>A command that rewrites a file that was there, writes a new one, and rewrites build output.</summary>
    private sealed class Rewriting : ITool
    {
        public ToolDefinition Definition => new("run_command", "runs a command", "{\"type\":\"object\"}", Kind: ToolKind.Command);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(ctx.WorkspaceRoot, "invoices", "a.txt"), "total: 12");
            File.WriteAllText(Path.Combine(ctx.WorkspaceRoot, "invoices", "check.log"), "a.txt off by 2");
            File.WriteAllText(Path.Combine(ctx.WorkspaceRoot, "obj", "cache.bin"), "rebuilt");
            return Task.FromResult(new ToolResult(true, "exit 0", null, [], new Dictionary<string, object?> { ["exitCode"] = 0 }));
        }
    }

    private static async Task<(List<WorkEvent> Events, EngineFixture Fx)> Run(Turn[] firstStep, Turn[] secondStep)
    {
        var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";
        fx.Write("invoices/a.txt", "total: 10");
        fx.Write("obj/cache.bin", "built");
        // Under git, as a project usually is: a file is put back byte for byte from what git kept of it (IWorkspaceChanges).
        TestGit.Repository(fx.Root);
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(new Rewriting()).ToArray();
        var worker = new FakeChatProvider([Turn.Says(Plan), .. firstStep, .. secondStep]);
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check the invoices and correct the totals");
        return (events, fx);
    }

    [Fact]
    public async Task In_a_read_only_step_a_command_s_change_to_a_file_that_was_there_is_put_back_and_its_own_files_are_left()
    {
        var (events, fx) = await Run(
            [Turn.Calls1("run_command", """{"command":"fix"}""", "c1"), Turn.Says("a.txt: the total should be 12.")],
            [Turn.Says("Nothing left to correct.")]);
        using var _ = fx;

        Assert.Equal("total: 10", fx.Read("invoices/a.txt"));
        Assert.Equal("a.txt off by 2", fx.Read("invoices/check.log"));
        Assert.Equal("rebuilt", fx.Read("obj/cache.bin"));
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains(
            "REFUSED after the fact: this command changed 'invoices/a.txt' - this step was planned as read-only", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.ArtifactReverted && e.Summary.Contains("Put back after a command: invoices/a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task In_a_step_that_changes_files_the_command_s_change_stands()
    {
        var (events, fx) = await Run(
            [Turn.Says("a.txt: the total should be 12.")],
            [Turn.Calls1("run_command", """{"command":"fix"}""", "c1"), Turn.Says("Corrected a.txt.")]);
        using var _ = fx;

        Assert.Equal("total: 12", fx.Read("invoices/a.txt"));
        Assert.DoesNotContain(events, e => e.Summary.Contains("REFUSED after the fact", StringComparison.Ordinal));
    }
}

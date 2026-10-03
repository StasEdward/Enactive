namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run aab59d, 2026-10-03: a step planned read-only made two files with a command - the one thing its file tools
/// cannot refuse. The review rejected it and said the files must go; the step tried to delete them and was
/// refused, because it is read-only; the review rejected it again, and the run failed with nothing wrong that
/// anybody was allowed to put right. A read-only step still changes nothing that was there before it. What it
/// made itself, it may take away. Deliberately not code: invoices, and a draft a command left behind.
/// </summary>
public sealed class AReadOnlyStepMayTakeAwayWhatItMadeTests
{
    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Check the invoice totals","dependsOn":[],"readOnly":true},
                  {"title":"Correct the wrong totals","dependsOn":[0]}]}
        """;

    /// <summary>A command: it changes the workspace without declaring what, so no rule can stop it beforehand.</summary>
    private sealed class Drafting : ITool
    {
        public ToolDefinition Definition => new("run_command", "runs a command", "{\"type\":\"object\"}", Kind: ToolKind.Command);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.Combine(ctx.WorkspaceRoot, "drafts"));
            File.WriteAllText(Path.Combine(ctx.WorkspaceRoot, "drafts", "corrected.txt"), "total: 12");
            return Task.FromResult(new ToolResult(true, "exit 0", null, [], new Dictionary<string, object?> { ["exitCode"] = 0 }));
        }
    }

    private static async Task<(List<WorkEvent> Events, EngineFixture Fx)> Run(params Turn[] firstStep)
    {
        var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";
        fx.Write("invoices/a.txt", "total: 10");
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(new Drafting()).ToArray();
        var worker = new FakeChatProvider([Turn.Says(Plan), .. firstStep, Turn.Says("a.txt: the total should be 12."), Turn.Says("Nothing left to correct.")]);
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check the invoices and correct the totals");
        return (events, fx);
    }

    private static bool Result(List<WorkEvent> events, string start)
        => events.Any(e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith(start, StringComparison.Ordinal));

    [Fact]
    public async Task What_the_step_made_with_a_command_it_may_delete()
    {
        var (events, fx) = await Run(
            Turn.Calls1("run_command", """{"command":"draft"}""", "c1"),
            Turn.Calls1("delete_file", """{"path":"drafts/corrected.txt"}""", "d1"));
        using var _ = fx;

        Assert.True(Result(events, "delete_file -> ok"), events.Text());
        Assert.False(fx.Exists("drafts/corrected.txt"));
    }

    [Fact]
    public async Task What_was_there_before_the_step_it_still_may_not()
    {
        var (events, fx) = await Run(
            Turn.Calls1("run_command", """{"command":"draft"}""", "c1"),
            Turn.Calls1("delete_file", """{"path":"invoices/a.txt"}""", "d1"));
        using var _ = fx;

        Assert.True(Result(events, "delete_file -> refused: 'invoices/a.txt' was not changed: this step was planned as read-only"), events.Text());
        Assert.True(fx.Exists("invoices/a.txt"));
    }

    [Fact]
    public async Task And_it_may_not_go_on_working_on_what_it_made()
    {
        // Taking it away undoes the step's mistake; editing it would be doing a later step's work here.
        var (events, fx) = await Run(
            Turn.Calls1("run_command", """{"command":"draft"}""", "c1"),
            Turn.Calls1("edit_file", """{"path":"drafts/corrected.txt","old_string":"total: 12","new_string":"total: 13"}""", "e1"));
        using var _ = fx;

        Assert.True(Result(events, "edit_file -> refused: 'drafts/corrected.txt' was not changed: this step was planned as read-only"), events.Text());
        Assert.Equal("total: 12", fx.Read("drafts/corrected.txt"));
    }
}

namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Bench build-error, 2026-10-18: refused twice on edit_file, the worker rewrote the forbidden Report.cs with
/// <c>powershell ... -replace ... | Set-Content</c>. The request's change limit is asked only of tools that name their
/// paths, so nothing held the command to it, and the run ended with the file changed. A call of a tool that names no
/// paths, in a step under the limit, is now measured around: what it changed of the files the run found is asked about
/// after the fact, and a refused file is put back byte for byte where the snapshot can prove the bytes - named otherwise.
/// </summary>
public sealed class WhatACommandChangedIsHeldToTheLimitTests
{
    private const string Limit = "Leave prices.txt as it is.";
    private const string Request = "Check the order totals against the price list. " + Limit + " Correct the totals in orders.txt.";
    private const string Refuse = """{"allow":false,"reason":"the request says to leave the price list as it is"}""";

    private static readonly ToolCall Command = new("k1", "run_command", """{"command":"echo 2.5> prices.txt"}""");
    private static readonly ToolRegistry Tools = new(EngineFixture.ShippedTools());

    private static ChangeLimitGuard Guard(IChatProvider planner)
        => new(Request, [Limit], planner, new ModelRef("planner", "strong"), new RunBudget(null, DateTimeOffset.UtcNow), 1000);

    private static async Task<StepFrame> FrameAsync(EngineFixture fx, IWorkspaceChanges? changes, bool measured = true)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, fx.Workspace, Tools.Definitions, [], new ExecutionJournal(), new(),
            fx.Artifacts.BeginStep(), changes: changes, stepStart: measured && changes is not null ? await changes.TakeAsync(default) : null);

    /// <summary>The effect of one "command": <paramref name="command"/> run between the two snapshots.</summary>
    private static async Task<CommandEffect.Outcome> AroundAsync(EngineFixture fx, FakeChatProvider planner, Action command,
        Func<StepFrame, Task>? setUp = null)
    {
        using var changes = new WorkspaceChanges(fx.Root);
        var frame = await FrameAsync(fx, changes);
        if (setUp is not null) await setUp(frame);
        var effect = new CommandEffect(Guard(planner), frame, "Correct the totals");
        var before = (await effect.BeforeAsync(default))!;
        command();
        return await effect.AfterAsync(Command, before, "I will fix the price instead.", default);
    }

    private static EngineFixture Repository(params (string Path, string Content)[] files)
    {
        var fx = new EngineFixture();
        foreach (var (path, content) in files) fx.Write(path, content);
        TestGit.Repository(fx.Root);
        return fx;
    }

    [Fact]
    public async Task Applies_only_with_a_limit_a_measured_step_and_a_tool_that_names_no_paths()
    {
        using var fx = new EngineFixture();
        using var changes = new WorkspaceChanges(fx.Root);
        var measured = await FrameAsync(fx, changes);
        var guard = Guard(new FakeChatProvider());
        var command = Tools.DefinitionOf("run_command");

        Assert.True(CommandEffect.Applies(guard, measured, command));
        Assert.False(CommandEffect.Applies(null, measured, command));
        Assert.False(CommandEffect.Applies(guard, await FrameAsync(fx, changes, measured: false), command));   // steps side by side
        Assert.False(CommandEffect.Applies(guard, measured, Tools.DefinitionOf("write_file")));               // names its paths
        Assert.False(CommandEffect.Applies(guard, measured, Tools.DefinitionOf("read_file")));                // changes nothing
    }

    [Fact]
    public async Task A_refused_change_is_put_back_and_said()
    {
        using var fx = Repository(("prices.txt", "apple 3\r\n"));
        var planner = new FakeChatProvider(Turn.Says(Refuse).Reporting(300, 20));

        var outcome = await AroundAsync(fx, planner, () => fx.Write("prices.txt", "apple 2.5"));

        Assert.StartsWith("REFUSED after the fact: this command changed 'prices.txt'", outcome.Appended, StringComparison.Ordinal);
        Assert.Contains("the request says to leave the price list as it is", outcome.Appended, StringComparison.Ordinal);
        Assert.Contains("It was put back as it was before the command.", outcome.Appended, StringComparison.Ordinal);
        var revert = Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted);
        Assert.Equal(["prices.txt"], revert.RevertedPaths());
        Assert.Equal("apple 3\r\n", fx.Read("prices.txt"));
        Assert.Contains(outcome.Events, e => e.Kind == EventKind.UsageReported);
    }

    [Fact]
    public async Task A_refused_change_that_cannot_be_put_back_says_why()
    {
        using var fx = Repository(("prices.txt", "apple 3"));
        TestGit.Run(fx.Root, "config", "core.autocrlf", "true");
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        var outcome = await AroundAsync(fx, planner, () => fx.Write("prices.txt", "apple 2.5"));

        Assert.Contains("could not be put back (a line-ending conversion applies to it)", outcome.Appended, StringComparison.Ordinal);
        Assert.Contains("Put it back yourself", outcome.Appended, StringComparison.Ordinal);
        Assert.Equal(["prices.txt"], Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted).KeptPaths());
        Assert.Equal("apple 2.5", fx.Read("prices.txt"));
    }

    [Fact]
    public async Task What_the_command_added_or_the_run_made_is_not_asked()
    {
        using var fx = Repository(("orders.txt", "2 apples: 5"));
        var planner = new FakeChatProvider();

        var outcome = await AroundAsync(fx, planner, () =>
            {
                fx.Write("new.txt", "made by the command");
                fx.Write("made.txt", "changed by the command");
                fx.Write(".enactive/scratch/notes.md", "n");
            },
            setUp: frame => frame.Store.CreateAsync("made.txt", ArtifactKind.FileSet, "made",
                s => s.WriteAsync(Encoding.UTF8.GetBytes("made by the run")).AsTask(), default));

        Assert.Empty(planner.Requests);
        Assert.Null(outcome.Appended);
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public async Task A_rename_is_asked_by_the_name_it_left()
    {
        using var fx = Repository(("prices.txt", "apple 3\napple 4\napple 5\n"));
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        var outcome = await AroundAsync(fx, planner, () => File.Move(Path.Combine(fx.Root, "prices.txt"), Path.Combine(fx.Root, "old-prices.txt")));

        Assert.Contains("It moved prices.txt away", planner.Requests[0].Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("apple 3\napple 4\napple 5\n", fx.Read("prices.txt"));
        Assert.True(fx.Exists("old-prices.txt"));
        var revert = Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted);
        Assert.Equal(["prices.txt"], revert.RevertedPaths());
        Assert.Equal(["old-prices.txt"], revert.KeptPaths());
        Assert.Contains("'old-prices.txt'", outcome.Appended, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Many_files_are_named_up_to_ten_and_counted()
    {
        var files = Enumerable.Range(1, 12).Select(i => ($"f{i:00}.txt", "as it was")).ToArray();
        using var fx = Repository(files);
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        var outcome = await AroundAsync(fx, planner, () => { foreach (var (path, _) in files) fx.Write(path, "formatted"); });

        Assert.Contains("and 2 more", outcome.Appended, StringComparison.Ordinal);
        Assert.DoesNotContain("f12.txt", outcome.Appended, StringComparison.Ordinal);
        Assert.Equal(12, Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted).RevertedPaths()!.Count);
        Assert.All(files, f => Assert.Equal("as it was", fx.Read(f.Item1)));
    }
}

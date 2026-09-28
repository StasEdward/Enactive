namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Run 9c1a061b, 2026-09-28, read from the whole request bodies: the prompt promised tools the run did not
/// carry, showed the verification contract as the engine's own JSON, and the searches walked a folder the
/// project's .gitignore excludes - old copies of the code, reported as the code.
/// </summary>
public sealed class WhatTheModelIsToldIsTrueTests
{
    [Fact]
    public void The_verification_contract_is_said_in_lines_not_as_the_engines_record()
    {
        var lines = Orchestrator.CheckLines([
            new SuccessCriterionDefinition("report exists", "Test-Path 'Docs/R.md'", Origin: CriterionOrigin.Proposed) { PlanningReason = "the deliverable" },
            new SuccessCriterionDefinition("tests", "dotnet test", Required: false)]);

        Assert.Equal("- report exists: `Test-Path 'Docs/R.md'`, expected exit code 0 - why: the deliverable\n"
                     + "- tests: `dotnet test`, expected exit code 0 (optional)", lines);
        Assert.DoesNotContain("Origin", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_the_gitignore_excludes_by_name_are_left_out_of_a_sweep_and_said_to_be()
    {
        using var fx = new EngineFixture();
        fx.Write(".gitignore", "# old copies\nwork/\n*.log\n/dist-old\n!keep\nDocs/R.md\n");
        fx.Write("work/Old.cs", "class Orchestrator {}");
        fx.Write("src/New.cs", "class Orchestrator {}");

        Assert.Equal(["work"], WorkspaceGuard.IgnoredFolders(fx.Root));        // a pattern, a missing folder, a negation, a file: left to git
    }

    [Fact]
    public async Task A_search_of_the_workspace_does_not_find_the_old_copies_and_says_what_it_left_out()
    {
        using var fx = new EngineFixture();
        fx.Write(".gitignore", "work/\n");
        fx.Write("work/Old.cs", "class Orchestrator {}");
        fx.Write("src/New.cs", "class Orchestrator {}");
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"find it"}"""),
            Turn.Calls1("search_files", """{"pattern":"class Orchestrator"}""", "s1"),
            Turn.Calls1("search_files", """{"pattern":"class Orchestrator","path":"work"}""", "s2"),
            Turn.Says("Found it."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "find the orchestrator");

        var sweep = provider.Requests[2].Messages.Last().Content!;
        Assert.Contains("src/New.cs", sweep, StringComparison.Ordinal);
        Assert.DoesNotContain("work/Old.cs", sweep, StringComparison.Ordinal);
        Assert.True(sweep.Contains("left out: work/ - excluded by the project's .gitignore", StringComparison.Ordinal), sweep);
        Assert.Contains("work/Old.cs", provider.Requests[3].Messages.Last().Content!, StringComparison.Ordinal);   // named, it is searched
    }

    [Fact]
    public async Task Tools_the_run_does_not_offer_are_said_to_the_model_once()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("read_file", "list_dir", "run_command"),
            policy: new Enactive.Core.Permissions.PermissionPolicy(Enactive.Core.Permissions.PermissionLevel.Execute, ["*"], ["run_command"]),
            decisions: new UnattendedDecisionHandler()), "look around");

        var told = provider.Requests[1].Messages.Where(m => m.Role == ChatRole.User
            && m.Content?.StartsWith("Not available in this run: run_command", StringComparison.Ordinal) == true).ToArray();
        Assert.Single(told);
        Assert.Contains("do the work with the tools you have", told[0].Content!, StringComparison.Ordinal);
    }

    /// <summary>Run 16d57849: a fresh start after a handover is rebuilt from the preamble, and the note was not in it.</summary>
    [Fact]
    public async Task What_is_not_offered_is_said_again_after_a_fresh_start()
    {
        using var fx = new EngineFixture();
        Turn Read(int i, int prompt)
        {
            fx.Write($"p{i}.md", $"page {i}");
            return Turn.Calls1("read_file", $$"""{"path":"p{{i}}.md"}""", $"r{i}").Reporting(prompt: prompt);
        }
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read"}"""),
            Read(1, 3_000), Read(2, 9_000),
            Turn.Says("# Note - read p1 and p2."),
            Read(3, 3_000),
            Turn.Says("Done.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("read_file", "list_dir", "run_command"),
            policy: new Enactive.Core.Permissions.PermissionPolicy(Enactive.Core.Permissions.PermissionLevel.Execute, ["*"], ["run_command"]),
            decisions: new UnattendedDecisionHandler()), "read the pages");

        Assert.Contains(events, e => e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal));
        var fresh = provider.Requests.First(r => r.Messages.Any(m => m.Content?.Contains("started again from your own notes", StringComparison.Ordinal) == true));
        Assert.Single(fresh.Messages, m => m.Content?.StartsWith("Not available in this run: run_command", StringComparison.Ordinal) == true);
    }
}

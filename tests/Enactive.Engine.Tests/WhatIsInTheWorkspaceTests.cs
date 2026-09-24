namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Xunit;

/// <summary>
/// The planner is shown how much of what there is.
///
/// <para><b>The measurement that asked for this.</b> One request, one workspace, five runs on
/// 2026-09-22: the planner produced 1, 3, 4 and 5 steps on different attempts, and the single-step
/// version ran to the 250-turn backstop, was marked Incomplete and took the rest of the plan with
/// it — 30.6M prompt tokens for nothing. "Check every page" is one comfortable step for eleven
/// pages and a dead run for three hundred, and nothing in the planner's prompt said which of the
/// two it was looking at. It was not a judgement the planner got wrong; it was a number nobody
/// gave it.</para>
///
/// <para>The engine already tells it what a step COSTS and when one is abandoned. That is advice
/// about a quantity it still cannot see. This is the quantity.</para>
/// </summary>
public sealed class WhatIsInTheWorkspaceTests
{
    private static string Tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "enactive-census-" + Guid.NewGuid().ToString("N"));

        void Write(string relative, int count, string extension)
        {
            var folder = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folder);
            for (var i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(folder, $"file{i}{extension}"), "x");
        }

        Write("Docs/wiki", 11, ".md");
        Write("src/Enactive.Core", 41, ".cs");
        Write("src/Enactive.Core", 1, ".csproj");
        Write("tests", 5, ".cs");
        Write("bin/Debug", 300, ".dll");          // build output: not the workspace
        Write(".enactive/scratch", 9, ".log");    // our own state: not the workspace either
        Write("odd", 1, ".txt");                  // one loose file is not a folder worth naming

        return root;
    }

    [Fact]
    public void The_census_counts_what_is_there_by_folder()
    {
        var root = Tree();
        try
        {
            var lines = WorkspaceCensus.Of(root);

            Assert.Contains(lines, l => l.StartsWith("src/Enactive.Core", StringComparison.Ordinal)
                                        && l.Contains("41 .cs", StringComparison.Ordinal)
                                        && l.Contains("1 .csproj", StringComparison.Ordinal));

            // The case the whole thing exists for: "Docs/wiki 11 .md" is what turns "check every
            // page" into arithmetic a planner can do.
            Assert.Contains(lines, l => l.StartsWith("Docs/wiki", StringComparison.Ordinal)
                                        && l.Contains("11 .md", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Build output is not the workspace, and neither is our own state folder. The 300 DLLs would
    /// otherwise be the largest thing in the census and the first thing the planner read.
    /// </summary>
    [Fact]
    public void Build_output_and_the_state_folder_are_not_counted()
    {
        var root = Tree();
        try
        {
            var lines = WorkspaceCensus.Of(root);

            Assert.DoesNotContain(lines, l => l.Contains("bin", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, l => l.Contains(".enactive", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, l => l.Contains(".dll", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A handful of loose files is not a folder anybody sizes work against.</summary>
    [Fact]
    public void A_folder_with_almost_nothing_in_it_gets_no_line()
    {
        var root = Tree();
        try
        {
            Assert.DoesNotContain(WorkspaceCensus.Of(root),
                                  l => l.StartsWith("odd", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The walk stops somewhere, and it SAYS where. A census that quietly stopped counting would
    /// hand the planner a number presented as the whole truth — the one thing it must not be given,
    /// since the whole point of the block is that it can be trusted for arithmetic.
    /// </summary>
    [Fact]
    public void A_census_that_stopped_counting_says_so()
    {
        var root = Tree();
        try
        {
            var lines = WorkspaceCensus.Of(root, maxFiles: 20);

            Assert.Contains(lines, l => l.Contains("counted the first 20 files", StringComparison.Ordinal)
                                        && l.Contains("there are more", StringComparison.Ordinal));

            // And it is not said when nothing was cut.
            Assert.DoesNotContain(WorkspaceCensus.Of(root),
                                  l => l.Contains("counted the first", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A workspace that cannot be walked leaves the planner exactly where it was before any of this
    /// existed. A census is a courtesy; it is never a reason a run does not start.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Z:\\no\\such\\place")]
    public void Nothing_to_count_is_not_an_error(string? root)
        => Assert.Empty(WorkspaceCensus.Of(root));

    /// <summary>
    /// THE ONE THAT DECIDED THE SHAPE. Counting what is ON DISK counts the wrong thing: run against
    /// this repository, a walk reported "work — 417 .ps1, 210 .log" and four folders of .dll before
    /// it reached a single source file. Scratch piles, publish output and restored packages are
    /// most of what sits there and none of what anybody means by "the project", and a planner
    /// sizing steps against that number is worse off than one given nothing.
    ///
    /// <para>So the census counts a LIST, and the caller hands it what the repository tracks. The
    /// skip list stays for workspaces that are not repositories — it can only ever chase the
    /// folders we already know about.</para>
    /// </summary>
    [Fact]
    public void What_the_repository_tracks_is_what_gets_counted()
    {
        // What `git ls-files` would return: no bin, no obj, no scratch pile - because none of it
        // is tracked. The census does not have to know why they are absent.
        var tracked = new[]
        {
            "Docs/wiki/Architecture.md", "Docs/wiki/Console.md", "Docs/wiki/Settings.md",
            "src/Enactive.Core/Chat.cs", "src/Enactive.Core/Tasks.cs", "src/Enactive.Core/Plan.cs",
            "src/Enactive.Core/Enactive.Core.csproj"
        };

        var lines = WorkspaceCensus.Of(tracked);

        Assert.Contains(lines, l => l.StartsWith("Docs/wiki", StringComparison.Ordinal)
                                    && l.Contains("3 .md", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("src/Enactive.Core", StringComparison.Ordinal)
                                    && l.Contains("3 .cs", StringComparison.Ordinal));
    }

    /// <summary>A list with nothing in it says nothing, rather than saying "0 files".</summary>
    [Fact]
    public void An_empty_list_is_an_empty_census()
        => Assert.Empty(WorkspaceCensus.Of(Array.Empty<string>()));

    /// <summary>
    /// And it reaches the planner. The census can be perfect and still be useless if the prompt
    /// does not carry it — which is what happened to four registered tools and two MCP servers
    /// this month.
    /// </summary>
    [Fact]
    public void The_planner_prompt_carries_it()
    {
        var context = new WorkContext(
            Guid.NewGuid(), "Enactive", null, null, "main",
            Array.Empty<string>(), Array.Empty<string>())
        {
            Inventory = new[] { "Docs/wiki — 11 .md", "src — 214 .cs" }
        };

        var where = Planner.Where(context);

        Assert.Contains("Docs/wiki — 11 .md", where, StringComparison.Ordinal);
        Assert.Contains("214 .cs", where, StringComparison.Ordinal);

        // Still labelled as background, like everything else in that block: a planner that reads
        // "src — 214 .cs" as something to act on plans to act on it.
        Assert.Contains("NOT the request", where, StringComparison.Ordinal);
    }
}

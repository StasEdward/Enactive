namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Run 341c2f, 2026-09-29: the analysis step read coverage-report.md - written by a run an hour before, about a test
/// project since removed - and handed on its figures as this run's findings. What an earlier run wrote, and what earlier
/// runs left in the one shared scratch area, is now told as that where it is read - to the model and in the evidence the
/// reviewer judges. Nothing is said about the project's own files, or about what this run wrote. Deliberately not code:
/// a stocktake report.
/// </summary>
public sealed class WhatAnEarlierRunLeftIsToldAsSuchTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"stocktake"}""";

    private static FakeChatProvider Writes(string path, string content) => new(
        Turn.Says(QuickAction),
        Turn.Calls1("write_file", $$"""{"path":"{{path}}","content":"{{content}}"}""", "w1"),
        Turn.Says("Written."));

    private static FakeChatProvider Reads(string path) => new(
        Turn.Says(QuickAction),
        Turn.Calls1("read_file", $$"""{"path":"{{path}}"}""", "r1"),
        Turn.Says("Read it."));

    private static string ReadResult(IEnumerable<WorkEvent> events)
        => events.Single(e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith("read_file", StringComparison.Ordinal)).Summary;

    /// <summary>THE ONE THAT MATTERS: the report an earlier run wrote is read as that run's account, not as a finding.</summary>
    [Fact]
    public async Task A_report_an_earlier_run_wrote_is_read_as_that_runs_account()
    {
        using var fx = new EngineFixture();
        await fx.RunAsync(fx.Build(Writes("stock/report.md", "Shelf 17: 42 boxes")), "count the boxes and write a report");
        Thread.Sleep(20);

        var reader = Reads("stock/report.md");
        var events = await fx.RunAsync(fx.Build(reader), "check the stocktake");

        var said = ReadResult(events);
        Assert.Contains("[Engine: 'stock/report.md' was written by an earlier Enactive run", said, StringComparison.Ordinal);
        Assert.Contains("request: \"count the boxes and write a report\"", said, StringComparison.Ordinal);
        Assert.Contains("has not changed since", said, StringComparison.Ordinal);
        Assert.Contains("not a measurement of the workspace now",
            reader.Requests.Last().Messages.Last().Content, StringComparison.Ordinal);            // the model is told
    }

    [Fact]
    public async Task One_changed_since_says_so()
    {
        using var fx = new EngineFixture();
        await fx.RunAsync(fx.Build(Writes("stock/report.md", "Shelf 17: 42 boxes")), "count the boxes and write a report");
        fx.Write("stock/report.md", "Shelf 17: 40 boxes (recounted by hand)");
        Thread.Sleep(20);

        var events = await fx.RunAsync(fx.Build(Reads("stock/report.md")), "check the stocktake");

        Assert.Contains("has been changed since", ReadResult(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_shared_scratch_held_before_this_run_is_not_this_runs_work()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/notes.md", "another task's notes");
        Thread.Sleep(20);

        var events = await fx.RunAsync(fx.Build(Reads(".enactive/scratch/notes.md")), "check the stocktake");

        Assert.Contains("was left in the shared scratch area before this run began", ReadResult(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_projects_own_files_and_what_this_run_wrote_are_read_as_they_are()
    {
        using var fx = new EngineFixture();
        fx.Write("stock/shelves.txt", "shelf 17");
        Thread.Sleep(20);
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"stock/shelves.txt"}""", "r1"),
            Turn.Calls1("write_file", """{"path":"stock/report.md","content":"42"}""", "w1"),
            Turn.Calls1("read_file", """{"path":"stock/report.md"}""", "r2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(worker), "count the boxes");

        Assert.DoesNotContain(events, e => e.Summary.Contains("[Engine:", StringComparison.Ordinal));
        Assert.Contains("stock/report.md", File.ReadAllText(EarlierRuns.FileIn(fx.Root)), StringComparison.Ordinal);   // recorded for the next
    }
}

namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What a snapshot did not measure is not reported as unchanged, and a handover reply that calls a
/// tool is not a note.
///
/// <para><b>Reported 2026-09-24 against 9dk and 9dl.</b> A staged write - a proposal, not on disk -
/// closed a run as "no files changed; written and left as it was: report.md", and a handover beside it
/// said "No file in the workspace differs", with an instruction to believe that over the model. A file
/// written into bin/, which the snapshot skips, read the same. And a handover reply of "I will read
/// Prod.cs next." with a read_file call was taken as the note, the history cut, and the call never
/// run.</para>
/// </summary>
public sealed class WhatWasNotMeasuredIsNotClaimedTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";

    private static string ClosingLine(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed).Summary;

    private static string Resumed(FakeChatProvider provider)
        => provider.Requests.SelectMany(r => r.Messages)
                   .Select(m => m.Content ?? "")
                   .Last(c => c.Contains("started again from your own notes", StringComparison.Ordinal));

    // ── the closing line ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_staged_write_is_a_proposal_waiting_not_a_file_left_as_it_was()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the report"}""", "w1"),
            Turn.Says("Written."));

        var events = await fx.RunAsync(
            fx.Build(provider, EngineFixture.Role("developer"), artifacts: new StagingArtifactStore(fx.Root)),
            "write the report");

        var line = ClosingLine(events);
        Assert.Contains("proposed, waiting to be applied: report.md", line, StringComparison.Ordinal);
        Assert.Contains("no files changed on disk", line, StringComparison.Ordinal);
        Assert.DoesNotContain("left as it was", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_written_where_the_snapshot_does_not_look_is_not_called_unchanged()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"bin/report.md","content":"the report"}""", "w1"),
            Turn.Says("Written."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the report");

        var line = ClosingLine(events);
        Assert.Equal("the report", fx.Read("bin/report.md"));
        Assert.Contains("written where the run does not measure, so not compared: bin/report.md", line, StringComparison.Ordinal);
        Assert.DoesNotContain("left as it was", line, StringComparison.Ordinal);
        Assert.DoesNotContain("no files changed", line, StringComparison.Ordinal);
    }

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    [Fact]
    public async Task A_file_git_ignores_is_not_called_unchanged()
    {
        using var fx = new EngineFixture();
        Git(fx.Root, "init", "-q");
        Git(fx.Root, "config", "user.email", "t@example.com");
        Git(fx.Root, "config", "user.name", "t");
        fx.Write(".gitignore", "*.log\n");
        Git(fx.Root, "add", "-A");
        Git(fx.Root, "commit", "-q", "-m", "start");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"run.log","content":"a log"}""", "w1"),
            Turn.Says("Written."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the report");

        Assert.Contains("so not compared: run.log", ClosingLine(events), StringComparison.Ordinal);
    }

    // ── the handover ─────────────────────────────────────────────────────────

    /// <summary>The measured case from the report: text and a tool call in one reply.</summary>
    [Fact]
    public async Task A_handover_reply_that_calls_a_tool_is_not_taken_as_the_note()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", "class Prod { }");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"Prod.cs"}""", "r1").Reporting(prompt: 8_000),
            new Turn("I will read Prod.cs next.", new[] { new ToolCall("r2", "read_file", """{"path":"Prod.cs"}""") }),
            Turn.Says("Done."))
        { Window = 10_000, HandoverAt = 75 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the report");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ContextTrimmed
                                           && e.Summary.Contains("Carrying its own notes", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Requests.SelectMany(r => r.Messages),
                              m => m.Content?.Contains("I will read Prod.cs next.", StringComparison.Ordinal) == true
                                   && m.Role == ChatRole.User);
    }

    [Fact]
    public async Task A_handover_names_a_staged_write_as_a_proposal()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the report"}""", "w1").Reporting(prompt: 8_000),
            Turn.Says("Note: report.md is written."),
            Turn.Says("Done."))
        { Window = 10_000, HandoverAt = 75 };

        await fx.RunAsync(
            fx.Build(provider, EngineFixture.Role("developer"), artifacts: new StagingArtifactStore(fx.Root)),
            "write the report");

        var resumed = Resumed(provider);
        Assert.Contains("Proposed and waiting for the user to apply - NOT on disk yet", resumed, StringComparison.Ordinal);
        Assert.Contains("report.md", resumed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handover_names_a_write_the_engine_did_not_measure()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"bin/report.md","content":"the report"}""", "w1").Reporting(prompt: 8_000),
            Turn.Says("Note: bin/report.md is written."),
            Turn.Says("Done."))
        { Window = 10_000, HandoverAt = 75 };

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the report");

        var resumed = Resumed(provider);
        Assert.Contains("Written by you where the engine does not measure", resumed, StringComparison.Ordinal);
        Assert.Contains("bin/report.md", resumed, StringComparison.Ordinal);
    }
}

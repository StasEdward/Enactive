namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Execution;
using Xunit;

public sealed class CommandHistoryTests
{
    [Fact]
    public void History_retains_failure_restoration_order_and_null_exit_without_interpreting_success()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", "{\"command\":\"verify\"}", ActionOutcome.Failed, "failed", exitCode: 1);
        journal.Record(1, "edit_file", "{}", ActionOutcome.Succeeded, "restored");
        journal.Record(2, "run_command", "{\"command\":\"verify\"}", ActionOutcome.Succeeded, "passed", exitCode: 0);
        journal.Record(2, "run_powershell", "{}", ActionOutcome.Refused, "denied");
        using var doc = JsonDocument.Parse(journal.Describe().CommandHistory());
        var calls = doc.RootElement.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 3, 4 }, calls.Select(c => c.GetProperty("call").GetInt32()));
        Assert.Equal(1, calls[0].GetProperty("exitCode").GetInt32());
        Assert.Equal(0, calls[1].GetProperty("exitCode").GetInt32());
        Assert.Equal(2, calls[1].GetProperty("step").GetInt32());
        Assert.Equal(JsonValueKind.Null, calls[2].GetProperty("exitCode").ValueKind);
    }

    [Fact]
    public void Hidden_calls_and_resume_gaps_are_explicit_and_not_revealed_by_history()
    {
        var journal = new ExecutionJournal();
        journal.NotePriorTranscript();
        for (var i = 0; i < 100; i++)
            journal.Record(1, "run_command", i == 0 ? "HIDDEN_FIRST" : "verify" + i, ActionOutcome.Succeeded, new string('x', 200), exitCode: 0);
        var view = journal.Describe(maxChars: 1200);
        var history = view.CommandHistory();
        Assert.DoesNotContain("HIDDEN_FIRST", history);
        using var doc = JsonDocument.Parse(history);
        Assert.True(doc.RootElement.GetProperty("priorHistoryUnavailable").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("omittedCommands").GetInt32() > 0);
        Assert.All(doc.RootElement.GetProperty("calls").EnumerateArray(), c =>
            Assert.Contains(c.GetProperty("call").GetInt32(), view.VisibleActionIds));
    }
}

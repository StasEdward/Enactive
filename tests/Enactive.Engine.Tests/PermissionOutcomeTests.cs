namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Workers;
using Xunit;

/// <summary>
/// A refusal is not a result.
///
/// <para>A call the role does not offer, one the policy blocks, or one the user declines used to
/// reach only the transcript: an ERROR line in the conversation and <c>continue</c>. Nothing counted
/// it. So a model that was denied the one action the request needed could go on to say "done", and
/// the run reported Completed with the file never written — a permission system whose entire effect
/// was a sentence nobody checked afterwards.</para>
///
/// <para>They are counted now, exactly like a tool that ran and failed, and the same end-of-loop
/// rule applies: a final answer over an unresolved call is Incomplete. Not permitted is not
/// performed.</para>
/// </summary>
public sealed class PermissionOutcomeTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"write the file"}""";

    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    private static FakeChatProvider TriesToWriteThenClaimsSuccess()
        => new(
            Turn.Says(QuickPlan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the report"}""", "c1"),
            Turn.Says("Done — I wrote the report."))
        {
            WhenExhausted = Turn.Says("Done — I wrote the report.")
        };

    [Fact]
    public async Task A_tool_the_role_does_not_offer_stops_the_run_being_called_done()
    {
        using var fx = new EngineFixture();

        // A role with no write_file. The request needs one.
        var events = await fx.RunAsync(
            fx.Build(TriesToWriteThenClaimsSuccess(), worker: EngineFixture.WorkerWith("read_file", "list_dir")),
            "write the report");

        Assert.Equal(RunOutcomeKind.Incomplete, Terminal(events).Outcome());
        Assert.False(fx.Exists("report.md"));
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.Contains("write_file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_the_user_declines_stops_the_run_being_called_done()
    {
        using var fx = new EngineFixture();

        // write_file has to be asked about, and the answer is no.
        var policy = new PermissionPolicy(
            PermissionLevel.Execute, new[] { "*" }, new[] { "write_file" });

        fx.Decisions.Answer = "deny";

        var events = await fx.RunAsync(
            fx.Build(TriesToWriteThenClaimsSuccess(),
                     worker: EngineFixture.WorkerWith("write_file", "read_file"),
                     policy: policy),
            "write the report");

        Assert.NotEmpty(fx.Decisions.Requests);
        // Not done: blocked on the permission it was refused (Phase 7) - which somebody can give.
        Assert.Equal(RunOutcomeKind.Blocked, Terminal(events).Outcome());
        Assert.Contains("needs a permission it was refused", Terminal(events).OutcomeReason(), StringComparison.Ordinal);
        Assert.False(fx.Exists("report.md"));
    }

    // The gate must not turn every run red either: a step that was allowed to do its work still
    // finishes as it did before.
    [Fact]
    public async Task An_allowed_call_still_completes()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(TriesToWriteThenClaimsSuccess(), worker: EngineFixture.WorkerWith("write_file", "read_file")),
            "write the report");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.True(fx.Exists("report.md"));
    }

    // And a refusal is not held against the step for ever: the same call, allowed and successful on
    // a later turn, clears it — exactly as a tool failure that is later put right does.
    [Fact]
    public async Task A_refused_call_that_later_goes_through_clears()
    {
        using var fx = new EngineFixture();

        var policy = new PermissionPolicy(
            PermissionLevel.Execute, new[] { "*" }, new[] { "write_file" });

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the report"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the report"}""", "c2"),
            Turn.Says("Done."))
        {
            WhenExhausted = Turn.Says("Done.")
        };

        // Refused the first time it is asked, allowed the second.
        fx.Decisions.Script.Enqueue("deny");
        fx.Decisions.Script.Enqueue("allow");

        var events = await fx.RunAsync(
            fx.Build(provider,
                     worker: EngineFixture.WorkerWith("write_file", "read_file"),
                     policy: policy),
            "write the report");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.True(fx.Exists("report.md"));
    }
}

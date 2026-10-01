namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// The seam between the engine that ASKS a permission and the handler that publishes it.
///
/// <para>Both sides of this seam were tested and both were right. RemoteApprovalTests hands
/// RemoteDecisionHandler a request carrying a BoundAction and proves it publishes a card, races the
/// desktop, and refuses a shell. The gateway tests prove the panel renders what arrives. Neither
/// asked the question in the middle: does the engine ever produce a request with a BoundAction on
/// it?</para>
///
/// <para>It did not. <c>DecisionRequest.Action</c> is an optional parameter defaulting to null, the
/// orchestrator's call site never passed one, and <c>new BoundAction(...)</c> appeared exactly once
/// in the repository - in a test. So every real permission request took the handler's first branch,
/// "no bound action, therefore a local question", and was answered on the desktop and nowhere else.
/// A task started from a phone asked its question on a screen the person was not looking at, then
/// waited two hours to expire. Remote approvals had never worked, and every test about them
/// passed.</para>
///
/// <para>These tests run the REAL orchestrator through the REAL handler. That is the whole point:
/// the shape has to be one the engine actually produces, not one a test can construct.</para>
/// </summary>
public sealed class RemoteApprovalWiringTests : IDisposable
{
    private const string RunId = "run-1";
    private const string QuickPlan = """{"disposition":"quick_action","title":"do it","steps":[]}""";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "enactive-wiring-" + Guid.NewGuid().ToString("N"));

    private readonly HostStore _store;
    private readonly RemoteApprovals _approvals = new();
    private readonly FixedHostKeys _keys = new();

    public RemoteApprovalWiringTests()
    {
        _store = new HostStore(Path.Combine(_folder, "remote.db"));

        _store.Accept(_keys.Start(runId: RunId));
        _store.BeginRun("command-1", RunId);
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Litter, not a failure.
        }
    }

    private static PermissionPolicy AsksBefore(params string[] tools)
        => new(PermissionLevel.Execute, new[] { "*" }, tools);

    /// <summary>Everything queued for the run, drained.</summary>
    private (RemoteEventKind Kind, ApprovalRequest? Request)[] Queued()
    {
        var events = new List<(RemoteEventKind, ApprovalRequest?)>();

        while (_store.NextOwed().FirstOrDefault(o => o.RunId == RunId) is { } owed)
        {
            events.Add((owed.Event.Kind, owed.Event.Approval));
            _store.Discard(owed.EventId);
        }

        return events.ToArray();
    }

    private ApprovalRequest? PublishedCard()
    {
        var cards = Queued().Where(e => e.Kind == RemoteEventKind.ApprovalRequested).ToArray();
        return cards.Length == 1 ? cards[0].Request : null;
    }

    /// <summary>
    /// The decisive one. A run asks a permission; the panel is told. Everything else about remote
    /// approval - the race, the two-hour window, the Allow and Deny buttons - is downstream of a
    /// card existing at all.
    /// </summary>
    [Fact]
    public async Task A_permission_the_engine_asks_for_reaches_the_panel()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "hello");

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("read_file", """{"path":"notes.md"}""", "r1"))
        {
            WhenExhausted = Turn.Says("read it")
        };

        await fx.RunAsync(
            fx.Build(provider,
                     worker: EngineFixture.WorkerWith("read_file"),
                     policy: AsksBefore("read_file"),
                     decisions: new RemoteDecisionHandler(
                         new ScriptedDecisionHandler("allow"), _store, _approvals, _keys.Sealer(),
                         RunId, TimeSpan.FromSeconds(30))),
            "read the notes");

        Assert.Contains(RemoteEventKind.ApprovalRequested, Queued().Select(e => e.Kind));
    }

    /// <summary>
    /// And the card says what was asked. A card that names no tool and carries no arguments is a
    /// prompt to approve something unspecified, which is the thing the unabridged-detail rule
    /// exists to prevent: a person cannot approve what they were not shown.
    /// </summary>
    [Fact]
    public async Task The_card_carries_the_tool_and_the_arguments()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "hello");

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("read_file", """{"path":"notes.md"}""", "r1"))
        {
            WhenExhausted = Turn.Says("read it")
        };

        await fx.RunAsync(
            fx.Build(provider,
                     worker: EngineFixture.WorkerWith("read_file"),
                     policy: AsksBefore("read_file"),
                     decisions: new RemoteDecisionHandler(
                         new ScriptedDecisionHandler("allow"), _store, _approvals, _keys.Sealer(),
                         RunId, TimeSpan.FromSeconds(30))),
            "read the notes");

        var card = PublishedCard();

        Assert.NotNull(card);
        var action = _keys.OpenAction(RunId, card!);
        Assert.Equal("read_file", action.Tool);
        Assert.Equal("r1", card.ToolCallId);
        Assert.Contains("notes.md", action.FullText);
        Assert.Contains("notes.md", action.ArgumentsJson);
        Assert.True(card.RemoteDecidable);
    }

    /// <summary>
    /// A shell asked for by a remote run is published and then refused, and the refusal does not
    /// depend on anybody being at the computer.
    ///
    /// <para>In a shipping remote run RemotePolicy denies both shells before the handler is
    /// reached, so this is the second of two independent refusals rather than the only one. It is
    /// exercised here through the real engine anyway: "the panel is told what the run wanted" is a
    /// claim about the pair, and a policy denial and a handler denial say different things to the
    /// person reading the timeline.</para>
    /// </summary>
    [Fact]
    public async Task A_shell_is_shown_to_the_panel_and_refused_without_asking_the_desktop()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo hello"}""", "c1"))
        {
            WhenExhausted = Turn.Says("done")
        };

        var desktop = new ScriptedDecisionHandler("allow");

        await fx.RunAsync(
            fx.Build(provider,
                     worker: EngineFixture.WorkerWith("run_command"),
                     policy: AsksBefore("run_command"),
                     decisions: new RemoteDecisionHandler(
                         desktop, _store, _approvals, _keys.Sealer(), RunId, TimeSpan.FromSeconds(30))),
            "run the thing");

        var card = PublishedCard();

        Assert.NotNull(card);
        Assert.Equal("run_command", _keys.OpenAction(RunId, card!).Tool);
        Assert.False(card.RemoteDecidable);

        // The desktop was never asked. A shell refused for a web-started run is refused because of
        // where the run came from, not because nobody happened to be sitting there to allow it.
        Assert.Empty(desktop.Requests);
    }
}

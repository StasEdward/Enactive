namespace Enactive.Engine.Tests;

using System.Reflection;
using System.Security.Cryptography;
using Enactive.App.Ui;
using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;
using Enactive.Settings;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What the Host seals before anything leaves it, and what it checks before it believes a command
/// (spec section 6).
///
/// <para>Protocol 2 makes the gateway a courier: it routes by ids and stores ciphertext. That only
/// holds if the Host never acts on a field the gateway could have written, so every command here is
/// first sealed the way a trusted browser seals it and then altered the way a gateway could alter it.
/// A test that only fed the Host genuine commands would prove that genuine commands work.</para>
/// </summary>
public sealed class RemoteSealingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FixedHostKeys _keys = new();
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "enactive-sealing-" + Guid.NewGuid().ToString("N"));

    private Sealer Sealer => _keys.Sealer(new FixedClock(Now));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Litter, not a failure.
        }
    }

    // ── rule 1: a start opens to what the owner sealed ──────────────────────

    [Fact]
    public void A_start_opens_to_the_task_the_owner_sealed()
    {
        var opened = Sealer.OpenStart(_keys.Start(issuedAt: Now));

        Assert.Equal(new OpenedStart("run-1", "task-1", "workspace-1", "Run the tests", "Please run them."), opened);
    }

    /// <summary>
    /// The gateway attaches another genuine task's sealed text to this command. The sealed task opens -
    /// it was sealed for the task id it now claims - but the owner's authorization for THIS command
    /// names a different task, and the Host acts only on the two agreeing.
    /// </summary>
    [Fact]
    public void A_start_carrying_another_tasks_sealed_text_is_refused()
    {
        var genuine = _keys.Start(issuedAt: Now);
        var other = _keys.Start(commandId: "command-other", taskId: "task-2", title: "Delete the backups", issuedAt: Now);

        var moved = Payload<StartTaskPayload>(genuine) with
        {
            TaskId = "task-2",
            SealedTask = Payload<StartTaskPayload>(other).SealedTask
        };

        var refused = Assert.Throws<CommandRefusedException>(() => Sealer.OpenStart(genuine with { Payload = RemoteJson.Serialize(moved) }));
        Assert.Contains("task", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The same, for a gateway that sends a genuine task to a different workspace.</summary>
    [Fact]
    public void A_start_moved_to_another_workspace_is_refused()
    {
        var genuine = _keys.Start(issuedAt: Now);
        var elsewhere = _keys.Start(commandId: "command-other", workspaceId: "workspace-2", issuedAt: Now);

        var moved = Payload<StartTaskPayload>(genuine) with
        {
            WorkspaceId = "workspace-2",
            SealedTask = Payload<StartTaskPayload>(elsewhere).SealedTask
        };

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenStart(genuine with { Payload = RemoteJson.Serialize(moved) }));
    }

    /// <summary>
    /// Freshness has an edge on each side. A day plus ten minutes of skew is still acted on, because
    /// that is how long the gateway keeps a command waiting for a computer that was asleep; ten
    /// minutes into the future is the most a browser's clock is allowed to be ahead.
    /// </summary>
    [Fact]
    public void A_start_is_acted_on_only_within_its_lifetime_and_the_clock_skew()
    {
        Sealer.OpenStart(_keys.Start(issuedAt: Now - RemoteProtocol.CommandLifetime - TimeSpan.FromMinutes(9)));
        Sealer.OpenStart(_keys.Start(issuedAt: Now + TimeSpan.FromMinutes(9)));

        Assert.Throws<CommandRefusedException>(() =>
            Sealer.OpenStart(_keys.Start(issuedAt: Now - RemoteProtocol.CommandLifetime - TimeSpan.FromMinutes(11))));
        Assert.Throws<CommandRefusedException>(() =>
            Sealer.OpenStart(_keys.Start(issuedAt: Now + TimeSpan.FromMinutes(11))));
    }

    // ── rule 2: an epoch this computer does not hold ────────────────────────

    [Fact]
    public void A_command_sealed_under_an_epoch_this_computer_does_not_hold_is_refused()
    {
        var stranger = new FixedHostKeys("host-1", HostKey.Create(7));

        var refused = Assert.Throws<CommandRefusedException>(() => Sealer.OpenStart(stranger.Start(issuedAt: Now)));

        Assert.Contains("sealed under a key this computer does not hold", refused.Message, StringComparison.Ordinal);
    }

    // ── rule 3: a decision is about the request it names ────────────────────

    [Fact]
    public void A_decision_opens_to_the_owners_answer()
    {
        var decision = Sealer.OpenDecision(_keys.Decide("approval-a", "hash-a", RemoteDecision.Allow, issuedAt: Now));

        Assert.Equal(("approval-a", "hash-a", RemoteDecision.Allow), (decision.ApprovalId, decision.ActionHash, decision.Decision));
    }

    /// <summary>
    /// An Allow the owner gave for approval A, placed by the gateway in a payload for approval B with
    /// B's hash. The envelope opens - it was sealed for this command id - and says A, so it is refused.
    /// </summary>
    [Fact]
    public void A_decision_for_another_approval_is_refused()
    {
        var forA = _keys.Decide("approval-a", "hash-a", RemoteDecision.Allow, issuedAt: Now);
        var forB = Payload<ResolveApprovalPayload>(forA) with { ApprovalId = "approval-b", ActionHash = "hash-b" };

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenDecision(forA with { Payload = RemoteJson.Serialize(forB) }));
    }

    [Fact]
    public void A_decision_for_another_action_is_refused()
    {
        var genuine = _keys.Decide("approval-a", "hash-a", RemoteDecision.Allow, issuedAt: Now);
        var rehashed = Payload<ResolveApprovalPayload>(genuine) with { ActionHash = "hash-of-something-else" };

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenDecision(genuine with { Payload = RemoteJson.Serialize(rehashed) }));
    }

    [Fact]
    public void A_cancel_opens_only_for_the_run_it_names()
    {
        Assert.Equal("run-1", Sealer.OpenCancel(_keys.Cancel(issuedAt: Now)).RunId);

        var genuine = _keys.Cancel(issuedAt: Now);
        var retargeted = Payload<CancelRunPayload>(genuine) with { RunId = "run-2" };

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenCancel(genuine with { Payload = RemoteJson.Serialize(retargeted) }));
    }

    [Fact]
    public void Device_commands_open_and_are_refused_when_old()
    {
        var revocation = DeviceCommand("command-r", CommandKind.RevokeDevice, new DeviceRevocation("device-1", Now));
        var endorsement = DeviceCommand("command-e", CommandKind.EndorseDevice, new DeviceEndorsement("device-2", "pub", "Phone", Now));

        Assert.Equal("device-1", Sealer.OpenRevocation(revocation).DeviceId);
        Assert.Equal("device-2", Sealer.OpenEndorsement(endorsement).DeviceId);

        var old = DeviceCommand("command-o", CommandKind.RevokeDevice, new DeviceRevocation("device-1", Now.AddHours(-25)));
        Assert.Throws<CommandRefusedException>(() => Sealer.OpenRevocation(old));
    }

    // ── rule 4 (review focus 3): the Host refuses a forged command ──────────

    /// <summary>
    /// The gateway copies a genuine start under a new command id, hoping to run the task again. The
    /// command id is part of what the authorization was sealed under, so it no longer opens.
    /// </summary>
    [Fact]
    public void Review_focus_3_a_sealed_start_copied_to_another_command_id_does_not_open()
    {
        var genuine = _keys.Start(issuedAt: Now);

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenStart(genuine with { Id = "command-copied" }));
    }

    /// <summary>
    /// A command sealed for host B, delivered to host A. Same key bytes on purpose: what refuses it is
    /// the host id in the associated data, not the accident of the two computers having different keys.
    /// </summary>
    [Fact]
    public void Review_focus_3_a_command_sealed_for_another_host_is_refused()
    {
        var hostB = new FixedHostKeys("host-B", _keys.Current);

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenStart(hostB.Start(issuedAt: Now)));
        Assert.Throws<CommandRefusedException>(() => Sealer.OpenDecision(hostB.Decide("a", "h", RemoteDecision.Allow, issuedAt: Now)));
    }

    /// <summary>A genuine command the gateway kept for a day and then delivered. Too old to believe.</summary>
    [Fact]
    public void Review_focus_3_a_command_issued_25_hours_ago_is_refused()
    {
        Assert.Throws<CommandRefusedException>(() => Sealer.OpenStart(_keys.Start(issuedAt: Now.AddHours(-25))));
        Assert.Throws<CommandRefusedException>(() => Sealer.OpenCancel(_keys.Cancel(issuedAt: Now.AddHours(-25))));
        Assert.Throws<CommandRefusedException>(() =>
            Sealer.OpenDecision(_keys.Decide("a", "h", RemoteDecision.Allow, issuedAt: Now.AddHours(-25))));
    }

    /// <summary>
    /// The same command id, delivered again. Not the sealer's job: the inbox refuses a command id it
    /// has already written down, across a restart, which is the gap where a crash lands.
    /// </summary>
    [Fact]
    public void Review_focus_3_a_replayed_command_id_is_recognised_after_a_restart()
    {
        var command = _keys.Start();
        var database = Path.Combine(_folder, "remote.db");

        using (var store = new HostStore(database))
        {
            Assert.True(store.Accept(command));
        }

        using (var store = new HostStore(database))
        {
            Assert.False(store.Accept(command));
        }
    }

    /// <summary>A payload that is not even an envelope is a refusal, not a crash of the command loop.</summary>
    [Fact]
    public void A_command_that_is_not_sealed_is_refused()
    {
        var plain = _keys.Command("command-p", CommandKind.CancelRun, RemoteJson.Serialize(new CancelRunPayload("run-1", "stop it")));

        Assert.Throws<CommandRefusedException>(() => Sealer.OpenCancel(plain));
        Assert.Throws<CommandRefusedException>(() => Sealer.OpenCancel(plain with { Payload = "not json" }));
    }

    // ── what the Host sends ─────────────────────────────────────────────────

    /// <summary>
    /// An event's detail opens only as the event it was sealed for. A gateway that shows run 1's
    /// sentence under run 2, or moves a failure's reason onto a later progress line, gets nothing.
    /// </summary>
    [Fact]
    public void An_event_detail_opens_only_as_the_event_it_was_sealed_for()
    {
        var detail = Sealer.Detail("run-1", 3, RemoteEventKind.Failed, "The provider is down.");

        Assert.Equal("The provider is down.",
            _keys.OpenDetail(new HostEvent("e", "run-1", 3, RemoteEventKind.Failed, detail)));
        Assert.ThrowsAny<CryptographicException>(() =>
            _keys.OpenDetail(new HostEvent("e", "run-1", 4, RemoteEventKind.Failed, detail)));
        Assert.ThrowsAny<CryptographicException>(() =>
            _keys.OpenDetail(new HostEvent("e", "run-2", 3, RemoteEventKind.Failed, detail)));
        Assert.ThrowsAny<CryptographicException>(() =>
            _keys.OpenDetail(new HostEvent("e", "run-1", 3, RemoteEventKind.Progress, detail)));
    }

    /// <summary>
    /// The workspace list a computer publishes names folders by id and seals what a person called
    /// them: a folder's name is often a client's or a project's.
    /// </summary>
    [Fact]
    public void Published_workspaces_carry_their_names_sealed()
    {
        using var fx = new EngineFixture();
        var entry = new WorkspaceEntry("Client X billing", fx.Root, DateTimeOffset.UtcNow);

        var published = Assert.Single(RemoteAccessService.Publishable([entry], Sealer));

        Assert.DoesNotContain("Client", published.SealedName, StringComparison.Ordinal);
        Assert.Equal("Client X billing", _keys.Current.OpenText(published.SealedName, Ad.Workspace("host-1", published.Id)));
    }

    // ── rule 6: a permission request carries what the card shows and what the hash covers ──

    /// <summary>
    /// The desktop's card shows FullText; the action hash covers ArgumentsJson. Both travel sealed, so
    /// the browser can show the one and recompute the hash over the other, and offer Allow only when
    /// it matches (spec section 6, permission integrity). Without ArgumentsJson in the envelope a
    /// browser could only trust the hash the gateway handed it.
    /// </summary>
    [Fact]
    public async Task A_permission_request_seals_what_the_card_shows_and_what_the_hash_covers()
    {
        using var store = new HostStore(Path.Combine(_folder, "remote.db"));
        store.Accept(_keys.Start());
        store.BeginRun("command-1", "run-1");

        var request = new DecisionRequest(
            Guid.NewGuid(), "Approve tool 'write_file'?", "short form",
            [new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny")],
            RecommendedOptionId: "allow",
            FullDetail: "write_file a.txt - the complete action",
            Action: new BoundAction(Guid.NewGuid(), "call-1", "write_file", """{"path":"a.txt"}""", "C:/work"));

        await new RemoteDecisionHandler(
                new ScriptedDecisionHandler("allow"), store, new RemoteApprovals(), Sealer, "run-1", TimeSpan.FromSeconds(30))
            .RequestAsync(request, CancellationToken.None);

        var asked = Assert.Single(Drain(store), e => e.Kind == RemoteEventKind.ApprovalRequested);
        var card = asked.Approval!;
        var action = _keys.OpenAction("run-1", card);

        Assert.Equal(new SealedAction("write_file", """{"path":"a.txt"}""", "write_file a.txt - the complete action", "C:/work",
            "Approve tool 'write_file'?"), action);
        Assert.Equal(ActionIdentity.Hash("run-1", card.ToolCallId, action.Tool, action.WorkingDirectory, action.ArgumentsJson),
            card.ActionHash);
        Assert.Equal("Approve tool 'write_file'?", _keys.OpenDetail(asked));
    }

    // ── the desktop before it has keys ──────────────────────────────────────

    /// <summary>
    /// Until this computer has been paired under protocol 2 it has no key, and connecting would only
    /// publish workspaces nobody can read and receive commands it cannot open. It says why instead.
    /// </summary>
    [Fact]
    public async Task Unpaired_service_says_so_and_does_not_connect()
    {
        var settings = new RemoteAccessSettings { Enabled = true, GatewayUrl = "https://remote.example.test", Token = "token" };
        await using var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], new ScriptedDecisionHandler("allow"),
            Path.Combine(_folder, "remote.db"));

        service.Start();

        Assert.Equal(RemoteAccessService.NeedsPairing, service.Status);
        Assert.Null(typeof(RemoteAccessService).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static T Payload<T>(HostCommand command) => RemoteJson.Deserialize<T>(command.Payload);

    private HostCommand DeviceCommand<T>(string commandId, CommandKind kind, T record)
        => _keys.Command(commandId, kind, RemoteJson.Serialize(new DevicePayload(
            _keys.Current.SealText(RemoteJson.Serialize(record), Ad.Command("host-1", commandId, kind)))));

    private static List<HostEvent> Drain(HostStore store)
    {
        var events = new List<HostEvent>();
        while (store.NextOwed().FirstOrDefault() is { } owed)
        {
            events.Add(owed.Event);
            store.Discard(owed.EventId);
        }

        return events;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

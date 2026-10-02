namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

/// <summary>
/// One computer with one epoch key, for tests that need the Host to seal and open without a key store.
///
/// <para>It also plays the trusted browser: the helpers below seal commands the way a device holding this
/// key would, so a test can hand the Host a genuine command and then a forged variant of it.</para>
/// </summary>
internal sealed class FixedHostKeys(string hostId, HostKey key) : IHostKeys
{
    public FixedHostKeys() : this("host-1", HostKey.Create(1)) { }

    public string HostId { get; } = hostId;

    public HostKey Current { get; } = key;

    public HostKey? Epoch(uint epoch) => epoch == Current.Epoch ? Current : null;

    /// <summary>The sealer the Host would use, on the real clock unless a test needs another.</summary>
    public Sealer Sealer(TimeProvider? clock = null) => new(this, clock ?? TimeProvider.System);

    /// <summary>An event's detail, opened as the browser would open it. Null when it carries none.</summary>
    public string? OpenDetail(HostEvent published)
        => published.SealedDetail is null
            ? null
            : Current.OpenText(published.SealedDetail, Ad.Event(HostId, published.RunId, published.Sequence, published.Kind));

    /// <summary>A permission request's sealed action, opened and read.</summary>
    public SealedAction OpenAction(string runId, ApprovalRequest request)
        => RemoteJson.Deserialize<SealedAction>(Current.OpenText(request.SealedAction,
            Ad.Approval(HostId, runId, request.ApprovalId, request.ToolCallId, request.ActionHash, request.RemoteDecidable)));

    // ── commands as a trusted browser seals them ────────────────────────────

    public HostCommand Start(
        string commandId = "command-1", string runId = "run-1", string taskId = "task-1",
        string workspaceId = "workspace-1", string title = "Run the tests", string prompt = "Please run them.",
        DateTimeOffset? issuedAt = null)
    {
        var task = Current.SealText(RemoteJson.Serialize(new SealedTask(title, prompt)), Ad.Task(HostId, taskId, workspaceId));
        var start = Current.SealText(
            RemoteJson.Serialize(new StartAuthorization(taskId, workspaceId, issuedAt ?? DateTimeOffset.UtcNow)),
            Ad.Command(HostId, commandId, CommandKind.StartTask));

        return Command(commandId, CommandKind.StartTask,
            RemoteJson.Serialize(new StartTaskPayload(runId, taskId, workspaceId, task, start)));
    }

    public HostCommand Cancel(string commandId = "command-2", string runId = "run-1", DateTimeOffset? issuedAt = null)
        => Command(commandId, CommandKind.CancelRun, RemoteJson.Serialize(new CancelRunPayload(runId,
            Current.SealText(RemoteJson.Serialize(new CancelAuthorization(runId, issuedAt ?? DateTimeOffset.UtcNow)),
                Ad.Command(HostId, commandId, CommandKind.CancelRun)))));

    public HostCommand Decide(
        string approvalId, string actionHash, RemoteDecision decision,
        string commandId = "command-3", string runId = "run-1", DateTimeOffset? issuedAt = null)
        => Command(commandId, CommandKind.ResolveApproval, RemoteJson.Serialize(new ResolveApprovalPayload(
            approvalId, runId, actionHash,
            Current.SealText(
                RemoteJson.Serialize(new DecisionAuthorization(approvalId, actionHash, decision, issuedAt ?? DateTimeOffset.UtcNow)),
                Ad.Command(HostId, commandId, CommandKind.ResolveApproval)))));

    public HostCommand Revoke(string deviceId, string commandId = "command-r", DateTimeOffset? issuedAt = null)
        => Device(commandId, CommandKind.RevokeDevice, new DeviceRevocation(deviceId, issuedAt ?? DateTimeOffset.UtcNow));

    public HostCommand Endorse(
        string deviceId, string devicePublic, string label, string commandId = "command-e", DateTimeOffset? issuedAt = null)
        => Device(commandId, CommandKind.EndorseDevice,
            new DeviceEndorsement(deviceId, devicePublic, label, issuedAt ?? DateTimeOffset.UtcNow));

    private HostCommand Device<T>(string commandId, CommandKind kind, T record)
        => Command(commandId, kind, RemoteJson.Serialize(new DevicePayload(
            Current.SealText(RemoteJson.Serialize(record), Ad.Command(HostId, commandId, kind)))));

    public HostCommand Command(string commandId, CommandKind kind, string payload)
        => new(commandId, HostId, kind, payload, CommandStatus.PendingDelivery,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(RemoteProtocol.CommandLifetime));
}

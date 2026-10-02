namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Host;

/// <summary>
/// A trusted browser of one computer: it holds that computer's key and seals what a person sends the
/// way the panel will, with <see cref="HostKey"/> and the associated data of <see cref="Ad"/>, and
/// opens what the computer sealed for it.
///
/// <para>Without it the gateway tests could only hand the gateway envelopes of random keys - which
/// proves the gateway passes them along, and not that the computer at the other end can open what
/// arrived. That second half is what the end-to-end test is for.</para>
/// </summary>
internal sealed class TestBrowser(string hostId, HostKey key)
{
    /// <summary>A browser and its computer sharing a fresh key, as pairing leaves them.</summary>
    public TestBrowser(string hostId) : this(hostId, HostKey.Create(1)) { }

    public string HostId { get; } = hostId;

    /// <summary>The same key, held by the computer.</summary>
    public FixedHostKeys Computer { get; } = new(hostId, key);

    // ── what the person sends ───────────────────────────────────────────────

    public string Task(string taskId, string workspaceId, string title, string prompt)
        => key.SealText(RemoteJson.Serialize(new SealedTask(title, prompt)), Ad.Task(HostId, taskId, workspaceId));

    public string Start(string commandId, string taskId, string workspaceId)
        => key.SealText(
            RemoteJson.Serialize(new StartAuthorization(taskId, workspaceId, DateTimeOffset.UtcNow)),
            Ad.Command(HostId, commandId, CommandKind.StartTask));

    public string Cancel(string commandId, string runId)
        => key.SealText(
            RemoteJson.Serialize(new CancelAuthorization(runId, DateTimeOffset.UtcNow)),
            Ad.Command(HostId, commandId, CommandKind.CancelRun));

    public string Decision(string commandId, string approvalId, string actionHash, RemoteDecision decision)
        => key.SealText(
            RemoteJson.Serialize(new DecisionAuthorization(approvalId, actionHash, decision, DateTimeOffset.UtcNow)),
            Ad.Command(HostId, commandId, CommandKind.ResolveApproval));

    public string Revocation(string commandId, string deviceId)
        => key.SealText(
            RemoteJson.Serialize(new DeviceRevocation(deviceId, DateTimeOffset.UtcNow)),
            Ad.Command(HostId, commandId, CommandKind.RevokeDevice));

    // ── what the computer sent ──────────────────────────────────────────────

    /// <summary>An event's sentence, opened as the browser opens it: under the run, sequence and kind it travelled with.</summary>
    public string OpenDetail(HostEvent published)
        => key.OpenText(
            published.SealedDetail ?? throw new InvalidOperationException("The event carries no detail."),
            Ad.Event(HostId, published.RunId, published.Sequence, published.Kind));

    public SealedTask OpenTask(TaskView task)
        => RemoteJson.Deserialize<SealedTask>(key.OpenText(task.Sealed, Ad.Task(HostId, task.Id, task.WorkspaceId)));

    public string OpenWorkspace(WorkspaceView workspace)
        => key.OpenText(workspace.SealedName, Ad.Workspace(HostId, workspace.Id));

    /// <summary>A finished run's summary, opened under the terminal event it was copied from.</summary>
    public string OpenSummary(RunView run, RemoteEventKind ending)
        => key.OpenText(
            run.SealedSummary ?? throw new InvalidOperationException("The run carries no summary."),
            Ad.Event(HostId, run.Id,
                run.SummarySequence ?? throw new InvalidOperationException("The summary has no sequence."),
                ending));
}

/// <summary>One computer with one epoch key, so the Host can seal and open without a key store.</summary>
internal sealed class FixedHostKeys(string hostId, HostKey key) : IHostKeys
{
    public string HostId { get; } = hostId;

    public HostKey Current { get; } = key;

    public HostKey? Epoch(uint epoch) => epoch == Current.Epoch ? Current : null;

    /// <summary>The sealer the Host would use.</summary>
    public Sealer Sealer() => new(this, TimeProvider.System);
}

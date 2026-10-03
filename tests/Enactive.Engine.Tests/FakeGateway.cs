namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

/// <summary>
/// The far end, doing whatever a test needs it to, and writing down every call in the order it came.
///
/// <para>Shared by the delivery, key administration and probe tests so the three exercise the Host
/// against one shape of gateway. The service under test calls it from its own task, so the call list
/// is kept under a lock and read as a copy.</para>
/// </summary>
internal sealed class FakeGateway : IGatewayConnection
{
    private readonly List<string> _calls = [];
    private readonly List<string> _invites = [];
    private readonly List<string> _answered = [];
    private readonly List<string> _revoked = [];
    private readonly List<EnrollmentView> _enrollments = [];

    public List<HostCommand> Pending { get; init; } = [];

    /// <summary>Set to false to have the connection close, as a dropped socket does.</summary>
    public bool IsOpen { get; set; } = true;

    public List<HostEvent> Published { get; } = [];

    public List<string> Acknowledged { get; } = [];

    /// <summary>Every PublishGrants call, as the grants it carried.</summary>
    public List<IReadOnlyList<KeyGrant>> GrantCalls { get; } = [];

    /// <summary>A coded refusal of an event, as the gateway would send it.</summary>
    public string? Refuse { get; set; }

    /// <summary>Something that is not a refusal at all - a closed socket, a timeout.</summary>
    public Exception? Throw { get; set; }

    /// <summary>What Hello answers instead of yes.</summary>
    public Exception? HelloRefusal { get; set; }

    /// <summary>What Sync answers instead of the pending commands.</summary>
    public Exception? SyncRefusal { get; set; }

    /// <summary>Runs on every Sync, after it is recorded - how a test closes the connection at a chosen moment.</summary>
    public Action<FakeGateway>? OnSync { get; set; }

    /// <summary>Decides, per call, whether a PublishGrants is refused, and how.</summary>
    public Func<IReadOnlyList<KeyGrant>, Exception?>? GrantRefusal { get; set; }

    /// <summary>What CreateInvite answers instead of yes - an invitation limit, say.</summary>
    public Exception? InviteRefusal { get; set; }

    /// <summary>The invitation ids registered so far, in order.</summary>
    public IReadOnlyList<string> InvitesCreated
    {
        get { lock (_calls) return [.. _invites]; }
    }

    /// <summary>The invitation ids said to be answered so far, in order.</summary>
    public IReadOnlyList<string> Answered
    {
        get { lock (_calls) return [.. _answered]; }
    }

    /// <summary>
    /// Hands this enrollment over on every Enrollments call from now on - answered or not, as a
    /// gateway that lies would. What the Host does with an answer it already handled is its own to get
    /// right, not something the gateway's honesty may cover for.
    /// </summary>
    public void Enroll(EnrollmentView enrollment)
    {
        lock (_calls) _enrollments.Add(enrollment);
    }

    /// <summary>The devices this computer asked the gateway to stop serving, in order.</summary>
    public IReadOnlyList<string> Revoked
    {
        get { lock (_calls) return [.. _revoked]; }
    }

    /// <summary>The methods called so far, in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_calls) return [.. _calls]; }
    }

    public Task HelloAsync(int protocolVersion, CancellationToken ct)
    {
        Record("Hello");
        return HelloRefusal is null ? Task.CompletedTask : Task.FromException(HelloRefusal);
    }

    public Task<IReadOnlyList<HostCommand>> SyncAsync(IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct)
    {
        Record("Sync");
        OnSync?.Invoke(this);
        return SyncRefusal is null
            ? Task.FromResult<IReadOnlyList<HostCommand>>(Pending)
            : Task.FromException<IReadOnlyList<HostCommand>>(SyncRefusal);
    }

    /// <summary>Decides, per command id, whether its Acknowledge fails, and how - a dropped socket, say.</summary>
    public Func<string, Exception?>? AcknowledgeFailure { get; set; }

    public Task AcknowledgeAsync(string commandId, CancellationToken ct)
    {
        Record("Acknowledge");
        if (AcknowledgeFailure?.Invoke(commandId) is { } failure) return Task.FromException(failure);

        Acknowledged.Add(commandId);
        return Task.CompletedTask;
    }

    public Task PublishAsync(HostEvent published, CancellationToken ct)
    {
        Record("Publish");

        if (Throw is not null)
        {
            throw Throw;
        }

        if (Refuse is not null)
        {
            throw new GatewayRefusedException(Refuse, "refused by the fake gateway");
        }

        Published.Add(published);
        return Task.CompletedTask;
    }

    public Task PublishGrantsAsync(IReadOnlyList<KeyGrant> grants, CancellationToken ct)
    {
        Record("PublishGrants");

        if (GrantRefusal?.Invoke(grants) is { } refusal)
        {
            throw refusal;
        }

        GrantCalls.Add([.. grants]);
        return Task.CompletedTask;
    }

    public Task CreateInviteAsync(string inviteId, CancellationToken ct)
    {
        Record("CreateInvite");
        if (InviteRefusal is not null) return Task.FromException(InviteRefusal);

        lock (_calls) _invites.Add(inviteId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EnrollmentView>> EnrollmentsAsync(CancellationToken ct)
    {
        Record("Enrollments");
        lock (_calls) return Task.FromResult<IReadOnlyList<EnrollmentView>>([.. _enrollments]);
    }

    public Task AnsweredInviteAsync(string inviteId, CancellationToken ct)
    {
        Record("AnsweredInvite");
        lock (_calls) _answered.Add(inviteId);
        return Task.CompletedTask;
    }

    /// <summary>What RevokeDevice answers instead of yes - a dropped socket, or the gateway not having the device.</summary>
    public Exception? RevokeRefusal { get; set; }

    public Task RevokeDeviceAsync(string deviceId, CancellationToken ct)
    {
        Record("RevokeDevice");
        if (RevokeRefusal is not null) return Task.FromException(RevokeRefusal);

        lock (_calls) _revoked.Add(deviceId);
        return Task.CompletedTask;
    }

    private void Record(string call)
    {
        lock (_calls) _calls.Add(call);
    }
}

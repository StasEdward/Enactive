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

    public List<HostCommand> Pending { get; init; } = [];

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

    /// <summary>Decides, per call, whether a PublishGrants is refused, and how.</summary>
    public Func<IReadOnlyList<KeyGrant>, Exception?>? GrantRefusal { get; set; }

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
        return SyncRefusal is null
            ? Task.FromResult<IReadOnlyList<HostCommand>>(Pending)
            : Task.FromException<IReadOnlyList<HostCommand>>(SyncRefusal);
    }

    public Task AcknowledgeAsync(string commandId, CancellationToken ct)
    {
        Record("Acknowledge");
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
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EnrollmentView>> EnrollmentsAsync(CancellationToken ct)
    {
        Record("Enrollments");
        return Task.FromResult<IReadOnlyList<EnrollmentView>>([]);
    }

    public Task AnsweredInviteAsync(string inviteId, CancellationToken ct)
    {
        Record("AnsweredInvite");
        return Task.CompletedTask;
    }

    private void Record(string call)
    {
        lock (_calls) _calls.Add(call);
    }
}

namespace Enactive.Remote.Gateway;

using System.Security.Claims;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// The methods a Host calls. Every one of them takes its Host and that Host's owner from the
/// authenticated identity and never from an argument, so "act as another Host", or "act in another
/// person's account", is not a request that can be made rather than one that is refused.
/// </summary>
[Authorize(AuthenticationSchemes = HostAuthentication.SchemeName)]
public sealed class HostHub(
    HostService hosts, DeviceService devices, HostConnections connections, HostCallLimit limit,
    ILogger<HostHub> log) : Hub
{
    /// <summary>
    /// Takes the connection's place among its computer's, its account's and the gateway's
    /// (<see cref="HostConnections"/>), or closes it.
    ///
    /// <para>Closed, not answered: the handshake was answered before this runs, so there is no status
    /// left to send, and the Host treats a closed connection as one to dial again after its wait. How
    /// often a computer may start one is counted earlier, at the door, where a refusal can still be
    /// said (<see cref="RequestLimits.UseComputerRelease"/>). The log says which limit and which
    /// computer - an id, never its token - so an operator can tell a flood from a full gateway.</para>
    ///
    /// <para>An older connection closed for this one is logged too. An honest reconnect makes one such
    /// line now and then; a token used from two places, or a flood, makes a run of them, and without the
    /// line nothing on the operator's side showed it.</para>
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var access = Access;
        // The account connection budget follows persisted host overrides on the next connection.
        var effective = await hosts.EffectiveLimitsAsync(access, Context.ConnectionAborted);
        var refusal = connections.TryAdd(
            Context.ConnectionId, access.HostId, access.OwnerId, Context.Abort, out var replaced, effective);

        if (refusal != HostConnections.Refusal.None)
        {
            log.LogWarning("A connection of computer {HostId} was refused: {Refusal}.", access.HostId, refusal);
            Context.Abort();
            return;
        }

        for (var i = 0; i < replaced; i++)
        {
            log.LogInformation("Computer {HostId}: its oldest connection was closed for a newer one.", access.HostId);
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// The first thing a Host says: which protocol it speaks. Another version is refused with a code
    /// the Host treats as final and words a person can act on; without this, a Host of another version
    /// would see each of its calls refused for a different-looking reason and keep trying.
    ///
    /// <para>A check the Host asks for, not a gate in front of the other calls: a connection that
    /// never says hello is still served, so a Host that does not call it yet keeps working.</para>
    /// </summary>
    public Task<HostReply<bool>> Hello(int protocolVersion)
        => Guard(() => protocolVersion == RemoteProtocol.Version
            ? Task.FromResult(true)
            : throw GatewayFault.ProtocolMismatch());

    public Task<HostReply<IReadOnlyList<HostCommand>>> Sync(List<WorkspaceRef> workspaces)
        => Guard(() => hosts.SyncAsync(Access, workspaces, Context.ConnectionAborted));

    public Task<HostReply<bool>> Acknowledge(string commandId)
        => Guard(async () =>
        {
            await hosts.AcknowledgeAsync(Access, commandId, Context.ConnectionAborted);
            return true;
        });

    public Task<HostReply<bool>> Publish(HostEvent published)
        => Guard(async () =>
        {
            await hosts.PublishAsync(Access, published, Context.ConnectionAborted);
            return true;
        });

    /// <summary>
    /// Grants of this computer's own keys to its owner's devices: answering a pairing or an invitation, or
    /// a rotation. All of them are stored or none is.
    /// </summary>
    public Task<HostReply<bool>> PublishGrants(List<KeyGrant> grants)
        => Guard(async () =>
        {
            await devices.PublishGrantsAsync(Access, grants, Context.ConnectionAborted);
            return true;
        });

    /// <summary>
    /// An invitation for another device of this computer's owner, under an id the computer made. Its link,
    /// with the pairing secret, is the computer's to show; the gateway only keeps the id, for ten minutes.
    /// </summary>
    public Task<HostReply<bool>> CreateInvite(string id)
        => Guard(async () =>
        {
            await devices.CreateInviteAsync(Access, id, Context.ConnectionAborted);
            return true;
        });

    /// <summary>The answers to this computer's invitations that it has not said it handled.</summary>
    public Task<HostReply<IReadOnlyList<EnrollmentView>>> Enrollments()
        => Guard(() => devices.EnrollmentsAsync(Access, Context.ConnectionAborted));

    /// <summary>The computer has handled the answer to its invitation; it is not handed over again.</summary>
    public Task<HostReply<bool>> AnsweredInvite(string id)
        => Guard(async () =>
        {
            await devices.AnsweredInviteAsync(Access, id, Context.ConnectionAborted);
            return true;
        });

    /// <summary>
    /// The person removed a device of theirs on this computer: the gateway stops serving it, as when they
    /// remove it in a browser. Only a device of this computer's own owner; any other is refused like a
    /// missing one.
    /// </summary>
    public Task<HostReply<bool>> RevokeDevice(string deviceId)
        => Guard(async () =>
        {
            await devices.RevokeByComputerAsync(Access, deviceId, Context.ConnectionAborted);
            return true;
        });

    private string HostId => Context.UserIdentifier
        ?? throw new HubException("This connection has no identity.");

    /// <summary>
    /// Built afresh for each call from the connection's claims. The service re-reads the account and
    /// the computer inside every call's transaction, so what the claims said when the connection
    /// opened only names whose rows to look at; it never vouches that they are still allowed.
    /// </summary>
    private HostAccess Access => new(
        HostId,
        Context.User?.FindFirstValue(HostAuthentication.OwnerClaim)
            ?? throw new HubException("This connection has no owner."));

    /// <summary>
    /// Turns a refusal into an ANSWER rather than an exception.
    ///
    /// <para>An exception carries only its message to the other end, and that message does not
    /// survive: SignalR prefixes it, and outside Development it replaces it entirely. A code put
    /// inside it therefore reaches the Host mangled or not at all - and a Host that cannot read the
    /// code treats the refusal as a transport failure and retries it for ever, which is exactly the
    /// right rule applied to the wrong information. Found by the end-to-end test, which is the
    /// first thing that ever ran both halves against each other.</para>
    ///
    /// <para>An unexpected exception still escapes as one. It is not a refusal, nothing about it is
    /// classifiable, and dressing it up as a coded answer would tell the Host something false.</para>
    ///
    /// <para>Every call is counted against this computer's limit here, first, so a refused one costs no
    /// database work and comes back coded like any other refusal.</para>
    /// </summary>
    private async Task<HostReply<T>> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            limit.Take(HostId);
            return HostReply<T>.Ok(await action());
        }
        catch (GatewayFault fault)
        {
            return HostReply<T>.Refused(fault.ToContract());
        }
    }
}

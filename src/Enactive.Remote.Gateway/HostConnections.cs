namespace Enactive.Remote.Gateway;

using Enactive.Remote.Gateway.Services;

/// <summary>
/// The computers' open connections: so revoking a credential can close them rather than waiting for the Host to
/// notice at its next call, and so they are counted.
///
/// <para><b>Why counted here.</b> A computer's connection is let out from under the gateway's ceiling on requests
/// once it has authenticated (<see cref="RequestLimits.UseComputerRelease"/>), because it lasts as long as the
/// computer is online. Nothing counted it after that: one token opened two hundred and one connections, past the
/// ceiling itself, and all of them stayed open. They have a budget of their own instead, taken when the hub
/// accepts a connection and given back when it ends:</para>
/// <list type="bullet">
/// <item><see cref="RequestLimits.ConnectionsPerComputer"/> per computer, the OLDEST closed for a newer one;</item>
/// <item>twice <see cref="Limits.HostsPerUser"/> per account, past which a new connection is refused;</item>
/// <item>a ceiling for the whole gateway, past which a new connection is refused.</item>
/// </list>
/// <para>A computer that already holds a connection is never refused by the last two: its new one closes its
/// oldest and takes no new place.</para>
///
/// <para><b>Places in the hub, openings at the door.</b> A connection is not a request: over long polling it is
/// a negotiation and then a poll after a poll, and over a WebSocket it may skip the negotiation. The hub's
/// <c>OnConnectedAsync</c> is the one place every connection passes once, whatever its transport, with the
/// computer and its account known, so the places are taken there. What it cannot see is the work done before:
/// a negotiation never followed up, or a transport opened and never sent a handshake. Those are counted where
/// they start, per computer, by <see cref="ComputerOpeningLimit"/> in
/// <see cref="RequestLimits.UseComputerRelease"/> - where a refusal can still be answered with a status.</para>
///
/// <para><b>The count is the set of connections.</b> Each one is held under its id; taking a place adds it and
/// giving one back removes it, and removing an id that is not there does nothing. So a connection closed here -
/// for a newer one, or by <see cref="CloseAll"/> - gives its place back at once, and its own disconnect later
/// gives back nothing a second time.</para>
/// </summary>
public sealed class HostConnections
{
    private readonly long _perAccount;
    private readonly int _ceiling;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Open> _byId = new(StringComparer.Ordinal);

    /// <summary>Each computer's connections, the oldest first.</summary>
    private readonly Dictionary<string, List<Open>> _byHost = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _byOwner = new(StringComparer.Ordinal);

    /// <param name="limits">The account limits; an account holds twice its computers in connections.</param>
    /// <param name="ceiling">The whole gateway's; another only for a test that cannot open two thousand.</param>
    public HostConnections(Limits limits, int ceiling = RequestLimits.ComputerConnections)
    {
        // In a long: twice the "unlimited" int.MaxValue the tests about something else use overflows an int,
        // and a negative ceiling would refuse every account its first connection.
        _perAccount = (long)limits.HostsPerUser * RequestLimits.ConnectionsPerComputer;
        _ceiling = ceiling;
    }

    /// <summary>Why a connection was refused, or <see cref="Refusal.None"/> when it was let in.</summary>
    public enum Refusal
    {
        None,

        /// <summary>The account holds twice its computers in connections already.</summary>
        AccountFull,

        /// <summary>The gateway holds its ceiling of computers' connections already.</summary>
        GatewayFull
    }

    /// <summary>The computers' connections open now, the whole gateway's.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>The connections open now of one computer.</summary>
    public int CountOf(string hostId)
    {
        lock (_lock)
        {
            return _byHost.TryGetValue(hostId, out var open) ? open.Count : 0;
        }
    }

    /// <summary>The connections open now of one account's computers.</summary>
    public int CountInAccount(string ownerId)
    {
        lock (_lock)
        {
            return _byOwner.GetValueOrDefault(ownerId);
        }
    }

    /// <summary>
    /// Takes a place for a connection the hub has just accepted, or answers why not; a refused connection holds
    /// nothing and is the caller's to close. A computer at <see cref="RequestLimits.ConnectionsPerComputer"/>
    /// has its oldest connection closed to make room, and so does one holding any connection when its account or
    /// the gateway is full; neither its account nor the gateway notices, as one place is given back for the one
    /// taken.
    /// </summary>
    /// <param name="abort">Closes this connection, when a newer one or a revocation needs it closed.</param>
    /// <param name="replaced">How many of the computer's older connections were closed for this one.</param>
    public Refusal TryAdd(string connectionId, string hostId, string ownerId, Action abort, out int replaced)
    {
        var closing = new List<Action>();

        lock (_lock)
        {
            // SignalR never reuses an id. Were one ever added twice, the second would overwrite the first's entry
            // and leave its counts behind, a place nobody can give back.
            if (_byId.TryGetValue(connectionId, out var again))
            {
                Forget(again);
            }

            var mine = _byHost.GetValueOrDefault(hostId);
            var held = mine?.Count ?? 0;

            var full = _byOwner.GetValueOrDefault(ownerId) >= _perAccount ? Refusal.AccountFull
                : _byId.Count >= _ceiling ? Refusal.GatewayFull
                : Refusal.None;

            if (full != Refusal.None && held == 0)
            {
                replaced = 0;
                return full;
            }

            // At its own limit the oldest goes; with the account or the gateway full, so does the oldest of
            // one or more. Refused instead, a computer whose only connection had died unnoticed could not come
            // back until the gateway timed the dead one out - and a full account or gateway stayed full of the
            // dead, which is exactly when it matters that an honest reconnect gets in.
            var keep = full == Refusal.None ? RequestLimits.ConnectionsPerComputer - 1 : held - 1;

            while (mine is not null && mine.Count > keep)
            {
                var oldest = mine[0];
                Forget(oldest);
                closing.Add(oldest.Abort);
            }

            var added = new Open(connectionId, hostId, ownerId, abort);
            _byId[connectionId] = added;
            _byOwner[ownerId] = _byOwner.GetValueOrDefault(ownerId) + 1;

            if (!_byHost.TryGetValue(hostId, out mine))
            {
                _byHost[hostId] = mine = [];
            }

            mine.Add(added);
        }

        CloseEach(closing);
        replaced = closing.Count;
        return Refusal.None;
    }

    /// <summary>Gives back the place a connection held; nothing when it held none, or gave it back already.</summary>
    public void Remove(string connectionId)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(connectionId, out var open))
            {
                Forget(open);
            }
        }
    }

    /// <summary>
    /// Closes every connection of the computer - revoked, or its account deleted - and gives their places back
    /// at once, rather than when each disconnect is noticed: the account may connect another computer now.
    /// </summary>
    public void CloseAll(string hostId)
    {
        List<Open> closing;

        lock (_lock)
        {
            if (!_byHost.TryGetValue(hostId, out var mine))
            {
                return;
            }

            closing = [.. mine];
            foreach (var open in closing)
            {
                Forget(open);
            }
        }

        CloseEach(closing.Select(open => open.Abort));
    }

    /// <summary>
    /// Closes connections whose places have already been given back, outside the lock: closing is the
    /// transport's business and may take its time, and nothing it does should wait on, or for, every other
    /// connection's count.
    ///
    /// <para>Each on its own. A close that throws - the connection already on its way out - is a connection
    /// that is going anyway, and its place was given back before it was asked; let through, the exception left
    /// <see cref="CloseAll"/> with a revoked computer's other connections still open, and failed the start of
    /// the newer connection a close was made for.</para>
    /// </summary>
    private static void CloseEach(IEnumerable<Action> closing)
    {
        foreach (var close in closing)
        {
            try
            {
                close();
            }
            catch (Exception)
            {
                // Going anyway; see above.
            }
        }
    }

    /// <summary>
    /// Takes one connection out of every count, under the lock. An entry that falls to nothing is removed, so a
    /// computer or an account that was ever connected is not remembered for ever.
    /// </summary>
    private void Forget(Open open)
    {
        _byId.Remove(open.ConnectionId);

        if (_byHost.TryGetValue(open.HostId, out var mine))
        {
            mine.Remove(open);

            if (mine.Count == 0)
            {
                _byHost.Remove(open.HostId);
            }
        }

        var left = _byOwner.GetValueOrDefault(open.OwnerId) - 1;

        if (left > 0)
        {
            _byOwner[open.OwnerId] = left;
        }
        else
        {
            _byOwner.Remove(open.OwnerId);
        }
    }

    private sealed record Open(string ConnectionId, string HostId, string OwnerId, Action Abort);
}

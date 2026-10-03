namespace Enactive.Remote.Gateway;

using System.Threading.RateLimiting;
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
/// <item>a ceiling for the whole gateway, past which a new connection is refused;</item>
/// <item><see cref="RequestLimits.ConnectionsPerMinute"/> new connections per computer, past which a new one is
/// refused.</item>
/// </list>
///
/// <para><b>Why in the hub and not at the door.</b> A connection is not a request: over long polling it is a
/// negotiation and then a poll after a poll, and over a WebSocket it may skip the negotiation. The hub's
/// <c>OnConnectedAsync</c> is the one place every connection passes once, whatever its transport, with the
/// computer and its account known. A connection that never gets there - a handshake that failed, a negotiation
/// never followed up - was never counted, so it has nothing to give back.</para>
///
/// <para><b>The count is the set of connections.</b> Each one is held under its id; taking a place adds it and
/// giving one back removes it, and removing an id that is not there does nothing. So a connection closed here -
/// for a newer one, or by <see cref="CloseAll"/> - gives its place back at once, and its own disconnect later
/// gives back nothing a second time.</para>
/// </summary>
public sealed class HostConnections : IDisposable
{
    private readonly long _perAccount;
    private readonly int _ceiling;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Open> _byId = new(StringComparer.Ordinal);

    /// <summary>Each computer's connections, the oldest first.</summary>
    private readonly Dictionary<string, List<Open>> _byHost = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _byOwner = new(StringComparer.Ordinal);

    /// <summary>
    /// A bucket rather than a minute's window, like <see cref="HostCallLimit"/>: a Host reconnects with a growing
    /// wait, and under a window one that spent its connections early would be shut out to the minute's end
    /// however long it then waited. Refilled steadily, its next attempt after a pause finds room.
    /// </summary>
    private readonly PartitionedRateLimiter<string> _opening = PartitionedRateLimiter.Create<string, string>(
        hostId => RateLimitPartition.GetTokenBucketLimiter(hostId, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = RequestLimits.ConnectionsPerMinute,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1) / RequestLimits.ConnectionsPerMinute,
            AutoReplenishment = true,
            QueueLimit = 0
        }));

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

        /// <summary>The computer opened <see cref="RequestLimits.ConnectionsPerMinute"/> lately.</summary>
        TooOften,

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
    /// has its oldest connection closed to make room, which neither its account nor the gateway notices: one
    /// place is given back for the one taken.
    /// </summary>
    /// <param name="abort">Closes this connection, when a newer one or a revocation needs it closed.</param>
    public Refusal TryAdd(string connectionId, string hostId, string ownerId, Action abort)
    {
        // Counted before anything else, refused ones too: what the rate protects is the work of a connection
        // being made, and a refused one has cost that already.
        using (var lease = _opening.AttemptAcquire(hostId))
        {
            if (!lease.IsAcquired)
            {
                return Refusal.TooOften;
            }
        }

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

            if (mine is null || mine.Count < RequestLimits.ConnectionsPerComputer)
            {
                if (_byOwner.GetValueOrDefault(ownerId) >= _perAccount)
                {
                    return Refusal.AccountFull;
                }

                if (_byId.Count >= _ceiling)
                {
                    return Refusal.GatewayFull;
                }
            }

            while (mine is not null && mine.Count >= RequestLimits.ConnectionsPerComputer)
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

        // Outside the lock: closing a connection is the transport's business and may take its time, and nothing
        // it does should wait on, or for, every other connection's count.
        foreach (var close in closing)
        {
            close();
        }

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

        foreach (var open in closing)
        {
            open.Abort();
        }
    }

    public void Dispose() => _opening.Dispose();

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

namespace Enactive.Remote.Gateway;

using System.Globalization;
using System.Threading.RateLimiting;
using Enactive.Remote.Gateway.Accounts;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// How much of the gateway one caller may use: each person's API, each address's sign-ins, each
/// computer's hub calls, and every request together.
///
/// <para><b>None of these is authorization.</b> A limit decides how often, never whether: a request it
/// lets through is still checked by the session, the account and the ownership of every row it names,
/// and a request it refuses would have been answered the same way a moment later. Nothing may treat
/// "it got past the limit" as "it was allowed".</para>
///
/// <para>Each is counted per the thing that is asking. The single shared lockout this replaces let one
/// stranger's failed sign-ins keep every account out, and a limit counted for everybody together is
/// that lockout again under another name.</para>
/// </summary>
public static class RequestLimits
{
    /// <summary>The sign-in endpoints' policy, per caller address.</summary>
    public const string Auth = "auth";

    /// <summary>The person's API's policy, per account.</summary>
    public const string Api = "api";

    /// <summary>The session endpoint's policy, per account or, before signing in, per address.</summary>
    public const string Session = "session";

    /// <summary>
    /// Sign-in requests under <c>/auth</c> (start and complete) and the development sign-in, per address
    /// and minute. A sign-in is two of them; twenty leaves room for retries and several people behind
    /// one address, and is far too few to guess anything with. The providers' callbacks have their own
    /// limit (<see cref="ExternalSignIn.CallbacksPerMinute"/>), which has to run before authentication.
    /// </summary>
    public const int AuthPerMinute = 20;

    /// <summary>
    /// Calls to the person's API, per account and minute. The panel polls its state every few seconds
    /// and sends a handful of commands; three hundred is several busy tabs, and keeps one account's
    /// runaway script from taking the database's time from everybody else.
    /// </summary>
    public const int ApiPerMinute = 300;

    /// <summary>
    /// Hub calls per computer and minute, on average (see <see cref="HostCallLimit"/>). A Host syncs
    /// every fifteen seconds and sends a run's events out every two; this is two calls a second, well
    /// above a busy run, and stops a computer stuck in a loop from flooding its account's line and the
    /// database.
    /// </summary>
    public const int HubCallsPerMinute = 120;

    /// <summary>
    /// Requests in progress at once, for the whole gateway. Every per-caller limit above can be met by
    /// many callers at once - a different address each time, each request slow to finish - and without a
    /// ceiling on the total such a flood holds every connection and every database connection, and the
    /// gateway answers nobody. Well above what the people using one gateway make together.
    /// </summary>
    public const int ConcurrentRequests = 200;

    /// <summary>
    /// Connections one computer may hold open at once (see <see cref="HostConnections"/>). A computer's
    /// connection is let out from under <see cref="ConcurrentRequests"/> once it has authenticated, and nothing
    /// counted it after that: one token opened two hundred and one connections and every one stayed open, each
    /// holding memory, a socket and a long poll. Two, not one: a Host whose connection dropped without either end
    /// noticing reconnects while the gateway still holds the dead one, and that reconnect must get in. A third
    /// closes the oldest instead of being refused, so an honest reconnect always wins and a flood with one token
    /// holds two places.
    /// </summary>
    public const int ConnectionsPerComputer = 2;

    /// <summary>
    /// New connections per computer and minute, on average. A computer under <see cref="ConnectionsPerComputer"/>
    /// could still open one after another, each closing the one before: every one costs a credential lookup in
    /// the database and a connection set up and torn down, so churn is a flood as well. A Host reconnects with
    /// a growing wait, and ten in a minute is more than a bad network makes it do.
    /// </summary>
    public const int ConnectionsPerMinute = 10;

    /// <summary>
    /// Computers' connections open at once, for the whole gateway. The per-computer and per-account limits can
    /// each be met by many accounts at once, and the connections they add up to are what the process holds in
    /// memory and sockets - outside <see cref="ConcurrentRequests"/>, which they are let out of. A constant and
    /// not a setting, like that ceiling: it is about what one gateway's machine can hold, not about what one
    /// person may use, and a deployment that needs more needs a larger machine. Ten times the requests' ceiling,
    /// four hundred accounts at their default of five computers online once each.
    /// </summary>
    public const int ComputerConnections = 2000;

    /// <summary>The policies, the hub's limit and the front door's, and the coded refusal.</summary>
    public static IServiceCollection AddRequestLimits(this IServiceCollection services)
    {
        services.AddSingleton<HostCallLimit>();
        services.AddSingleton<FrontDoorLimit>();

        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (rejected, _) => RefuseAsync(rejected.HttpContext, rejected.Lease);

            // Per address: the people signing in have no account yet, so the address is all there is to
            // count. Protects the sign-in from being used to guess or to load the database; it decides
            // nothing about who may sign in, which the provider and the admission list do.
            o.AddPolicy(Auth, context => PerMinute(ClientAddress(context), AuthPerMinute));

            // Per account, which is why this runs after authentication: before it there is no account to
            // count, and every person would share one bucket. Protects the database's time; whether the
            // person may do what they asked is the services' decision, on every call it lets through.
            // The group's authorization refuses a request with no session before this is reached, so
            // UserAccess cannot come back empty here; if it ever did, it throws and the request fails
            // closed, rather than being counted in some shared bucket.
            o.AddPolicy(Api, context => PerMinute(context.UserAccess().UserId, ApiPerMinute));

            // GET /api/session, which answers before anybody has signed in and so is outside the group:
            // per person when the cookie names one, per address when not, at the API's rate. Protects
            // the database's time (a signed-in answer reads the account) and nothing else - what the
            // session says is the cookie check's decision. The prefixes keep an account id and an
            // address from ever sharing a count.
            o.AddPolicy(Session, context => PerMinute(
                context.SignedInUserId() is { } userId ? "user:" + userId : "address:" + ClientAddress(context),
                ApiPerMinute));
        });
    }

    /// <summary>
    /// The limits that have to run before authentication: the whole gateway's ceiling on requests in
    /// progress, and the providers' callbacks, which are answered inside authentication. The endpoints'
    /// own policies run later, from <c>UseRateLimiter</c>, once the account is known.
    /// </summary>
    public static IApplicationBuilder UseFrontDoorLimit(this IApplicationBuilder app)
    {
        var limiter = app.ApplicationServices.GetRequiredService<FrontDoorLimit>();

        return app.Use(async (context, next) =>
        {
            var lease = await limiter.AcquireAsync(context);

            if (!lease.IsAcquired)
            {
                using (lease)
                {
                    await RefuseAsync(context, lease);
                }

                return;
            }

            // Held until the request is answered - a concurrency permit is the request being in
            // progress - unless a computer's connection lets it go sooner (UseComputerRelease).
            var permit = new FrontDoorPermit(lease);
            context.Features.Set(permit);

            try
            {
                await next(context);
            }
            finally
            {
                permit.Release();
            }
        });
    }

    /// <summary>
    /// Lets a computer's connection out from under the ceiling once it has proved which computer it is.
    /// After authorization, so only a request the hub has accepted is let go.
    ///
    /// <para>A computer's connection lasts as long as it is online - a WebSocket, or a long poll answered
    /// only when there is something to say - so held, two hundred computers online would leave no place
    /// for anybody's panel. Its calls are limited per computer (<see cref="HostCallLimit"/>) instead, and its
    /// connections have a budget of their own (<see cref="Gateway.HostConnections"/>).</para>
    ///
    /// <para>Not by its path. The hub's path was exempt from the ceiling once, and the path is the
    /// caller's to write: anybody could send requests there without end, each with a made-up token
    /// costing a database lookup, and nothing counted them. Every request is counted until it is
    /// answered or has authenticated as a computer.</para>
    /// </summary>
    public static IApplicationBuilder UseComputerRelease(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            if (context.User.Identity is { IsAuthenticated: true, AuthenticationType: HostAuthentication.SchemeName })
            {
                context.Features.Get<FrontDoorPermit>()?.Release();
            }

            return next(context);
        });

    /// <summary>
    /// The caller's address. Behind the tunnel the forwarded-headers step has already set it from the
    /// tunnel's own header, so this is the visitor and not cloudflared.
    /// </summary>
    internal static string ClientAddress(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> PerMinute(string key, int permits)
        => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

    /// <summary>
    /// 429 in the API's own shape, with when to try again if the limiter knows. A bare status left the
    /// panel parsing an empty body, unable to tell "wait a moment" from "this is broken".
    /// </summary>
    private static async ValueTask RefuseAsync(HttpContext context, RateLimitLease lease)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await GatewayFault.RateLimited().WriteAsync(context);
    }

    /// <summary>
    /// The front door's limiters, one set per gateway: the concurrency ceiling and the callbacks' limit,
    /// chained. Held by the service container so it is disposed with the gateway, and so two gateways in
    /// one process - the tests' - never share a count.
    /// </summary>
    internal sealed class FrontDoorLimit : IDisposable
    {
        private readonly PartitionedRateLimiter<HttpContext> _concurrency = PartitionedRateLimiter.Create<HttpContext, string>(
            _ => RateLimitPartition.GetConcurrencyLimiter("all", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = ConcurrentRequests,

                // Refused at once rather than queued: a queue full of a flood's requests is the same
                // outage, only slower to show.
                QueueLimit = 0
            }));

        private readonly PartitionedRateLimiter<HttpContext> _callbacks = ExternalSignIn.CallbackLimiter();

        private readonly PartitionedRateLimiter<HttpContext> _chained;

        public FrontDoorLimit()
        {
            _chained = PartitionedRateLimiter.CreateChained(_concurrency, _callbacks);
        }

        public ValueTask<RateLimitLease> AcquireAsync(HttpContext context)
            => _chained.AcquireAsync(context, 1, context.RequestAborted);

        public void Dispose()
        {
            _chained.Dispose();
            _concurrency.Dispose();
            _callbacks.Dispose();
        }
    }

    /// <summary>
    /// One request's place under the ceiling, given back once: when the request is answered, or earlier
    /// for a computer's connection. Disposing the lease twice would give the place back twice.
    /// </summary>
    internal sealed class FrontDoorPermit(RateLimitLease lease)
    {
        private RateLimitLease? _lease = lease;

        public void Release() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

/// <summary>
/// The hub's limit: <see cref="RequestLimits.HubCallsPerMinute"/> calls per computer. Not authorization:
/// a call it lets through is still checked against the computer's row and its account in the service.
///
/// <para>Taken inside the hub's own call wrapper, so a refused call is answered with
/// <see cref="Contracts.FaultCode.QuotaExceeded"/>, which the Host keeps the item for and sends again.
/// Refused as an exception it would reach the Host as an unclassified failure, or close the connection,
/// and a Host reconnects - which costs more than the call did.</para>
///
/// <para>A bucket that refills a little every second rather than a window that empties once a minute.
/// A Host retries a refused event every couple of seconds and parks one refused ten times; a refused
/// sync makes it reconnect, with a growing wait. Under a minute's window a computer that spent its calls
/// early was refused for the rest of that minute, so a busy run's events were parked and its sync sent
/// it into reconnecting. Refilled steadily, a computer over its rate is slowed to that rate and its next
/// retry finds room.</para>
/// </summary>
public sealed class HostCallLimit : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        hostId => RateLimitPartition.GetTokenBucketLimiter(hostId, _ => new TokenBucketRateLimiterOptions
        {
            // A whole minute's calls may come at once - a backlog after a reconnect - and the bucket
            // then refills at the minute's rate.
            TokenLimit = RequestLimits.HubCallsPerMinute,
            TokensPerPeriod = RequestLimits.HubCallsPerMinute / 60,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0
        }));

    /// <summary>Counts one call of <paramref name="hostId"/>; throws the coded refusal past the limit.</summary>
    public void Take(string hostId)
    {
        using var lease = _limiter.AttemptAcquire(hostId);

        if (!lease.IsAcquired)
        {
            throw GatewayFault.TooManyCalls();
        }
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// The data export's limit: one per account in <see cref="Window"/>. Not authorization: an export it lets
/// through is still the account the session names, and nothing else.
///
/// <para><b>Why once an hour.</b> An export reads every row the account has, in one transaction held open while
/// the file is downloaded - the most the gateway does for any one request - and at the API's rate a script, or a
/// button pressed again and again, could keep a database connection and a snapshot of the account open for as
/// long as it liked. Nobody needs a second copy of everything within the hour. What is counted is an export
/// STARTING: one that fails part-way - the database, a tab closed while it was being prepared - has used the
/// hour too, because what it cost was spent.</para>
///
/// <para><b>Why not a rate-limiter policy.</b> It was one, a fixed window, and a refusal from it said "try again
/// in an hour" whenever it was asked: the limiter reports the window's length, not what is left of it. A person
/// who exported at 10:00 and pressed again at 10:55 was told 11:55, and waited an hour for what worked at 11:00.
/// This keeps when each account's last export started, so a refusal says exactly when the next may.</para>
///
/// <para>Held in memory, like every other limit here: a restart of the gateway - a deploy - forgets it, and an
/// account may export again at once after one. A table would survive that, at a write per export, for a limit
/// whose purpose is load and not a promise to anybody.</para>
/// </summary>
public sealed class ExportLimit(TimeProvider clock)
{
    /// <summary>How long after one export the next may start.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Counts an export of <paramref name="userId"/>'s starting now and answers null, or answers how long until one
    /// may start, without counting anything.
    /// </summary>
    public TimeSpan? TryStart(string userId)
    {
        var now = clock.GetUtcNow();

        lock (_lock)
        {
            if (_started.TryGetValue(userId, out var last) && now - last < Window)
            {
                return last + Window - now;
            }

            // An account whose hour is over has nothing left to remember. Forgotten here, on the rare call that
            // starts an export, rather than kept for every account that ever exported until the next restart.
            foreach (var (id, at) in _started)
            {
                if (now - at >= Window)
                {
                    _started.Remove(id);
                }
            }

            _started[userId] = now;
            return null;
        }
    }
}

using System.Net;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// Stage 2 of the remote-access design. The panel itself is stage 6; what is here is the surface it
// will call and the hub a Host connects to.

// Answered before ANYTHING else, including the configuration checks below: this asks the assembly
// what it knows and must work on a build that has been downloaded and not yet configured. That is
// exactly when it is asked - the deploy timer runs it on a release sitting in a directory, to find
// out whether installing it would change the schema, before deciding whether it may install it
// unattended at all.
//
// The build answers for itself rather than the pipeline answering for it. Migrations are embedded
// resources, so an unzipped release has no .sql files to count, and a number CI wrote into a
// manifest is a claim ABOUT the assembly that nothing keeps true.
if (args.Contains("--schema-version"))
{
    Console.WriteLine(Migrator.KnownVersions().Max());
    return;
}

// Asked by the deploy timer in the same place, for the same reason. The schema version cannot tell it
// that a release speaks another protocol: protocol 2 started its schema again at 1, below the protocol-1
// database's 2, so by schema alone it reads as a rollback and would be installed onto a database it
// cannot read. The number and nothing else on stdout: the timer compares it with the running release's.
if (args.Contains("--protocol-version"))
{
    Console.WriteLine(RemoteProtocol.Version);
    return;
}

// The operator's command line, on the same binary: `admin approve github:12345`. Handled before the web
// application is built, so it never binds a port or touches the providers, and needs only the database.
// Without this the arguments would be passed on to a web server that waits for ever for requests.
if (args.Length > 0 && args[0] == "admin")
{
    var adminConnection = Environment.GetEnvironmentVariable("ENACTIVE_REMOTE_DB");

    if (string.IsNullOrWhiteSpace(adminConnection))
    {
        Console.Error.WriteLine("Set ENACTIVE_REMOTE_DB to the gateway's MySQL connection string.");
        Environment.ExitCode = 2;
        return;
    }

    Environment.ExitCode = await AdminCommands.RunAsync(args[1..], new Database(adminConnection), Console.Out);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Both required, both from the environment. A gateway that starts without them and finds out on
// the first request has already told somebody it was healthy.
var connectionString = builder.Configuration["ENACTIVE_REMOTE_DB"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Set ENACTIVE_REMOTE_DB to the gateway's MySQL connection string before starting.");
}

// Read now, because it throws when it is switched on outside Development: refusing to start is the
// whole of its safety, and a gateway that started first would already be serving the sign-in.
var developmentSignIn = DevelopmentSignIn.Enabled(builder.Configuration, builder.Environment);

// Read now for the same reason: a provider with half its settings, or no public origin to send people back
// to, stops the start rather than offering a sign-in that fails for everybody.
var externalProviders = ExternalProviders.FromConfiguration(builder.Configuration, builder.Environment);

// The providers' callbacks carry the authorization code in their query string, and two framework logs
// print it. The request log writes every request's full URL at Information - the DEFAULT level, so with no
// logging configuration at all every code went into the journal. The sign-in handlers' debug output writes
// the authorization request and the callback message once logging is turned up. Capped here, in code, so
// neither the defaults nor turning logging up to chase a fault puts codes there. A configuration rule for
// a longer category (Logging__LogLevel__Microsoft.AspNetCore.Authentication.OpenIdConnect=Debug, say) or
// one aimed at a single logging provider still outranks these, so the deployment's configuration must not
// set a level below these for anything under them.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Information);

var dataDirectory = builder.Configuration["ENACTIVE_DATA"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);

// Sessions have to survive a restart, so the keys that protect the cookie live on disk beside the
// application. The directory must be readable only by the account the gateway runs as; this build
// does not encrypt them separately, and disk encryption is a deployment concern.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

// How long the panel's history is kept. Configurable because a month is a guess, and validated
// here because a zero or a typo would otherwise delete everything the first time the job ran.
var retentionDays = builder.Configuration.GetValue<int?>("ENACTIVE_RETENTION_DAYS") ?? Retention.DefaultDays;
if (retentionDays < 1)
{
    throw new InvalidOperationException(
        $"ENACTIVE_RETENTION_DAYS is {retentionDays}; it must be at least 1 day.");
}

builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddSingleton(TimeProvider.System);

// Read now, like the settings above: a mode that is not "list" or "open" must stop the start, not be
// guessed at. Whether "open" may be used at all is checked once the services are built, below.
var admission = Admission.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(services => new AccountService(
    services.GetRequiredService<Database>(), TimeProvider.System, admission));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton(services => new Retention(services.GetRequiredService<Database>(), retentionDays));
// Read now, like the settings above: a limit that is not a whole number of at least one stops the start
// rather than being read as its default. The open admission check below reads whatever is registered here.
builder.Services.AddSingleton(Limits.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<HostService>();
builder.Services.AddSingleton(services => new UserService(
    services.GetRequiredService<Database>(), services.GetRequiredService<Limits>(), TimeProvider.System,
    retentionDays));
builder.Services.AddSingleton(services => new DeviceService(
    services.GetRequiredService<Database>(), services.GetRequiredService<Limits>(), TimeProvider.System));
builder.Services.AddSingleton<Projection>();
builder.Services.AddSingleton<HostConnections>();
builder.Services.AddSingleton<AccountDeletion>();
builder.Services.AddSingleton<Export>();
builder.Services.AddHostedService<RetentionLoop>();

// The panel's half of the wire, on the same terms as the Host's.
//
// ASP.NET's default writes an enum as a NUMBER and refuses to read one written as a name, which
// broke this in both directions at once: the panel was shown "status": 3 and could not answer a
// permission at all, because "Approve" failed to bind and came back as a 400 with no code in it.
// A number is also the brittle form - inserting a member into the middle of RemoteRunStatus would
// renumber every status the panel had been taught, and it would go on rendering, wrongly.
//
// So there is ONE serialisation contract in this system, and RemoteJson holds it. Unknown members
// are refused here too: a field the reader does not recognise is a message from a version it does
// not understand, and quietly dropping it is how half an instruction gets carried out.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in RemoteJson.Options.Converters)
    {
        o.SerializerOptions.Converters.Add(converter);
    }

    o.SerializerOptions.PropertyNamingPolicy = RemoteJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.UnmappedMemberHandling = RemoteJson.Options.UnmappedMemberHandling;
});

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 65536)
    .AddJsonProtocol(o =>
    {
        // The same options both ends use, so an enum is a name on the wire in both directions and
        // a field neither side knows is refused rather than dropped.
        foreach (var converter in RemoteJson.Options.Converters)
        {
            o.PayloadSerializerOptions.Converters.Add(converter);
        }

        o.PayloadSerializerOptions.PropertyNamingPolicy = RemoteJson.Options.PropertyNamingPolicy;
        o.PayloadSerializerOptions.UnmappedMemberHandling = RemoteJson.Options.UnmappedMemberHandling;
    });

// Secure everywhere but Development, where an in-process test server and a developer's http://localhost
// could not send a Secure cookie back.
var cookieSecurity = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

builder.Services.AddAuthentication(UserCookie.SchemeName)
    .AddCookie(UserCookie.SchemeName, o =>
    {
        o.Cookie.Name = UserCookie.CookieName;
        o.Cookie.HttpOnly = true;

        // Lax, not Strict: signing in with GitHub or Google ends in a top-level navigation from their
        // site to ours, and a Strict cookie set on that callback is not sent on the redirect after it.
        // Lax still keeps it off another site's form posts and fetches, and antiforgery covers the rest.
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = cookieSecurity;

        // Host-only and for the whole site: no Domain, so no other host under the same parent domain is
        // sent it, and Path=/ so the panel and its API share it.
        o.Cookie.Domain = null;
        o.Cookie.Path = "/";

        o.ExpireTimeSpan = SessionStore.Lifetime;
        o.SlidingExpiration = false;

        // Every request is checked against the session's row, so a revocation, a sign-out elsewhere or a
        // disabled account stops this cookie on its next request rather than when it expires.
        //
        // The check is made here, at the door, and NOT again inside each state-changing transaction.
        // Design section 6 asks for a locking re-read of the account and session inside those
        // transactions; that is not built. What it would add is narrow: a revocation that commits while
        // a request is already past this check lets that one request finish. The design's own
        // "Revocation" section accepts that operations authorized before a revocation commits may
        // complete; every request after it is refused here.
        o.Events.OnValidatePrincipal = UserCookie.ValidatePrincipalAsync;

        // An API, not a website: an unauthenticated call gets a status, never a redirect to a
        // login page that a fetch() would follow and then fail to parse. And the status comes with a
        // coded body like every other refusal: a bare one left the panel parsing an empty body.
        o.Events.OnRedirectToLogin = c => GatewayFault.Unauthenticated().WriteAsync(c.HttpContext);
        o.Events.OnRedirectToAccessDenied = c => GatewayFault.Forbidden().WriteAsync(c.HttpContext);
    })
    .AddScheme<AuthenticationSchemeOptions, HostAuthentication>(HostAuthentication.SchemeName, _ => { })
    .AddExternalSignIn(externalProviders, cookieSecurity);

builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

// Per address for signing in, per account for the API, per computer for the hub, and a ceiling on
// everything at once. None of them is authorization: see RequestLimits.
builder.Services.AddRequestLimits();

// Reached through a Cloudflare tunnel: cloudflared runs on this machine and connects outward, so
// nothing here listens publicly and every request arrives from 127.0.0.1. See Deployment.
var behindTunnel = Deployment.BehindTunnel(builder.Configuration);

if (behindTunnel)
{
    Deployment.RequireLoopbackListeners(builder.Configuration);

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardedForHeaderName = Deployment.ClientAddressHeader;

        // One hop, and only from this machine. Cleared first because the defaults trust loopback
        // AND every private network - which is a different, larger promise than the one being made
        // here, and one nobody would notice had been made.
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownProxies.Add(IPAddress.Loopback);
        o.KnownProxies.Add(IPAddress.IPv6Loopback);
    });
}

var app = builder.Build();

// Before the first request, and before the migrations: open admission lets strangers create accounts,
// which is only tolerable with ceilings on what an account may use.
Admission.RequireLimits(admission, app.Services.GetRequiredService<Limits>());

// FIRST, before anything reads a scheme or an address: the security headers below, the rate
// limiter's partition, and every log line all describe the caller, and until this has run they
// describe cloudflared instead.
if (behindTunnel)
{
    app.UseForwardedHeaders();
}

// The security headers, on every response the gateway gives: set here, before anything below can
// answer, so a redirect, a refusal or a 404 carries them as the page does. A header added only to the
// page is missing from the one response nobody thought of, and that is the one somebody frames or sniffs.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";

    // No address of this site leaves it in a Referer. An invitation is /pair#..., and the fragment is
    // never sent - but the path is, and even the path tells another site that its visitor was just
    // handed a device invitation, and from where.
    context.Response.Headers["Referrer-Policy"] = "no-referrer";

    // The panel uses none of what these name: no plugin, no worker, no web app manifest. A page that
    // holds keys allows nothing it does not use, so a script slipped into it that tries to load a plugin
    // or start a worker - code running beside the page rather than in it - is refused rather than run.
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; "
        + "connect-src 'self'; object-src 'none'; worker-src 'none'; manifest-src 'none'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    // Nor does it need a camera, a microphone, a location or a payment. Refused for the page and for
    // anything it might embed, so no script it was tricked into running can ask a person for them.
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

    // A window another site opened, or opened from here, cannot reach into the panel through
    // window.opener; and no other site can load these responses into its own page as an image or a
    // script. Same-origin is safe for sign-in: the redirect to the provider and its callback are
    // top-level navigations, which neither header restricts.
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    try
    {
        await next();
    }
    catch (GatewayFault fault)
    {
        // The code travels with the message. Nothing on the person's API has a durable outbox today,
        // but the panel has to distinguish "try again" from "this is settled" for the same reason.
        await fault.WriteAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "csrf",
            error = "Your session changed. Refresh the page and try again."
        });
    }
});

if (!app.Environment.IsDevelopment())
{
    // HSTS either way: the header is for the browser, and the browser is talking to Cloudflare over
    // HTTPS whether or not this process ever sees a certificate.
    app.UseHsts();

    // Redirecting is for a gateway that is itself reachable over plain HTTP. Behind the tunnel it
    // is not: the edge already refuses HTTP, the last hop is loopback and http by design, and
    // UseHttpsRedirection would answer a perfectly good request with a redirect to a port it cannot
    // name. This is why the two settings are read together rather than one being assumed.
    if (!behindTunnel)
    {
        app.UseHttpsRedirection();
    }
}

// The page, with its scripts and stylesheets fingerprinted - BEFORE the static file handler, so
// index.html is never served raw. See PanelAssets: no-cache asks a client to revalidate, and a tab
// restored from a phone's back-forward cache never asks at all, so the release that reached the
// server was invisible on the device the panel exists for.
var panel = PanelAssets.Load(app.Environment.WebRootPath!);

app.Use(async (context, next) =>
{
    // The published list of what the panel is: every script and stylesheet with its SHA-256, so anyone can
    // compare what this server sends with what a release says it sent. Revalidated always, like the page:
    // a cached list compared against a newer release reads as an altered server when nothing was altered.
    // HEAD too: left to the static files it met a 404, which reads as a gateway that publishes no list. The
    // server drops the body of a HEAD, so writing it costs nothing and keeps the length honest.
    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.Request.Path == "/.well-known/enactive-panel.json")
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(panel.Manifest, context.RequestAborted);
        return;
    }

    // /pair too: an invitation link is /pair#..., and the new device that opens it needs the panel, which reads
    // the invitation from the fragment. Without it the link met a 404.
    if (!HttpMethods.IsGet(context.Request.Method)
        || (context.Request.Path != "/" && context.Request.Path != "/index.html" && context.Request.Path != "/pair"))
    {
        await next();
        return;
    }

    // The pointer revalidates, always. Everything it points at is now addressed by its contents,
    // so this one request is what makes the rest correct.
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/html; charset=utf-8";

    await context.Response.WriteAsync(panel.Page, context.RequestAborted);
});

app.UseDefaultFiles();

// The panel is told to revalidate, always.
//
// Nothing here sent a Cache-Control header before, so Cloudflare filled one in - four hours - and
// the panel's files are not fingerprinted, so a browser that had them kept them. A release reached
// the server in ten minutes and the person looking at it saw the old page for the rest of the
// afternoon, with no way to tell a stale page from a broken one. That cost an afternoon once
// already, when a blank page was read as a deployment fault and was a cached one.
//
// no-cache does not mean "do not store": the browser keeps the file and asks whether it changed,
// which is answered by the ETag with a 304 and almost no bytes. For a panel of about 30 KB served
// to one owner, that is the right side of the trade - correctness over a round trip.
//
// The fonts are included deliberately rather than exempted. Exempting them would mean deciding
// which files never change, and that decision is wrong the first time somebody rebrands. When this
// panel is worth fingerprinting, fingerprint it; until then it revalidates.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = served => served.Context.Response.Headers.CacheControl = "no-cache"
});
// Before authentication, for two reasons. The providers' callbacks are answered inside authentication,
// and their limit has to refuse a replay before the handler redeems its code. And authentication reads
// the database for every cookie, so the ceiling on requests in progress has to come before it or a
// flood reaches the database anyway. The panel's page and files above are served from memory and disk
// and are not counted.
app.UseFrontDoorLimit();

// Before authentication, where the providers' handlers build their callback address. See ExternalSignIn.
app.UsePublicOrigin(externalProviders);
app.UseAuthentication();
app.UseAuthorization();

// A computer's connection is let out from under the ceiling once the hub has accepted it, and not
// before: it lasts as long as the computer is online. See RequestLimits.UseComputerRelease.
app.UseComputerRelease();

// The endpoints' policies, AFTER authentication: the API is counted per account, and before
// authentication there is no account - every person would share one bucket, and one busy panel would
// limit everybody's. A request with no session is refused by authorization before it is counted.
app.UseRateLimiter();

app.MapGet("/health", () => new { status = "ok", protocolVersion = RemoteProtocol.Version });

// Who is signed in, if anyone, and the antiforgery token for what they send next. Anonymous, since the
// panel asks it before there is anyone to be: it is how the page learns whether to show a sign-in.
app.MapGet("/api/session", async (
    HttpContext context, IAntiforgery antiforgery, AccountService accounts, CancellationToken ct) =>
{
    var signedIn = context.User.Identity?.IsAuthenticated == true;
    var user = signedIn ? context.UserAccess() : null;
    var displayName = user is null ? null : await accounts.DisplayNameAsync(user, ct);

    return new
    {
        authenticated = displayName is not null,
        csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken,
        user = displayName is null ? null : new { id = user!.UserId, displayName }
    };
}).RequireRateLimiting(RequestLimits.Session);

if (developmentSignIn)
{
    app.MapDevelopmentSignIn(RequestLimits.Auth);
}

app.MapExternalSignIn(externalProviders, RequestLimits.Auth);

// The person's API: their cookie, and nothing else. A computer's bearer token is a credential for the
// hub only - one that could call this could register computers and start work on its own say-so.
var api = app.MapGroup("/api")
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = UserCookie.SchemeName })
    .RequireRateLimiting(RequestLimits.Api);

// Every state-changing call, sign-out included. A token that is only checked on the interesting
// endpoints is a token somebody will forget to check on the next one.
api.AddEndpointFilter(async (invocation, next) =>
{
    if (invocation.HttpContext.Request.Method != HttpMethods.Get)
    {
        await invocation.HttpContext.RequestServices
            .GetRequiredService<IAntiforgery>()
            .ValidateRequestAsync(invocation.HttpContext);
    }

    return await next(invocation);
});

// The session's ROW is revoked, not only the cookie deleted: a cookie copied before signing out would
// otherwise go on working until it expired.
api.MapPost("/logout", async (HttpContext context, SessionStore sessions, CancellationToken ct) =>
{
    await sessions.RevokeAsync(context.UserAccess(), ct);
    await context.SignOutAsync(UserCookie.SchemeName);
    return Results.Ok();
});

// Every session of the account, this one included - for a lost phone or a borrowed laptop left signed
// in. The security version moves on too, so a session a sign-in was opening at the same moment is ended
// with the rest.
api.MapPost("/logout-all", async (HttpContext context, SessionStore sessions, CancellationToken ct) =>
{
    await sessions.RevokeAllAsync(context.UserAccess().UserId, ct);
    await context.SignOutAsync(UserCookie.SchemeName);
    return Results.Ok();
});

// The person deleting their own account and everything stored for it, from a session opened in the last ten
// minutes (AccountDeletion). The cookie is removed too: the session it names went with the account.
api.MapDelete("/account", async (HttpContext context, AccountDeletion deletion, CancellationToken ct) =>
{
    await deletion.DeleteAsync(context.UserAccess(), ct);
    await context.SignOutAsync(UserCookie.SchemeName);
    return Results.Ok();
});

// `since` is the cursor from the previous reply, as text. A cursor that is not one of the person's
// current line gets their whole snapshot rather than an error, because the panel can do nothing
// with an error except ask again without one.
api.MapGet("/state", (string? since, HttpContext context, Projection projection, CancellationToken ct) =>
    projection.ReadAsync(context.UserAccess(), since, ct));

// The person's own security log, the newest first: sign-ins, devices and computers added and removed, and
// what the operator did to the account. Read by the account the session names, so another person's rows
// are not in it to leak.
api.MapGet("/audit", async (HttpContext context, Database db, CancellationToken ct) =>
    Results.Ok(await Audit.ReadAsync(db, context.UserAccess(), ct)));

// Everything stored for the account, streamed as one JSON file (Export): the account's own rows, metadata and
// envelopes, read by the account the session names. Once an hour per account (RequestLimits.ExportWindow).
api.MapGet("/export", (HttpContext context, Export export, CancellationToken ct) =>
    export.WriteAsync(context.UserAccess(), context.Response, ct))
    .RequireRateLimiting(RequestLimits.Export);

api.MapPost("/hosts", async (
    RegisterHostRequest request, HttpContext context, UserService users, CancellationToken ct) =>
{
    var (id, name, token) = await users.RegisterHostAsync(context.UserAccess(), request.Name, ct);

    // The only time this value exists anywhere but the device it is going to.
    return Results.Ok(new { id, name, token });
});

api.MapPost("/hosts/{id}/revoke", async (
    string id, HttpContext context, UserService users, HostConnections connections, CancellationToken ct) =>
{
    await users.RevokeHostAsync(context.UserAccess(), id, ct);
    connections.CloseAll(id);
    return Results.Ok();
});

// Revoke or endorse a browser on one computer. Which browser, and its key, are inside the seal; the
// kind is plaintext only so the computer knows which record to open it as.
api.MapPost("/hosts/{hostId}/device-commands", async (
    string hostId, DeviceCommandRequest request, HttpContext context, UserService users,
    CancellationToken ct) =>
    Results.Ok(await users.SendDeviceCommandAsync(
        context.UserAccess(), hostId, request.Kind, request.CommandId, request.Sealed, ct)));

// A browser profile of this account, and the public key grants will be sealed to. The key is checked for
// shape and curve; what it is for is the browser's and the computer's business, not this gateway's.
api.MapPost("/devices", async (
    RegisterDeviceRequest request, HttpContext context, DeviceService devices, CancellationToken ct) =>
    Results.Ok(new
    {
        id = await devices.RegisterAsync(
            context.UserAccess(), DeviceService.DecodeKey(request.PublicKey), request.Label, ct)
    }));

api.MapGet("/devices", async (HttpContext context, DeviceService devices, CancellationToken ct) =>
    Results.Ok(await devices.ListAsync(context.UserAccess(), ct)));

api.MapPost("/devices/{id}/revoke", async (
    string id, HttpContext context, DeviceService devices, CancellationToken ct) =>
{
    await devices.RevokeAsync(context.UserAccess(), id, ct);
    return Results.Ok();
});

// The grants made to the browser the call names, for every computer of the person's. The gateway hands
// them over as they were stored; it has no key to open one.
api.MapGet("/grants", async (HttpContext context, DeviceService devices, CancellationToken ct) =>
{
    var device = await devices.RequireAsync(context.UserAccess(), context.RequireDeviceId(), ct);
    return Results.Ok(await devices.ReadGrantsAsync(device, ct));
});

// A trusted browser passing keys it holds to another of the person's devices, after an invitation. The
// browser making the call must be one of the person's and not removed, like the devices it grants to.
api.MapPost("/grants", async (
    List<KeyGrant>? grants, HttpContext context, DeviceService devices, CancellationToken ct) =>
{
    var device = await devices.RequireAsync(context.UserAccess(), context.RequireDeviceId(), ct);
    await devices.PublishGrantsAsync(device, grants, ct);
    return Results.Ok();
});

// A trusted browser invites another device. The id is the browser's own, and the link it shows carries the
// pairing secret in its fragment, which never reaches this server.
api.MapPost("/invites", async (
    CreateInviteRequest request, HttpContext context, DeviceService devices, CancellationToken ct) =>
{
    var device = await devices.RequireAsync(context.UserAccess(), context.RequireDeviceId(), ct);
    var expiresAt = await devices.CreateInviteAsync(device, request.Id, ct);
    return Results.Ok(new { id = request.Id, expiresAt });
});

// The new device's answer. Which device answers is in the body, not the header: it may be a browser that
// has only just registered its key, and the device named must be the person's own and not removed.
api.MapPost("/enrollments", async (
    EnrollmentRequest request, HttpContext context, DeviceService devices, CancellationToken ct) =>
{
    await devices.EnrollAsync(context.UserAccess(), request.InviteId, request.DeviceId, request.Mac, ct);
    return Results.Ok();
});

// The inviting browser asks until there is an answer: 204 while there is none. The public key and label are
// the answering device's own, from its row; the MAC is what lets the browser check the key was not swapped.
api.MapGet("/invites/{id}/enrollment", async (
    string id, HttpContext context, DeviceService devices, CancellationToken ct) =>
{
    var device = await devices.RequireAsync(context.UserAccess(), context.RequireDeviceId(), ct);

    return await devices.ReadEnrollmentAsync(device, id, ct) is { } enrollment
        ? Results.Ok(new
        {
            deviceId = enrollment.DeviceId,
            publicKey = enrollment.DevicePublic,
            label = enrollment.Label,
            mac = enrollment.Mac
        })
        : Results.NoContent();
});

api.MapPost("/tasks", async (
    CreateTaskRequest request, HttpContext context, UserService users, CancellationToken ct) =>
{
    await users.CreateTaskAsync(
        context.UserAccess(), request.TaskId, request.HostId, request.WorkspaceId, request.SealedTask, ct);
    return Results.Ok(new { id = request.TaskId });
});

api.MapPost("/tasks/{id}/start", async (
    string id, CommandRequest request, HttpContext context, UserService users, CancellationToken ct) =>
    Results.Ok(await users.StartAsync(context.UserAccess(), id, request.CommandId, request.Sealed, ct)));

api.MapPost("/runs/{id}/cancel", async (
    string id, CommandRequest request, HttpContext context, UserService users, CancellationToken ct) =>
    Results.Ok(await users.CancelAsync(context.UserAccess(), id, request.CommandId, request.Sealed, ct)));

api.MapPost("/approvals/{id}/resolve", async (
    string id, DecisionRequest request, HttpContext context, UserService users, CancellationToken ct) =>
    Results.Ok(await users.DecideAsync(
        context.UserAccess(), id, request.HostId, request.CommandId, request.Decision, request.ActionHash,
        request.Sealed, ct)));

// `through` is the cursor of the snapshot on the person's screen, exactly as they were given it. A
// malformed one is refused here, before anything is marked; whether it is one of their current line
// is the service's to decide, against the line.
api.MapPost("/notices/read", async (
    NoticesReadRequest request, HttpContext context, UserService users, CancellationToken ct) =>
{
    if (!Projection.TryParseCursor(request.Through, out var epoch, out var ordinal))
    {
        throw GatewayFault.BadRequest("'through' must be the cursor of the snapshot being marked read.");
    }

    await users.MarkNoticesReadAsync(context.UserAccess(), epoch, ordinal, ct);
    return Results.Ok();
});

app.MapHub<HostHub>("/hubs/host");

await Migrator.ApplyAsync(new Database(connectionString).ConnectionString);
app.Run();

internal sealed record RegisterHostRequest(string? Name);
internal sealed record RegisterDeviceRequest(string? PublicKey, string? Label);
internal sealed record CreateInviteRequest(string? Id);
internal sealed record EnrollmentRequest(string? InviteId, string? DeviceId, string? Mac);
internal sealed record CreateTaskRequest(string TaskId, string HostId, string WorkspaceId, string SealedTask);
internal sealed record CommandRequest(string CommandId, string Sealed);
internal sealed record DecisionRequest(
    string CommandId, string HostId, RemoteDecision Decision, string ActionHash, string Sealed);
internal sealed record NoticesReadRequest(string? Through);
internal sealed record DeviceCommandRequest(string CommandId, CommandKind Kind, string Sealed);

/// <summary>Named so a test host can reference this assembly's entry point.</summary>
public partial class Program;

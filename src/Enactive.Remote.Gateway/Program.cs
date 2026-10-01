using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// Stage 2 of the remote-access design. The panel itself is stage 6; what is here is the surface it
// will call and the hub a Host connects to.

const string OwnerScheme = "Owner";

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

var builder = WebApplication.CreateBuilder(args);

// Both required, both from the environment. A gateway that starts without them and finds out on
// the first request has already told somebody it was healthy.
var connectionString = builder.Configuration["ENACTIVE_REMOTE_DB"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Set ENACTIVE_REMOTE_DB to the gateway's MySQL connection string before starting.");
}

var ownerKey = builder.Configuration["ENACTIVE_OWNER_KEY"];
if (string.IsNullOrWhiteSpace(ownerKey) || ownerKey.Length < 24)
{
    throw new InvalidOperationException(
        "Set ENACTIVE_OWNER_KEY to a private random value of at least 24 characters before starting.");
}

var dataDirectory = builder.Configuration["ENACTIVE_DATA"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);

// Sessions have to survive a restart, so the keys that protect the cookie live on disk beside the
// application. The directory must be readable only by the account the gateway runs as; this build
// does not encrypt them separately, and disk encryption is a deployment concern.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

// How long the panel's history is kept. Configurable because a month is a guess, and validated
// here because a zero or a typo would otherwise delete everything the first time the job ran.
var retentionDays = builder.Configuration.GetValue<int?>("ENACTIVE_RETENTION_DAYS") ?? 30;
if (retentionDays < 1)
{
    throw new InvalidOperationException(
        $"ENACTIVE_RETENTION_DAYS is {retentionDays}; it must be at least 1 day.");
}

builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddSingleton(new OwnerKey(ownerKey));
builder.Services.AddSingleton(services => new Retention(services.GetRequiredService<Database>(), retentionDays));
builder.Services.AddSingleton<HostService>();
builder.Services.AddSingleton<OwnerService>();
builder.Services.AddSingleton<Projection>();
builder.Services.AddSingleton<HostConnections>();
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

builder.Services.AddAuthentication(OwnerScheme)
    .AddCookie(OwnerScheme, o =>
    {
        o.Cookie.Name = "Enactive.Owner";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;

        // Changing the configured key signs everybody out, everywhere. It is the only such control
        // this build has - per-device sessions are in the design document and are not built.
        o.Events.OnValidatePrincipal = async context =>
        {
            var key = context.HttpContext.RequestServices.GetRequiredService<OwnerKey>();

            if (context.Principal?.FindFirstValue("keyVersion") != key.Version)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(OwnerScheme);
            }
        };

        // An API, not a website: an unauthenticated call gets a status, never a redirect to a
        // login page that a fetch() would follow and then fail to parse.
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, HostAuthentication>(HostAuthentication.SchemeName, _ => { });

builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

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
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownProxies.Add(IPAddress.Loopback);
        o.KnownProxies.Add(IPAddress.IPv6Loopback);
    });
}

var app = builder.Build();

// FIRST, before anything reads a scheme or an address: the security headers below, the rate
// limiter's partition, and every log line all describe the caller, and until this has run they
// describe cloudflared instead.
if (behindTunnel)
{
    app.UseForwardedHeaders();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

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
        // The code travels with the message. Nothing on the owner path has a durable outbox today,
        // but the panel has to distinguish "try again" from "this is settled" for the same reason.
        context.Response.StatusCode = fault.Status;
        await context.Response.WriteAsJsonAsync(new { code = fault.Code, error = fault.Message });
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
    if (!HttpMethods.IsGet(context.Request.Method)
        || (context.Request.Path != "/" && context.Request.Path != "/index.html"))
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
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => new { status = "ok", protocolVersion = RemoteProtocol.Version });

app.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery) => new
{
    authenticated = context.User.Identity?.IsAuthenticated == true,
    csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken
});

app.MapPost("/api/login", async (
    LoginRequest request, HttpContext context, IAntiforgery antiforgery, OwnerKey key) =>
{
    await antiforgery.ValidateRequestAsync(context);

    if (key.LockedOut)
    {
        return Results.Json(new { code = "locked-out", error = "Too many failed attempts. Try again shortly." },
            statusCode: 429);
    }

    if (!key.Matches(request.Key))
    {
        return Results.Unauthorized();
    }

    await context.SignInAsync(OwnerScheme, new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim("keyVersion", key.Version)],
        OwnerScheme)));

    return Results.Ok();
}).RequireRateLimiting("login");

var api = app.MapGroup("/api")
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = OwnerScheme });

// Every state-changing call, login and logout included. A token that is only checked on the
// interesting endpoints is a token somebody will forget to check on the next one.
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

api.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(OwnerScheme);
    return Results.Ok();
});

// `since` is the cursor from the previous reply and nothing else. It is not validated against
// anything: a cursor from the future returns an empty delta, a cursor from before the history was
// trimmed returns what survived, and neither is an error the panel could do anything about.
api.MapGet("/state", (long? since, Projection projection, CancellationToken ct) =>
    projection.ReadAsync(since, ct));

api.MapPost("/hosts", async (RegisterHostRequest request, OwnerService owner, CancellationToken ct) =>
{
    var (id, name, token) = await owner.RegisterAsync(request.Name, ct);

    // The only time this value exists anywhere but the device it is going to.
    return Results.Ok(new { id, name, token });
});

api.MapPost("/hosts/{id}/revoke", async (
    string id, OwnerService owner, HostConnections connections, CancellationToken ct) =>
{
    await owner.RevokeAsync(id, ct);
    connections.CloseAll(id);
    return Results.Ok();
});

api.MapPost("/tasks", async (CreateTaskRequest request, OwnerService owner, CancellationToken ct) =>
    Results.Ok(new { id = await owner.CreateTaskAsync(
        request.HostId, request.WorkspaceId, request.Title, request.Prompt, ct) }));

api.MapPost("/tasks/{id}/start", async (
    string id, CommandRequest request, OwnerService owner, CancellationToken ct) =>
    Results.Ok(await owner.StartAsync(id, request.CommandId, ct)));

api.MapPost("/runs/{id}/cancel", async (
    string id, CommandRequest request, OwnerService owner, CancellationToken ct) =>
    Results.Ok(await owner.CancelAsync(id, request.CommandId, ct)));

api.MapPost("/approvals/{id}/resolve", async (
    string id, DecisionRequest request, OwnerService owner, CancellationToken ct) =>
    Results.Ok(await owner.DecideAsync(id, request.CommandId, request.Decision, request.ActionHash, ct)));

api.MapPost("/notices/read", async (OwnerService owner, CancellationToken ct) =>
{
    await owner.MarkNoticesReadAsync(ct);
    return Results.Ok();
});

app.MapHub<HostHub>("/hubs/host");

await Migrator.ApplyAsync(new Database(connectionString).ConnectionString);
app.Run();

internal sealed record LoginRequest(string? Key);
internal sealed record RegisterHostRequest(string? Name);
internal sealed record CreateTaskRequest(string HostId, string WorkspaceId, string? Title, string? Prompt);
internal sealed record CommandRequest(string CommandId);
internal sealed record DecisionRequest(string CommandId, RemoteDecision Decision, string ActionHash);

/// <summary>Named so a test host can reference this assembly's entry point.</summary>
public partial class Program;

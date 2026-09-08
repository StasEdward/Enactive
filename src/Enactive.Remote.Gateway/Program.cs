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

// Stage 2 of Docs/REMOTE_DESIGN.md. The panel itself is stage 6; what is here is the surface it
// will call and the hub a Host connects to.

const string OwnerScheme = "Owner";

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

builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddSingleton(new OwnerKey(ownerKey));
builder.Services.AddSingleton<HostService>();
builder.Services.AddSingleton<OwnerService>();
builder.Services.AddSingleton<Projection>();
builder.Services.AddSingleton<HostConnections>();

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

var app = builder.Build();

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
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseDefaultFiles();
app.UseStaticFiles();
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

api.MapGet("/state", (Projection projection, CancellationToken ct) => projection.ReadAsync(ct));

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

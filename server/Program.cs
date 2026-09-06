using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Enactive.Server;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
var ownerKey = builder.Configuration["ENACTIVE_OWNER_KEY"];
if (string.IsNullOrWhiteSpace(ownerKey) || ownerKey.Length < 24)
    throw new InvalidOperationException("Set ENACTIVE_OWNER_KEY to a private random value of at least 24 characters before starting the server.");
var dataDirectory = builder.Configuration["ENACTIVE_DATA"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
builder.Services.AddSingleton<GatewayStore>();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddSingleton<HostConnections>();
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 65536);
builder.Services.AddAuthentication("Owner")
    .AddCookie("Owner", o =>
    {
        o.Cookie.Name = "Enactive.Owner";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        // Changing the configured owner credential invalidates existing sessions.
        o.Events.OnValidatePrincipal = async context =>
        {
            if (context.Principal?.FindFirstValue("keyVersion") != GatewayService.Hash(ownerKey))
            { context.RejectPrincipal(); await context.HttpContext.SignOutAsync("Owner"); }
        };
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, HostAuthentication>("Host", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (ApiError error) { context.Response.StatusCode = error.Status; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
    catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Session changed. Refresh the page and try again." }); }
});
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery) => new
{
    authenticated = context.User.Identity?.IsAuthenticated == true,
    csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken
});
app.MapPost("/api/login", async (LoginInput input, HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(input.Key ?? ""));
    if (!CryptographicOperations.FixedTimeEquals(supplied, SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)))) return Results.Unauthorized();
    var principal = new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim("keyVersion", GatewayService.Hash(ownerKey))], "Owner"));
    await context.SignInAsync("Owner", principal);
    return Results.Ok();
}).RequireRateLimiting("login");

var api = app.MapGroup("/api").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { AuthenticationSchemes = "Owner" });
api.AddEndpointFilter(async (invocation, next) =>
{
    if (invocation.HttpContext.Request.Method != "GET")
        await invocation.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(invocation.HttpContext);
    return await next(invocation);
});
api.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync("Owner"); return Results.Ok(); });
api.MapGet("/state", (GatewayService service) => service.Snapshot());
api.MapPost("/hosts", (RegisterHostInput input, GatewayService service) => service.Register(input.Name));
api.MapPost("/hosts/{id}/revoke", (string id, GatewayService service, HostConnections connections) =>
{ service.Revoke(id); connections.Revoke(id); return Results.Ok(); });
api.MapPost("/tasks", (CreateTaskInput input, GatewayService service) => service.Create(input));
api.MapPost("/tasks/{id}/start", (string id, CommandInput input, GatewayService service) => service.Start(id, input.CommandId));
api.MapPost("/runs/{id}/cancel", (string id, CommandInput input, GatewayService service) => service.Cancel(id, input.CommandId));
api.MapPost("/approvals/{id}/resolve", (string id, DecisionInput input, GatewayService service) => service.Decide(id, input));
api.MapPost("/notices/read", (GatewayService service) => { service.MarkRead(); return Results.Ok(); });
app.MapHub<HostHub>("/hubs/host");
app.MapGet("/health", () => new { status = "ok", protocolVersion = 1 });
app.Run();

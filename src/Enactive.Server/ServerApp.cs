namespace Enactive.Server;

using System.Security.Cryptography.X509Certificates;
using Enactive.Server.Accounts;
using Enactive.Server.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Enactive.Workspace;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
/// The web application the service runs: built here, apart from Program, so a test can build the same pipeline on a
/// test server instead of Kestrel - the checks that matter (what answers, what refuses) are in the pipeline, and a
/// copy of it in a test would pass while the real one was wrong.
/// </summary>
internal static class ServerApp
{
    /// <summary>The Windows service's name, and the Event Log source it writes as.</summary>
    public const string ServiceName = "Enactive";

    /// <param name="certificate">What Kestrel presents on every endpoint; null only for a test server, which has no sockets.</param>
    /// <param name="adjust">For a test: swap in a test server, a data folder of its own.</param>
    public static WebApplication Build(string[] args, ServerOptions options, string dataFolder, X509Certificate2? certificate,
        Action<WebApplicationBuilder>? adjust = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Named, not taken from the entry assembly: under a test that is the test host, and the static assets
            // (and the pages) are looked up by this application's name.
            ApplicationName = typeof(ServerApp).Assembly.GetName().Name,
            // Beside the executable, wherever it was started from: a service starts in System32, and a console in
            // whatever folder the person was in - the pages and static files were not found from either.
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        // Into the Application log as "Enactive", the source the installer registers; left to the default it is the
        // assembly's name, which nothing registered, and the service's own entries were lost.
        builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(s => s.SourceName = ServiceName);
        // Before the services, as it always was: the authentication state provider of ServerAuth must be the one
        // registered last, and the components' own registrations are not to land after it.
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        AddServices(builder.Services, options, dataFolder);

        // The log file's provider, from the one log AddServices registered: the service's own lines go where the
        // engine's do.
        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp => new FileLoggerProvider(sp.GetRequiredService<LogHub>()));
        // The framework says every request at Information - a line per page, script and hub message - which would push
        // the runs out of the log the page keeps. Its warnings and errors still come through; the host's start and stop do.
        builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);

        // The pages' scripts and styles from a build as from a publish. Without it a build run outside Development - the
        // browser tests' harness, a trial on the server before installing - served blazor.web.js and the stylesheets
        // with status 200 and no bytes, and every page stayed dead (found by tests/server-e2e). A published service has
        // no build manifest, and this does nothing there.
        builder.WebHost.UseStaticWebAssets();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // HTTPS on every endpoint, and no other listener: there is no "http first, certificate later".
            foreach (var endpoint in options.Endpoints())
                kestrel.Listen(endpoint, listen => listen.UseHttps(certificate
                    ?? throw new InvalidOperationException("The service does not listen without a certificate.")));
        });

        adjust?.Invoke(builder);

        var app = builder.Build();

        // First, before anything answers: the subnets, the origin, the Host header (NetworkGate).
        var gate = app.Services.GetRequiredService<NetworkGate>();
        app.Use((context, next) => gate.InvokeAsync(context, _ => next()));

        // Browsers are told to come back over HTTPS only, for a year; the service never answers plain HTTP anyway.
        app.UseHsts();
        app.UseAuthentication();
        // The browser hub only for somebody signed in. Its pages check their own authorization, but the hub negotiated
        // a connection with anybody who asked (seen against Kestrel), and a circuit is not something to hand out before
        // sign-in. The sign-in page is rendered statically and opens none.
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/_blazor") && context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            return next(context);
        });
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();

        // For the installer and a monitor: up, and which build. Says nothing else - it answers before anyone signs in.
        app.MapGet("/health", () => Results.Json(new { status = "ok", version = BuildVersion() }));

        // A form post from the page's own header, with its antiforgery token; SameSite=Strict already keeps other
        // sites from sending the cookie, this keeps a page of this site that was not the header from doing it either.
        app.MapPost("/signout", async (HttpContext context, UserStore users, IAntiforgery antiforgery) =>
            {
                // Checked here: for an endpoint that binds no form, the antiforgery middleware only RECORDS a failed
                // check, and the post went through without its token (found by SignInTests).
                if (!await antiforgery.IsRequestValidAsync(context))
                    return Results.BadRequest();
                if (context.User.Identity?.Name is { } name)
                    users.Audit(name, "signed out", null, context.Connection.RemoteIpAddress?.ToString());
                await context.SignOutAsync();
                return Results.LocalRedirect("/signin");
            });

        // A kept proposal applied or rejected (ServerStaging checks the role and the run's end), back to its page.
        app.MapPost("/proposals/act", async (HttpContext context, IAntiforgery antiforgery, Runs.ServerStaging staging) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync();
            var (workspace, set) = (form["ws"].ToString(), form["set"].ToString());
            var said = await staging.ActAsync(context.User, workspace, set,
                Guid.TryParse(form["change"], out var change) ? change : Guid.Empty, form["act"] == "apply", context.RequestAborted);
            return Results.LocalRedirect("/" + Components.Links.Proposals(workspace, set) + "&said=" + Uri.EscapeDataString(said));
        });

        // The accounts page's changes: an Admin's, checked by AccountAdmin; the token here. Not at the page's own
        // address: a page also takes form posts, and two endpoints on one address was a 500 (found by tests/server-e2e).
        app.MapPost("/admin/users/act", async (HttpContext context, IAntiforgery antiforgery, AccountAdmin accounts) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync();
            var said = accounts.Do(context.User, form["act"].ToString(), form["name"].ToString().Trim(),
                form["role"].ToString(), form["password"].ToString(), form["again"].ToString());
            return Results.LocalRedirect("/admin/users?said=" + Uri.EscapeDataString(said));
        });

        // The old addresses of a workspace's history and of the workspace list, which the Runs screen is now: a link in an
        // earlier mail or a bookmark still lands. The history named its workspace by its path; the Runs screen names it by
        // its id (WorkspaceIds), found only among the workspaces the service knows - a path it does not know lands on the
        // Runs screen as it opens, not on a folder read off the disk. What the old page was told to say comes along. A known
        // folder whose path cannot be read (a drive not mounted) has no id to give, and lands there too (TryIdOf): read
        // unguarded it was a 500 for an old link.
        app.MapGet("/history", (HttpContext context, Runs.ServerHistory history, Runs.WorkspaceIds ids) =>
            Results.LocalRedirect(Components.Links.Runs(
                history.Workspace(context.User, context.Request.Query["ws"].ToString()) is { } known ? ids.TryIdOf(known.RootPath) : null,
                context.Request.Query["said"].ToString())))
            .RequireAuthorization(nameof(Role.Viewer));
        app.MapGet("/workspaces", () => Results.LocalRedirect("/")).RequireAuthorization(nameof(Role.Viewer));

        // A past run's old page, and the page a file it wrote was read on, are the Runs screen now - the run at its own
        // address there (/history/{workspace}/{run}), its files reviewed on its Artifacts tab - and an old link lands, by
        // the same rule as the history's above: the workspace by its id, a path the service does not know on "/".
        app.MapGet("/history/run", (HttpContext context, Runs.ServerHistory history, Runs.WorkspaceIds ids) =>
            {
                var workspace = history.Workspace(context.User, context.Request.Query["ws"].ToString()) is { } known
                    ? ids.TryIdOf(known.RootPath)
                    : null;
                return Results.LocalRedirect(workspace is { } id && Guid.TryParse(context.Request.Query["id"], out var run)
                    ? "/" + Components.Links.PastRun(id, run)
                    : Components.Links.Runs(workspace));
            })
            .RequireAuthorization(nameof(Role.Viewer));
        app.MapGet("/file", (HttpContext context, Runs.ServerHistory history, Runs.WorkspaceIds ids) =>
            Results.LocalRedirect(Components.Links.Runs(
                history.Workspace(context.User, context.Request.Query["ws"].ToString()) is { } known ? ids.TryIdOf(known.RootPath) : null)))
            .RequireAuthorization(nameof(Role.Viewer));

        // A workspace's templates and schedules (ServerLibrary checks the role and the rules), each back to its page.
        app.MapPost("/library/template/act", async (HttpContext context, IAntiforgery antiforgery, Runs.ServerLibrary library) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync();
            var workspace = form["ws"].ToString();
            var scope = Enum.TryParse<Enactive.Workspace.TemplateScope>(form["scope"], out var s) ? s : Enactive.Workspace.TemplateScope.Workspace;
            var said = form["act"] == "delete"
                ? library.DeleteTemplate(context.User, workspace, form["id"].ToString(), scope)
                : library.SaveTemplate(context.User, workspace, form["json"].ToString(), scope).Said;
            return Results.LocalRedirect("/" + Components.Links.Library(workspace) + "&said=" + Uri.EscapeDataString(said));
        });
        app.MapPost("/library/schedule/act", async (HttpContext context, IAntiforgery antiforgery, Runs.ServerLibrary library) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync();
            var workspace = form["ws"].ToString();
            Guid? id = Guid.TryParse(form["id"], out var parsed) ? parsed : null;
            var said = form["act"].ToString() switch
            {
                "on" => library.SwitchSchedule(context.User, workspace, id ?? Guid.Empty, on: true),
                "off" => library.SwitchSchedule(context.User, workspace, id ?? Guid.Empty, on: false),
                "delete" => library.DeleteSchedule(context.User, workspace, id ?? Guid.Empty),
                _ => library.SaveSchedule(context.User, workspace, new Runs.ScheduleForm(id, form["name"].ToString(),
                    form["work"].ToString(), form["repeat"].ToString(), form["time"].ToString(), form["day"].ToString(),
                    form["date"].ToString(), form["tier"].ToString(), form["runLate"] == "true", form["parameters"].ToString())).Said
            };
            return Results.LocalRedirect("/" + Components.Links.Library(workspace) + "&said=" + Uri.EscapeDataString(said));
        });

        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    /// <summary>
    /// Every service the application keeps, registered on any container: the pipeline's Build adds them to the web
    /// host, and the component tests to bUnit's, so a page under test gets the services it has on the server - a copy of
    /// the list in a test would pass while the real one lacked a service. What needs the web host itself (Kestrel, the
    /// log providers, the static assets, the Razor components' endpoints) stays in Build.
    /// </summary>
    internal static void AddServices(IServiceCollection services, ServerOptions options, string dataFolder)
    {
        services.AddSingleton(options);
        services.AddSingleton(new ServerFolders(dataFolder));

        // One log for the process: the service's own lines and the engine's, kept recent in memory for the log page and
        // written to the daily file. They were two - the page would have shown the engine and not the request that
        // started its run.
        var logFile = new FileLogSink(Path.Combine(dataFolder, "logs", "server"));
        var log = new LogHub(minLevel: Enactive.Core.Diagnostics.LogLevel.Debug, downstream: [logFile]);
        services.AddSingleton(logFile);
        services.AddSingleton(log);
        // The keys that protect sign-in cookies and the antiforgery tokens: in the service's data folder, encrypted
        // for its account. Left to the default they went into the profile's ASP.NET folder, apart from everything else
        // the service keeps and from its backups.
        services.AddDataProtection()
            .SetApplicationName(ServiceName)
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataFolder, "keys")))
            .ProtectKeysWithDpapi();

        // The accounts and the audit log (server.db), signing in, the roles (ServerAuth).
        ServerAuth.Add(services, new UserStore(dataFolder));

        // Runs: the engine (built when first asked for, from the service account's settings), the line of runs, and
        // the one interface every action on a run goes through (IRunControl) - which checks who is asking.
        services.AddSingleton(sp => new Runs.ServerEngine(dataFolder, sp.GetRequiredService<LogHub>()));
        services.AddSingleton(new Enactive.Application.RunQueue(options.MaxActiveRuns));
        services.AddSingleton<Runs.ServerStaging>();
        services.AddSingleton<Runs.IRunExecutor>(sp => new Runs.EngineExecutor(sp.GetRequiredService<Runs.ServerEngine>(),
            sp.GetRequiredService<Runs.ServerStaging>()));
        services.AddSingleton<Runs.IRunControl>(sp => new Runs.RunControl(sp.GetRequiredService<UserStore>(),
            sp.GetRequiredService<Enactive.Application.RunQueue>(), sp.GetRequiredService<Runs.IRunExecutor>(),
            sp.GetRequiredService<ILogger<Runs.RunControl>>(), TimeSpan.FromMinutes(options.QuestionWaitMinutes),
            sp.GetRequiredService<Runs.WorkspaceRoots>()));
        services.AddSingleton(new Runs.WorkspaceRoots(options.WorkspaceRoots));
        services.AddSingleton<Runs.ServerHistory>();
        services.AddSingleton<Runs.WorkspaceIds>();
        services.AddSingleton<Runs.ServerInbox>();
        services.AddSingleton<AccountAdmin>();
        services.AddSingleton<Runs.ServerSettings>();
        services.AddSingleton<Runs.ServerLibrary>();
        services.AddSingleton<Runs.ServerLogs>();
        // The live run's page moves its clock every second while the run goes (Runs/LiveRun).
        services.AddSingleton(Components.Runs.LiveRunClock.EverySecond);
        services.AddHostedService<Runs.RunsStopWithTheService>();
        // The schedules, every minute (ServerScheduler).
        services.AddSingleton<Runs.ServerScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Runs.ServerScheduler>());
        // Proposed changes nobody decided in server.json's ProposalsKeptDays are dropped, and said (StagingRetention).
        services.AddHostedService<Runs.StagingRetention>();
        // What arrives in the inbox, by mail, when server.json names who to tell (MailNotices).
        services.AddHostedService(sp => new Runs.MailNotices(sp.GetRequiredService<Runs.ServerEngine>(), options,
            sp.GetRequiredService<ServerFolders>(), sp.GetRequiredService<ILogger<Runs.MailNotices>>()));
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        services.AddSingleton(sp => new NetworkGate(options.Subnets(), options.HostNames, sp.GetRequiredService<ILogger<NetworkGate>>()));

        // The Host header is NetworkGate's, after the subnets. The framework's own host filter runs ahead of the whole
        // pipeline when it has names to filter by, and answered a network the service does not answer with its
        // "Invalid Hostname" page; told every name is allowed, it passes everything on to the gate.
        services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(o => o.AllowedHosts = ["*"]);
    }

    private static string BuildVersion()
        => typeof(ServerApp).Assembly
               .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
               .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
               .FirstOrDefault()?.InformationalVersion
           ?? "unknown";
}

/// <summary>Where the service keeps what is its own: <c>server.json</c>, its lock, its log.</summary>
internal sealed record ServerFolders(string Data)
{
    /// <summary>
    /// The service account's <c>%APPDATA%\Enactive</c> - the same folder its engine settings are in (AppSettings), so
    /// its secrets are protected for the account that reads them (ADR 0002 §7).
    /// </summary>
    public static string Default()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enactive");
}

namespace Enactive.Server;

using System.Security.Cryptography.X509Certificates;
using Enactive.Server.Components;
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
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ServerFolders(dataFolder));

        var logFile = new FileLogSink(Path.Combine(dataFolder, "logs", "server"));
        builder.Services.AddSingleton(logFile);
        builder.Logging.AddProvider(new FileLoggerProvider(logFile));

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // HTTPS on every endpoint, and no other listener: there is no "http first, certificate later".
            foreach (var endpoint in options.Endpoints())
                kestrel.Listen(endpoint, listen => listen.UseHttps(certificate
                    ?? throw new InvalidOperationException("The service does not listen without a certificate.")));
        });

        // The keys that protect sign-in cookies and the antiforgery tokens: in the service's data folder, encrypted
        // for its account. Left to the default they went into the profile's ASP.NET folder, apart from everything else
        // the service keeps and from its backups.
        builder.Services.AddDataProtection()
            .SetApplicationName(ServiceName)
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataFolder, "keys")))
            .ProtectKeysWithDpapi();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        builder.Services.AddSingleton(sp => new NetworkGate(options.Subnets(), options.HostNames, sp.GetRequiredService<ILogger<NetworkGate>>()));

        // Only the names the service is reached by, in the Host header. A name anyone can point at the service's
        // address (DNS rebinding) would otherwise make a page of theirs same-origin with it.
        builder.Services.AddHostFiltering(o =>
        {
            o.AllowedHosts = options.HostNames.ToList();
            o.AllowEmptyHosts = false;
        });

        adjust?.Invoke(builder);

        var app = builder.Build();

        // First, before anything answers: the subnets and the origin (NetworkGate), then the Host header.
        var gate = app.Services.GetRequiredService<NetworkGate>();
        app.Use((context, next) => gate.InvokeAsync(context, _ => next()));
        app.UseHostFiltering();

        // Browsers are told to come back over HTTPS only, for a year; the service never answers plain HTTP anyway.
        app.UseHsts();
        app.UseAntiforgery();
        app.MapStaticAssets();

        // For the installer and a monitor: up, and which build. Says nothing else - it answers before anyone signs in.
        app.MapGet("/health", () => Results.Json(new { status = "ok", version = BuildVersion() }));

        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
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

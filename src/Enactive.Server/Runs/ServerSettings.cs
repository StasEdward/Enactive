namespace Enactive.Server.Runs;

using System.Security.Claims;
using System.Security.Cryptography;
using Enactive.Server.Accounts;
using Enactive.Settings;
using Enactive.Tools.Mcp;

/// <summary>The settings as an Admin opened them: a copy to edit, and the version it was opened at.</summary>
internal sealed record OpenedSettings(AppSettings Copy, string Version);

/// <summary>
/// The service's engine settings, edited from the browser by an Admin (server plan phase 3): providers, models,
/// workers, the shell rule, mail, web search - the service account's own <c>settings.json</c>.
///
/// <para><b>An older copy does not overwrite a newer.</b> Two Admins with the page open, or an Admin and somebody at
/// the desktop of the service's account: the second save was the one that counted, and the first person's change was
/// gone without a word. The version a page opened is the file's content hash, compared at save - no field added to the
/// file, so the desktop and older builds read it as they always did, and a change from anywhere is seen.</para>
///
/// <para><b>Secrets</b> - API keys, the mail password - are DPAPI-protected for the service's account when written
/// (AppSettings.Save), decrypted only in the service's memory, and never sent to a page: a page shows whether one is
/// set and takes a new one.</para>
/// </summary>
internal sealed class ServerSettings(UserStore users, ServerEngine engine)
{
    /// <summary>A copy of the settings as they are on disk, for an Admin; null and why for anybody else.</summary>
    public (OpenedSettings? Opened, string? Problem) Open(ClaimsPrincipal user)
    {
        if (Admin(user) is null)
            return (null, NotAdmin(user));
        return (new OpenedSettings(AppSettings.Load(engine.SettingsFile), VersionNow()), null);
    }

    /// <summary>Saves an edited copy, if nothing changed the file since it was opened; what to tell the person.</summary>
    public (bool Saved, string Said) Save(ClaimsPrincipal user, OpenedSettings edited)
    {
        if (Admin(user) is not { } admin)
            return (false, NotAdmin(user));

        if (VersionNow() != edited.Version)
            return (false, "The settings were changed since you opened them - by another page, or at the service's own "
                           + $"desktop (last written {File.GetLastWriteTime(engine.SettingsFile):yyyy-MM-dd HH:mm}). "
                           + "Nothing was saved; open them again and make your change there.");

        // Validated before it is written: a configuration that cannot be built, written anyway, takes the service down
        // at its next start, which reads the same file (SettingsSave says the same for the window).
        if (edited.Copy.Validate() is { Count: > 0 } problems)
            return (false, "Not saved - " + string.Join(" ", problems));

        if (!edited.Copy.Save(engine.SettingsFile, replaceUnreadable: true))
            return (false, "Not saved - " + (edited.Copy.LastSaveError ?? "the file could not be written."));

        engine.Reload();
        users.Audit(admin.Name, "settings saved",
            $"{edited.Copy.Providers.Count} provider(s), {edited.Copy.Workers.Count} worker(s)"
            + (engine.Problem is { } problem ? "; " + problem : ""));
        return (true, engine.Problem is { } still
            ? "Saved. " + still
            : "Saved. The next run uses them; runs already going finish on what they started with.");
    }

    /// <summary>
    /// Connects to an MCP server as the page has it and lists its tools: what it offers, or why it could not be
    /// reached. An Admin's - a stdio server is a program the service STARTS, as its own account.
    /// </summary>
    public async Task<string> TestMcpAsync(ClaimsPrincipal user, McpServerConfig config, CancellationToken ct)
    {
        if (Admin(user) is null)
            return NotAdmin(user);
        if (config.Validate() is { } problem)
            return problem;
        try
        {
            // Started in the data folder unless the server names its own: the service has no "current" project.
            await using var connection = await McpConnection.ConnectAsync(config, Path.GetDirectoryName(engine.SettingsFile)!, ct);
            return $"Connected · {connection.Tools.Count} tool(s): " + string.Join(", ", connection.Tools.Select(t => t.Definition.Name));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"No answer within {config.TimeoutSeconds} s.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type, not the message: a failed start can echo the command line, and with it what the environment
            // was built from.
            return $"Could not connect ({ex.GetType().Name}). Check the command or address, the credentials, and that the server runs.";
        }
    }

    /// <summary>The file's content, as a version: what a save compares against.</summary>
    private string VersionNow()
    {
        if (!File.Exists(engine.SettingsFile))
            return "none";
        using var stream = File.OpenRead(engine.SettingsFile);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private Account? Admin(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is { Role: Role.Admin } account ? account : null;

    private string NotAdmin(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is null
            ? "Your session has ended. Sign in again."
            : "Your role does not include the service's settings.";
}

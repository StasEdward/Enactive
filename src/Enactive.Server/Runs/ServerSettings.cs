namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Permissions;
using Enactive.Providers;
using Enactive.Server.Accounts;
using Enactive.Settings;
using Enactive.Tools.Mcp;

/// <summary>
/// The settings as an Admin opened them: a copy to edit, which holds no secret (AppSettings.WithoutSecrets), and the
/// version it was opened at.
/// </summary>
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
/// <para><b>Secrets</b> - API keys, the mail password, MCP credentials - are DPAPI-protected for the service's account
/// when written (AppSettings.Save) and never reach a page, not even the copy a page edits: that copy is made without
/// them (AppSettings.WithoutSecrets), and Save puts back each one the page left alone from the file it reads again
/// (RestoreSecrets). A page says whether one is kept, takes a new one, and removes one only on purpose. Testing a
/// provider or an MCP server with what is kept happens here, as the service, for the same reason.</para>
/// </summary>
/// <param name="grants">
/// The standing write grants the service's runs are checked against - its account's own (WritableRoots.Default, which
/// RunEngineResources uses); a test's file otherwise.
/// </param>
internal sealed class ServerSettings(UserStore users, ServerEngine engine, WorkspaceRoots roots, WritableRoots? grants = null)
{
    private readonly WritableRoots _grants = grants ?? WritableRoots.Default;

    /// <summary>What a refused save starts with, so a host that says "Not saved" itself can say it once.</summary>
    public const string NotSaved = "Not saved - ";

    /// <summary>A copy of the settings as they are on disk, without their secrets, for an Admin; null and why for anybody else.</summary>
    public (OpenedSettings? Opened, string? Problem) Open(ClaimsPrincipal user)
    {
        if (Admin(user) is null)
            return (null, NotAdmin(user));
        var (settings, version) = AppSettings.LoadVersioned(engine.SettingsFile);
        return (new OpenedSettings(settings.WithoutSecrets(), version), null);
    }

    /// <summary>
    /// The window's settings (SettingsViewModel) over a copy opened for an Admin, for the settings page: its Save comes
    /// back here as the person (<see cref="Save"/>), and what the service said of a save that was made goes to
    /// <paramref name="saved"/>. A save refused is said by the view model, as the window's is.
    /// </summary>
    /// <param name="templatesWorkspace">
    /// The workspace whose templates the Templates section lists beside the global ones - the one the Templates screen
    /// opens on - or null for none.
    /// </param>
    public (SettingsViewModel? View, string? Problem) Edit(ClaimsPrincipal user, string? templatesWorkspace, Action<string> saved)
    {
        var (opened, problem) = Open(user);
        if (opened is null)
            return (null, problem);

        var view = new SettingsViewModel(
            opened.Copy,
            working =>
            {
                var (done, said) = Save(user, opened with { Copy = working });
                // The view model says a refusal as "Not saved — " and the reason, as the window's does.
                if (!done)
                    throw new InvalidOperationException(said.StartsWith(NotSaved, StringComparison.Ordinal) ? said[NotSaved.Length..] : said);
                saved(said);
            },
            // The service has no start-up to change (Windows starts it) and no Remote access (it pairs with no gateway).
            startup: NoStartupEntry.Instance,
            // Its templates are changed through ServerLibrary as the person, and its grants not from a page at all: the
            // view model's own writes to them are off, whatever a section might wire later.
            editsFilesHere: false,
            workspaceRoot: templatesWorkspace,
            toolNames: engine.ToolNames(),
            hostPane: null,
            providerCalls: new AsTheAdmin(this, user),
            // Its engine's grants and its own workspaces: the files the service's runs read. Shown, and revoked through
            // RevokeWritableFolder - the view model's own Revoke is off here (editsFilesHere).
            writableRoots: _grants,
            knownWorkspaces: GrantedWorkspaces);
        return (view, null);
    }

    /// <summary>
    /// The folders the service runs tasks under (server.json, WorkspaceRoots), for an Admin: set when the service was
    /// installed and read only here - a page that could add one could open every folder of the server to a run.
    /// </summary>
    public (IReadOnlyList<string>? Roots, string? Problem) WorkspaceRoots(ClaimsPrincipal user)
        => Admin(user) is null ? (null, NotAdmin(user)) : (roots.All, null);

    /// <summary>
    /// Takes back one standing write grant - a folder outside a workspace that runs there may write to, kept from a
    /// decision card's "keep for this workspace" - for an Admin, at once and audited, as the window's Revoke is at once
    /// (WritableRootsSettingsViewModel.RevokeWritableRoot). On the service any Operator can make such a grant from a card;
    /// without this the settings listed none of them and nothing on a page could take one back.
    ///
    /// <para>The grant is found again in the store by its key and folder, not taken from the page: one revoked since the
    /// page was drawn is said to be gone rather than revoked twice. An orphan - its workspace no longer known - is
    /// forgotten whole, as the window does, there being no path left to name one folder by.</para>
    /// </summary>
    public (bool Revoked, string Said) RevokeWritableFolder(ClaimsPrincipal user, string key, string folder)
    {
        if (Admin(user) is not { } admin)
            return (false, NotAdmin(user));

        var entry = _grants.Review(GrantedWorkspaces())
            .FirstOrDefault(g => g.Key == key && g.Roots.Contains(folder, StringComparer.OrdinalIgnoreCase));
        if (entry is null)
            return (false, $"{folder} is no longer kept for that workspace.");

        if (entry.IsOrphan)
            _grants.Forget(entry.Key);
        else
            _grants.Revoke(entry.WorkspaceRoot!, folder);

        users.Audit(admin.Name, "writable folder revoked", entry.IsOrphan
            ? $"{string.Join(", ", entry.Roots)} for a workspace the service no longer knows"
            : $"{folder} for {entry.WorkspaceRoot}");
        return (true, entry.IsOrphan
            ? $"Revoked: every folder kept for that workspace ({string.Join(", ", entry.Roots)})."
            : $"Revoked: runs in {entry.WorkspaceRoot} are asked again before writing to {folder}.");
    }

    /// <summary>
    /// The workspaces the grants are named by: every one the service's registry knows, not only those under the roots
    /// (KnownWorkspaces) - read through the roots, a grant of a workspace outside them read as an orphan, and its Revoke
    /// forgot every folder kept for it.
    /// </summary>
    private IEnumerable<string> GrantedWorkspaces() => engine.Workspaces.Entries.Select(e => e.RootPath);

    /// <summary>Saves an edited copy, if nothing changed the file since it was opened; what to tell the person.</summary>
    public (bool Saved, string Said) Save(ClaimsPrincipal user, OpenedSettings edited)
    {
        if (Admin(user) is not { } admin)
            return (false, NotAdmin(user));

        // On a copy: the page keeps what it sent, which must go on holding no secret if this is refused. What is kept
        // follows the page's other fields - the mail password a changed SMTP host, a key a changed address; see
        // WithKeptKey for why an Admin may do that.
        var copy = edited.Copy.Clone();
        // The final save checks the version under the shared file lock; a concurrent change after
        // this read must refuse the save rather than restore secrets from the wrong version.
        if (copy.RestoreSecrets(AppSettings.Load(engine.SettingsFile)) is { Count: > 0 } missing)
            return (false, NotSaved + string.Join(", ", missing) + " could not be found in the settings as they are now. "
                           + "Open them again and make your change there.");

        // Validated before it is written: a configuration that cannot be built, written anyway, takes the service down
        // at its next start, which reads the same file (SettingsSave says the same for the window).
        if (copy.Validate() is { Count: > 0 } problems)
            return (false, NotSaved + string.Join(" ", problems));

        if (!copy.Save(engine.SettingsFile, replaceUnreadable: true, expectedVersion: edited.Version))
            return (false, NotSaved + (copy.LastSaveError ?? "the file could not be written."));

        engine.Reload();
        users.Audit(admin.Name, "settings saved",
            $"{copy.Providers.Count} provider(s), {copy.Workers.Count} worker(s)"
            + (engine.Problem is { } problem ? "; " + problem : ""));
        return (true, engine.Problem is { } still
            ? "Saved. " + still
            : "Saved. The next run uses them; runs already going finish on what they started with.");
    }

    /// <summary>
    /// Connects to an MCP server as the page has it and lists its tools: what it offers, or why it could not be
    /// reached. An Admin's - a stdio server is a program the service STARTS, as its own account. Credentials the page
    /// left as they were are the ones kept for it, which the page never held.
    /// </summary>
    public async Task<string> TestMcpAsync(ClaimsPrincipal user, McpServerConfig config, CancellationToken ct)
    {
        if (Admin(user) is not { } admin)
            return NotAdmin(user);
        // Kept credentials follow the command or address as the page has it now - an Admin's to change, who can start
        // any program as the service anyway (see WithKeptKey): never on the page is the guarantee.
        var kept = new AppSettings { McpServers = [config.Clone()] };
        var usesKept = kept.NamesKeptSecrets();
        kept.RestoreSecrets(AppSettings.Load(engine.SettingsFile));
        config = kept.McpServers[0];
        if (config.Validate() is { } problem)
            return problem;
        if (usesKept)
            AuditKeptUse(admin, "MCP server tested", config.Id,
                // The command without its arguments: they are the person's to type and may hold what they should not.
                config.Transport == McpTransportKind.Stdio ? config.Command : Address(config.Url));
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

    /// <summary>
    /// A provider asked as the settings page asks it (IProviderCalls), as the service: only for an Admin still, and with
    /// the key kept for it where the page's box was left empty - the page never holds one, and asked with none a
    /// provider that is fine was drawn red.
    /// </summary>
    private sealed class AsTheAdmin(ServerSettings settings, ClaimsPrincipal user) : IProviderCalls
    {
        public Task<ProviderStatus> CheckAsync(ProviderConfig provider, string? model)
            => settings.Admin(user) is not { } admin
                ? Task.FromResult(new ProviderStatus(ProviderHealth.Unknown, settings.NotAdmin(user), [], DateTimeOffset.UtcNow))
                : DirectProviderCalls.Instance.CheckAsync(settings.WithKeptKey(provider, admin, "provider checked"), model);

        public Task<List<string>> ModelsAsync(ProviderConfig provider)
            => settings.Admin(user) is not { } admin
                ? throw new InvalidOperationException(settings.NotAdmin(user))
                : DirectProviderCalls.Instance.ModelsAsync(settings.WithKeptKey(provider, admin, "provider checked", " - its models listed"));
    }

    /// <summary>
    /// The provider as typed, with the key and headers kept for it put back where the page left them alone.
    ///
    /// <para>They go wherever the page now points: a kept key follows an Admin's changed base URL, as kept MCP
    /// credentials follow a changed command or address (<see cref="TestMcpAsync"/>) and the kept mail password a changed
    /// SMTP host on Save. Accepted: an Admin can already run any program as the service's account (a stdio MCP server
    /// is one), so what this guards is that a secret is never on a page, not that an Admin can never use one.</para>
    /// </summary>
    private ProviderConfig WithKeptKey(ProviderConfig typed, Account admin, string action, string what = "")
    {
        var kept = new AppSettings { Providers = [typed.Clone()] };
        var usesKept = kept.NamesKeptSecrets();
        kept.RestoreSecrets(AppSettings.Load(engine.SettingsFile));
        if (usesKept)
            AuditKeptUse(admin, action, typed.Id, Address(typed.BaseUrl) + what);
        return kept.Providers[0];
    }

    /// <summary>
    /// A kept secret used where an Admin pointed it - a provider's address, an MCP server's command or address - is
    /// audited with what it was sent to, never the secret: the Admin may point it anywhere (see above), and then the trail
    /// says where it went and by whose hand. Unaudited, a key sent to a host of somebody's choosing left no trace.
    /// </summary>
    private void AuditKeptUse(Account admin, string action, string id, string endpoint)
        => users.Audit(admin.Name, action, $"{id}: {endpoint} (with the credentials kept for it)");

    /// <summary>
    /// Where an address goes - its scheme, host, port and path - and nothing else. An address an Admin types may carry a
    /// credential of its own (https://user:pass@host, ?key=..., ?token=...), and written whole into the audit trail it put
    /// that credential where every Admin reads it. One that cannot be read is not written at all: what it holds is unknown.
    /// </summary>
    internal static string Address(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "(an address that could not be read)";

    private Account? Admin(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is { Role: Role.Admin } account ? account : null;

    private string NotAdmin(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is null
            ? "Your session has ended. Sign in again."
            : "Your role does not include the service's settings.";
}

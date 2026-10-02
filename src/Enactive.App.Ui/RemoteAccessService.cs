using Enactive.Core.Context;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;
using Enactive.Workspace;
using Enactive.Settings;

namespace Enactive.App.Ui;

/// <summary>
/// Holds this computer's end of remote access open for as long as the application is running.
///
/// <para>Everything it needs from the window arrives as a callback, because the window owns the
/// things this needs and none of them can be read from a background thread on demand: which
/// workspaces exist, what the autonomy slider says, and who answers a permission question at the
/// machine.</para>
///
/// <para>It never throws at its caller. A gateway that is down, a token that was revoked, a
/// workspace that has been deleted - none of these are the desktop's problem to crash over, and all
/// of them are things a person needs told rather than discovered. <see cref="Status"/> is what they
/// are told, and <see cref="Changed"/> is when.</para>
/// </summary>
internal sealed class RemoteAccessService : IAsyncDisposable
{
    /// <summary>
    /// How often the gateway is asked for work. Also the longest a task started from a phone waits
    /// before this computer notices it, which is why it is seconds and not minutes.
    /// </summary>
    private static readonly TimeSpan SyncEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often queued events are pushed out between syncs.
    ///
    /// <para>Separate from <see cref="SyncEvery"/> because they answer opposite questions. Asking
    /// for work is polling and fifteen seconds of it costs nothing; sending a progress line is a
    /// person watching a screen, and fifteen seconds of nothing there reads as a run that has
    /// stopped.</para>
    /// </summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait between attempts to reconnect after the gateway refused to be reached.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    /// <summary>
    /// What a person is told while this computer has no protocol-2 keys. Connecting without them would
    /// publish workspaces nobody can read and take commands this computer cannot open, so it does not.
    /// </summary>
    public const string NeedsPairing =
        "Remote access needs this computer to be connected with a connection code - see Settings, Remote access.";

    private readonly RemoteAccessSettings _settings;
    private readonly Func<CancellationToken, Task<IGatewayConnection>> _connect;
    private readonly TimeSpan _firstRetry;

    /// <summary>
    /// The keys, as handed in or as read from remote.db under the settings' computer id - and the key
    /// store itself when this service made it, because then it is this service's to dispose.
    /// </summary>
    private IHostKeys? _keys;
    private HostKeyStore? _ownedKeys;
    private Sealer? _sealer;
    private readonly Func<WorkspaceEntry, Task<RunEnvironment>> _environment;
    private readonly Func<IReadOnlyList<WorkspaceEntry>> _workspaces;
    private readonly IDecisionHandler _desktop;
    private readonly string _databasePath;

    private readonly CancellationTokenSource _stopping = new();
    private readonly BackgroundRunGroup _runs = new();
    private readonly object _lifecycle = new();
    private Task? _shutdown;

    private HostStore? _store;
    private IGatewayConnection? _connection;
    private Task? _loop;
    private bool _recovered;
    private volatile bool _connectedSinceFailure;
    private string _status = "Not connected.";

    /// <summary>
    /// The runner and the approvals it is waiting on, made ONCE and kept across reconnections.
    ///
    /// <para>They belong to this computer, not to a connection. Rebuilding them when the socket
    /// came back would orphan everything in flight: a run started ten minutes ago would no longer
    /// be cancellable, because Cancel looks a run up in the runner that started it, and a
    /// permission the phone was about to answer would be waited on by an object nobody can reach.
    /// The connection is the thing that comes and goes; only <see cref="DeliveryLoop"/> is rebuilt
    /// with it.</para>
    /// </summary>
    private readonly RemoteApprovals _approvals = new();

    /// <param name="keys">
    /// This computer's keys, or null to read them from remote.db under the computer id the settings
    /// hold - which is what the application does. With no id there are none: the computer has not been
    /// connected with a code, and the service says so and does not connect.
    /// </param>
    /// <param name="environment">
    /// The run setup for one workspace. A function of the WORKSPACE rather than a value, because
    /// the autonomy level, worker and staging flag are facts about a folder: a task naming one
    /// project must not be governed by the slider belonging to whichever project is open on the
    /// desktop. A function rather than a table read once, because those settings are editable and
    /// a task arriving in an hour should run under what they say then.
    /// </param>
    /// <param name="desktop">
    /// Who answers a permission question at this machine. It is wrapped, not replaced: a question
    /// raised by a remote run appears on the desktop AND on the phone, and whichever answers first
    /// decides.
    /// </param>
    /// <param name="connect">
    /// Opens a connection to the gateway. The SignalR one unless a test hands in a fake; what the
    /// service does with a refusal is the thing under test, and a real gateway cannot be made to speak
    /// another protocol on cue.
    /// </param>
    /// <param name="firstRetry">
    /// The first wait before connecting again after a connection was lost. Five seconds unless a test
    /// needs to see a reconnection without waiting for one.
    /// </param>
    public RemoteAccessService(
        RemoteAccessSettings settings,
        IHostKeys? keys,
        Func<WorkspaceEntry, Task<RunEnvironment>> environment,
        Func<IReadOnlyList<WorkspaceEntry>> workspaces,
        IDecisionHandler desktop,
        string databasePath,
        Func<CancellationToken, Task<IGatewayConnection>>? connect = null,
        TimeSpan? firstRetry = null)
    {
        _firstRetry = firstRetry ?? TimeSpan.FromSeconds(5);
        _settings = settings;
        _keys = keys;
        _sealer = keys is null ? null : new Sealer(keys, TimeProvider.System);
        _connect = connect ?? ConnectSignalRAsync;
        _environment = environment;
        _workspaces = workspaces;
        _desktop = desktop;
        _databasePath = databasePath;
    }

    /// <summary>What a person should be told about remote access, in one line.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;

            _status = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when <see cref="Status"/> changed. Not on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>What "Add a device" says when there is no connection to make an invitation on.</summary>
    public const string NotConnectedForInvite =
        "This computer is not connected to the gateway right now, so it cannot make an invitation. Try again once it is.";

    /// <summary>
    /// Adding devices by invitation, on the connection that is up - null while there is none past Hello,
    /// and when the keys were handed in rather than read from remote.db (a test's).
    /// </summary>
    public KeyAdministration? KeyAdministration => _administration;

    private volatile KeyAdministration? _administration;

    /// <summary>A device answered an invitation and was admitted; its label. Not on the UI thread.</summary>
    public event Action<string>? DeviceAdmitted;

    /// <summary>An answer to an invitation was refused, or set aside. Not on the UI thread.</summary>
    public event Action<AdmissionNotice>? InvitationNoticed;

    /// <summary>
    /// Opens an invitation on the connection that is up, for the gateway the settings name. The link holds
    /// the pairing secret: the caller shows it and wipes <see cref="InviteLink.PairingSecret"/> after.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no connection past Hello.</exception>
    public Task<InviteLink> InviteAsync(CancellationToken ct)
        => _administration is { } administration
            ? administration.InviteAsync(new Uri(_settings.GatewayUrl), ct)
            : throw new InvalidOperationException(NotConnectedForInvite);

    /// <summary>
    /// Closes an invitation nobody answered - its window was cancelled or ran out. Here rather than on the
    /// connection, because the invitation is in the key store and the connection may be down.
    /// </summary>
    public void WithdrawInvite(string inviteId) => (_keys as HostKeyStore)?.ForgetInvite(inviteId);

    /// <summary>Every device this computer has trusted, revoked ones included; none before its keys are read.</summary>
    public IReadOnlyList<TrustedDevice> TrustedDevices() => (_keys as HostKeyStore)?.Trusted ?? [];

    /// <summary>Runs started from a phone that are still going, by remote run id.</summary>
    public IReadOnlyCollection<string> Running => _runner?.Running ?? [];

    private RemoteRunner? _runner;

    /// <summary>
    /// Starts the connection if the settings say to, and returns immediately either way.
    ///
    /// <para>Returning immediately matters more than it looks: this is called while the main window
    /// is being built, and a gateway that is slow to answer must not be able to hold up the
    /// application starting.</para>
    /// </summary>
    public void Start()
    {
        if (!_settings.Enabled)
        {
            Status = "Remote access is off.";
            return;
        }

        if (!TryKeys(out var why))
        {
            Status = why;
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.GatewayUrl) || string.IsNullOrEmpty(_settings.Token))
        {
            Status = "Remote access is on but not set up - see Settings, Remote access.";
            return;
        }

        lock (_lifecycle)
        {
            if (_shutdown is not null || _loop is not null) return;
            _loop = Task.Run(() => RunAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Makes sure this service has keys to seal and open with, reading them from remote.db under the
    /// computer id the settings hold. False, with what a person should be told, when it cannot.
    ///
    /// <para>Read once and kept: a key store is one per remote.db, and a second one would rotate from
    /// its own idea of the newest epoch.</para>
    /// </summary>
    private bool TryKeys(out string why)
    {
        lock (_lifecycle)
        {
            why = string.Empty;

            if (_sealer is not null)
                return true;

            if (_shutdown is not null)
            {
                why = "Remote access is stopping.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_settings.HostId))
            {
                why = NeedsPairing;
                return false;
            }

            try
            {
                var store = Store();

                // A computer id with no keys behind it is a remote.db that was deleted or replaced.
                // Making keys now would connect a computer that no device can read - the same silent
                // breakage as replacing keys that cannot be read - so it is said instead.
                if (!HostKeyStore.HasKeys(store))
                {
                    why = "This computer's remote keys are missing - connect it again with a new connection code.";
                    return false;
                }

                _ownedKeys = new HostKeyStore(store, _settings.HostId);
            }
            catch (HostKeysUnreadableException unreadable)
            {
                why = unreadable.Message;
                return false;
            }
            catch (Exception failure)
            {
                why = $"Remote access could not open this computer's records: {failure.Message}";
                return false;
            }

            _keys = _ownedKeys;
            _sealer = new Sealer(_ownedKeys, TimeProvider.System);
            return true;
        }
    }

    /// <summary>
    /// Opened once and kept: the store is this computer's record of what it was asked to do and what
    /// it has not yet managed to report, and it must outlive any one connection.
    /// </summary>
    private HostStore Store()
    {
        lock (_lifecycle)
            return _store ??= new HostStore(_databasePath);
    }

    private async Task<IGatewayConnection> ConnectSignalRAsync(CancellationToken ct)
    {
        var connection = new SignalRGatewayConnection(GatewayAddress.Hub(_settings.GatewayUrl), _settings.Token);
        try
        {
            await connection.StartAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// "Test connection": the stored settings tried once against the gateway - hello, and a sync with
    /// the workspace list sealed as the running connection seals it - and what happened, in a sentence.
    ///
    /// <para>The stored settings, because they are the only ones there are: the address, the computer
    /// and the token all come from one connection code, and there is nothing typed to try instead.</para>
    /// </summary>
    public async Task<string> CheckAsync(CancellationToken ct)
    {
        if (!TryKeys(out var why))
            return why;

        var check = await GatewayProbe.CheckAsync(
            _settings.GatewayUrl, _settings.Token, Publishable(_workspaces(), _sealer!), ct);

        return check.Detail;
    }

    /// <summary>
    /// Applies a connection code: the keys in remote.db (see <see cref="Pairing.ConnectAsync"/>), then
    /// the settings it names - the gateway, the computer and its token, and remote access turned on.
    /// The caller saves the settings; the token reaches disk only as DPAPI ciphertext.
    ///
    /// <para>The questions are asked first, while a running service may still hold a key store over
    /// <paramref name="databasePath"/>; <paramref name="beforeChange"/> is where the caller stops it,
    /// once every answer is yes and before anything is written. Replacing keys under a running service
    /// would leave it sealing with keys written nowhere. Nothing in the settings changes when the
    /// person declines or the code is refused.</para>
    /// </summary>
    public static async Task<PairingOutcome> ConnectWithCodeAsync(
        ConnectionCode code, RemoteAccessSettings settings, string databasePath,
        Func<string, Task<bool>> confirm, Func<Task>? beforeChange = null)
    {
        PairingOutcome outcome;
        using (var store = new HostStore(databasePath))
        {
            outcome = await Pairing.ConnectAsync(store, settings.HostId, settings.GatewayUrl, code, confirm, beforeChange);
        }

        if (outcome.Paired)
            Remember(code, settings);

        return outcome;
    }

    /// <summary>
    /// What a code leaves in the settings: the gateway, the computer and its token, and remote access
    /// on. One place, because the settings window writes the same into its own copy - a copy that
    /// still held the old values would put them back at the next Save.
    /// </summary>
    public static void Remember(ConnectionCode code, RemoteAccessSettings settings)
    {
        settings.GatewayUrl = code.Gateway.GetLeftPart(UriPartial.Authority);
        settings.HostId = code.HostId;
        settings.Token = code.Token;
        settings.Enabled = true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _firstRetry;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(ct);

                // Came back without an exception: the loop decided to stop, and it has already said
                // why. Reconnecting would only reach the same verdict.
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                if (_connectedSinceFailure)
                {
                    _connectedSinceFailure = false;
                    backoff = _firstRetry;
                }

                Status = $"Not connected: {failure.Message} Trying again in {backoff.TotalSeconds:0}s.";
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Doubling, capped. A gateway that is down stays down for hours sometimes, and a
            // desktop asking every five seconds for those hours is a machine making noise about a
            // thing nobody can fix from here.
            backoff = backoff < MaxBackoff
                ? TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds))
                : MaxBackoff;
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        var store = Store();
        if (_runner is null)
        {
            _runner = new RemoteRunner(store, _approvals, _sealer!, PrepareAsync);

            // A refused command with no run to report on is said here, the same way a command that
            // failed is: otherwise the only trace of a forged command would be a list nobody reads.
            _runner.Noticed += notice => Status = $"A remote command was refused: {notice.Detail}";
        }

        IGatewayConnection connection;
        try
        {
            connection = await _connect(ct);
        }
        catch (GatewayCredentialRefusedException refused)
        {
            // Returned from, not thrown: RunAsync would dial again, and the same credential is
            // refused the same way every time.
            Status = refused.Message;
            return;
        }

        _connection = connection;
        try
        {
            await ServeAsync(store, connection, ct);
        }
        finally
        {
            if (connection is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    /// <summary>
    /// Serves one connection, from its Hello to its end.
    ///
    /// <para>Every connection starts here, the first and every one after a drop: the SignalR client's
    /// own automatic reconnect is off, because it came back without saying Hello - a gateway redeployed
    /// with another protocol meanwhile was sent Sync and Publish, refused them for reasons that named
    /// neither protocol, and the person never saw the sentence that would have told them to update.</para>
    /// </summary>
    private async Task ServeAsync(HostStore store, IGatewayConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.HelloAsync(RemoteProtocol.Version, ct);
        }
        catch (GatewayRefusedException refused) when (refused.Disposition == FaultDisposition.Fatal)
        {
            // Another protocol, or a credential gone between connecting and the first call. The
            // gateway's sentence says which and what to do; a reconnect would only hear it again.
            Status = $"The gateway refused this computer, so remote access has stopped: {refused.Message}";
            return;
        }

        var loop = new DeliveryLoop(store, connection, _sealer!, _keys as IGrantOutbox);

        // Said as it happens: a device whose grant was refused for good will never be able to read
        // this computer, and the only other trace of that is a list nobody reads.
        loop.Noticed += notice =>
        {
            if (notice.Kind == "GrantDropped")
                Status = notice.Detail;
        };

        // Once per PROCESS, not once per connection. "In flight" means a run with no ending
        // written, and a run going right now is one of those - so doing this after a dropped socket
        // came back would tell the phone that a task still working had been interrupted, while it
        // carried on working. The first connection is the only one where every such run really is
        // the wreckage of a previous process.
        if (!_recovered)
        {
            _recovered = true;
            loop.RecoverInterruptedRuns();
        }

        // Only over keys read from remote.db: invitations and the devices they admit live in that store.
        var hostKeys = _keys as HostKeyStore;
        if (hostKeys is not null)
        {
            var administration = new KeyAdministration(hostKeys, connection, TimeProvider.System);
            administration.Noticed += notice =>
            {
                // A refused answer is said in the status line as well: it is someone other than the
                // invited device holding the link, and the window that asked may have been closed.
                if (notice.Refused)
                    Status = notice.Detail;
                InvitationNoticed?.Invoke(notice);
            };
            _administration = administration;
        }

        // Said after the invitations are ready: "Connected." is what tells the settings pane that Add a
        // device can be pressed, and a press that found no connection to invite on would say it is down.
        Status = "Connected.";

        // A connection that got as far as Hello was a working one, so the wait after it drops starts
        // short again. Without this every drop of a long-lived connection added to the wait, and a
        // computer that lost its network twice in a week waited two minutes to come back.
        _connectedSinceFailure = true;

        try
        {
            await ServeTurnsAsync(connection, loop, hostKeys, ct);
        }
        finally
        {
            // An invitation made on a connection that is gone would be registered nowhere; "Add a device"
            // says there is no connection instead.
            _administration = null;
        }

        if (loop.Stopped)
        {
            // A fatal refusal: a revoked token, a computer the gateway no longer knows. The queue is
            // kept intact and nothing is retried, because retrying is what turns a revoked token
            // into a machine hammering a server it is not allowed to talk to.
            var why = loop.Notices.LastOrDefault(n => n.Kind == "Stopped")?.Detail;

            Status = why is null
                ? "The gateway refused this computer, so remote access has stopped."
                : $"The gateway refused this computer, so remote access has stopped: {why}";
        }
    }

    private async Task ServeTurnsAsync(
        IGatewayConnection connection, DeliveryLoop loop, HostKeyStore? hostKeys, CancellationToken ct)
    {
        var nextSync = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested && !loop.Stopped)
        {
            // A closed connection is left for RunAsync to replace with a new one, which says Hello.
            // Calls on it would only fail one by one, and each failed Publish counts toward parking
            // an event that was never refused.
            if (!connection.IsOpen)
                throw new IOException("The connection to the gateway closed.");

            if (DateTimeOffset.UtcNow >= nextSync)
            {
                nextSync = DateTimeOffset.UtcNow + SyncEvery;

                foreach (var command in await loop.TurnAsync(Publishable(_workspaces(), _sealer!), ct))
                {
                    Begin(command);
                }

                // Asked only while an invitation is open: otherwise it is one more call every turn, for
                // nothing. The grants an answer queues go out with the next flush, two seconds on.
                if (_administration is { } administration && hostKeys!.HasPendingInvites)
                {
                    foreach (var label in await administration.AnswerEnrollmentsAsync(ct))
                    {
                        DeviceAdmitted?.Invoke(label);
                    }
                }
            }
            else
            {
                await loop.FlushAsync(ct);
            }

            await Task.Delay(FlushEvery, ct);
        }
    }

    /// <summary>
    /// The workspaces this computer will let a phone name.
    ///
    /// <para>Read from the registry every sync rather than once: a workspace opened after the
    /// application started should be reachable without restarting it, and one whose folder has been
    /// deleted should stop being offered. A folder that cannot be read is skipped rather than
    /// failing the sync - one missing project must not take remote access down.</para>
    ///
    /// <para>The name is sealed and only the id travels in the clear: a folder's name is often a
    /// client's or a project's, and the gateway needs nothing but the id to route a task.</para>
    /// </summary>
    public static IReadOnlyList<WorkspaceRef> Publishable(IReadOnlyList<WorkspaceEntry> entries, Sealer sealer)
    {
        var published = new List<WorkspaceRef>();

        foreach (var entry in entries)
        {
            try
            {
                if (!Directory.Exists(entry.RootPath))
                    continue;

                var id = WorkspaceInfo.For(entry.RootPath).Id.ToString();
                published.Add(new WorkspaceRef(id, sealer.WorkspaceName(id, entry.Name)));
            }
            catch (Exception)
            {
                // Unreadable folder, a permissions change, a disconnected drive. Not offered.
            }
        }

        return published;
    }

    /// <summary>
    /// Turns one accepted command into a run, on its own task.
    ///
    /// <para>On its own task because a run takes minutes and the delivery loop has to keep going
    /// while it does - otherwise nothing this run produces would be sent until it finished, and a
    /// phone would show a task that started and then said nothing for ten minutes.</para>
    /// </summary>
    private void Begin(HostCommand command)
    {
        _runs.TryStart(async ct =>
        {
            try { await _runner!.ApplyAsync(command, ct); }
            catch (Exception failure)
            {
                Status = $"A remote command could not be carried out: {failure.Message}";
            }
        });
    }

    /// <summary>
    /// Builds the engine for one task started from a phone.
    ///
    /// <para>The same composition the background runs use, with two differences that are the whole
    /// of what "started from a phone" means: the intent says so, and the decision handler is the
    /// desktop's wrapped so that either end can answer.</para>
    /// </summary>
    private async Task<RemotePreparation> PrepareAsync(
        OpenedStart task,
        Func<IDecisionHandler, IDecisionHandler> wrap,
        CancellationToken ct)
    {
        var found = Resolve(task.WorkspaceId)
            ?? throw new InvalidOperationException(
                "This computer has no workspace with that id any more. It may have been removed from "
                + "the workspace list, or its folder may have been deleted or moved.");

        var (workspace, entry) = found;

        // Refused for the same reason a background run is: staging that survives an unattended run
        // needs a store that persists its proposals, and there is not one. Running anyway would
        // write to the files directly while the history said the changes were staged - and here
        // nobody is at the machine to notice the difference.
        if (entry.StageChanges)
        {
            throw new InvalidOperationException(
                $"'{entry.Name}' is set to stage changes, and a task started from the web cannot "
                + "stage: it would write to the files directly while the history claimed otherwise. "
                + "Turn Stage changes off for that workspace to run it from here.");
        }

        var composed = await UnattendedRun.ComposeAsync(
            // The autonomy, worker and staging saved against THE WORKSPACE THIS TASK NAMES - not
            // the slider on the desktop, which is about whatever folder happens to be open there.
            await _environment(entry),
            workspace,
            task.Prompt,
            IntentSource.Remote,
            wrap(_desktop),
            ct);

        return new RemotePreparation(composed.Engine, composed.Intent, composed.Resources);
    }

    /// <summary>
    /// The folder a remote workspace id refers to, or null when this computer no longer has one.
    ///
    /// <para>Null rather than an exception at this level so the refusal reads as what it is - the
    /// one thing the application can say about a workspace id that the library deliberately cannot.
    /// </para>
    /// </summary>
    private (WorkspaceInfo Workspace, WorkspaceEntry Entry)? Resolve(string workspaceId)
    {
        foreach (var entry in _workspaces())
        {
            try
            {
                if (!Directory.Exists(entry.RootPath))
                    continue;

                var workspace = WorkspaceInfo.For(entry.RootPath);

                if (string.Equals(workspace.Id.ToString(), workspaceId, StringComparison.OrdinalIgnoreCase))
                    return (workspace, entry);
            }
            catch (Exception)
            {
                // Same as Publishable: an unreadable folder is not this workspace.
            }
        }

        return null;
    }

    /// <summary>Close admission, cancel and drain accepted commands before disposing their store.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
            return new ValueTask(_shutdown ??= Task.Run(ShutdownAsync));
    }

    private async Task ShutdownAsync()
    {
        var runs = _runs.StopAsync();
        try
        {
            await Task.WhenAll(_stopping.CancelAsync(), runs, _loop ?? Task.CompletedTask);
        }
        catch (Exception)
        {
            // WhenAll has settled every owner, including terminal persistence, even on failure.
        }
        try
        {
            if (_connection is IAsyncDisposable connection) await connection.DisposeAsync();
        }
        finally
        {
            _store?.Dispose();
            _ownedKeys?.Dispose();
            _stopping.Dispose();
        }
    }
}

using Enactive.Core.Context;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
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

    private readonly RemoteAccessSettings _settings;
    private readonly Func<WorkspaceEntry, Task<RunEnvironment>> _environment;
    private readonly Func<IReadOnlyList<WorkspaceEntry>> _workspaces;
    private readonly IDecisionHandler _desktop;
    private readonly string _databasePath;

    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _runs = [];

    private HostStore? _store;
    private SignalRGatewayConnection? _connection;
    private Task? _loop;
    private bool _recovered;
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
    public RemoteAccessService(
        RemoteAccessSettings settings,
        Func<WorkspaceEntry, Task<RunEnvironment>> environment,
        Func<IReadOnlyList<WorkspaceEntry>> workspaces,
        IDecisionHandler desktop,
        string databasePath)
    {
        _settings = settings;
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

        if (string.IsNullOrWhiteSpace(_settings.GatewayUrl) || string.IsNullOrEmpty(_settings.Token))
        {
            Status = "Remote access is on but not set up - see Settings, Remote access.";
            return;
        }

        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);

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
        // Opened once and kept: the store is this computer's record of what it was asked to do and
        // what it has not yet managed to report, and it must outlive any one connection.
        _store ??= new HostStore(_databasePath);
        _runner ??= new RemoteRunner(_store, _approvals, PrepareAsync);

        await using var connection = new SignalRGatewayConnection(
            GatewayAddress.Hub(_settings.GatewayUrl), _settings.Token);

        _connection = connection;
        await connection.StartAsync(ct);

        var loop = new DeliveryLoop(_store, connection);

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

        Status = "Connected.";

        var nextSync = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested && !loop.Stopped)
        {
            if (DateTimeOffset.UtcNow >= nextSync)
            {
                nextSync = DateTimeOffset.UtcNow + SyncEvery;

                foreach (var command in await loop.TurnAsync(Publishable(_workspaces()), ct))
                {
                    Begin(command, ct);
                }
            }
            else
            {
                await loop.FlushAsync(ct);
            }

            Reap();
            await Task.Delay(FlushEvery, ct);
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

    /// <summary>
    /// The workspaces this computer will let a phone name.
    ///
    /// <para>Read from the registry every sync rather than once: a workspace opened after the
    /// application started should be reachable without restarting it, and one whose folder has been
    /// deleted should stop being offered. A folder that cannot be read is skipped rather than
    /// failing the sync - one missing project must not take remote access down.</para>
    /// </summary>
    public static IReadOnlyList<WorkspaceRef> Publishable(IReadOnlyList<WorkspaceEntry> entries)
    {
        var published = new List<WorkspaceRef>();

        foreach (var entry in entries)
        {
            try
            {
                if (!Directory.Exists(entry.RootPath))
                    continue;

                published.Add(new WorkspaceRef(
                    WorkspaceInfo.For(entry.RootPath).Id.ToString(), entry.Name));
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
    private void Begin(HostCommand command, CancellationToken ct)
    {
        var run = Task.Run(async () =>
        {
            try
            {
                await _runner!.ApplyAsync(command, ct);
            }
            catch (Exception failure)
            {
                // ApplyAsync writes the run's own ending itself. Reaching here means something
                // outside a run went wrong - an unknown command kind from a newer gateway, say.
                Status = $"A remote command could not be carried out: {failure.Message}";
            }
        }, ct);

        lock (_runs)
        {
            _runs.Add(run);
        }
    }

    /// <summary>Forgets finished runs, so the list is what is actually going on.</summary>
    private void Reap()
    {
        lock (_runs)
        {
            _runs.RemoveAll(r => r.IsCompleted);
        }
    }

    /// <summary>
    /// Builds the engine for one task started from a phone.
    ///
    /// <para>The same composition the background runs use, with two differences that are the whole
    /// of what "started from a phone" means: the intent says so, and the decision handler is the
    /// desktop's wrapped so that either end can answer.</para>
    /// </summary>
    private async Task<RemotePreparation> PrepareAsync(
        StartTaskPayload task,
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

    /// <summary>
    /// Stops connecting and lets go of the connection.
    ///
    /// <para>Runs in flight are cancelled rather than waited for. They are cancelled the same way
    /// closing the application cancels a background run, and they report Interrupted the next time
    /// this computer connects - which is true, and is better than a desktop that will not close
    /// because a phone started something long.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            if (_loop is not null)
                await _loop;
        }
        catch (Exception)
        {
            // Shutting down. Nothing here is worth reporting to somebody who is closing the app.
        }

        if (_connection is not null)
            await _connection.DisposeAsync();

        _store?.Dispose();
        _stopping.Dispose();
    }
}

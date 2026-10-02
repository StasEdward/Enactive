namespace RemoteHostHarness;

using Enactive.Core.Context;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

/// <summary>
/// One computer, wired the way the desktop's RemoteAccessService wires it: a connection code applied to a
/// key store, Hello, then turns of the delivery loop - every command it accepts carried out by the real
/// runner, owed removals passed on, enrollments answered while an invitation is open. What differs is what
/// a run does (<see cref="ScriptedEngine"/>), where the keys are kept (the test protector: the CI job is
/// Linux, with no DPAPI), and that it says what happened on stdout for the browser test to wait on.
/// </summary>
internal static class Computer
{
    /// <summary>The one workspace this computer offers. Its name is sealed; only the id travels in the clear.</summary>
    private const string WorkspaceId = "harness-workspace";

    /// <summary>
    /// How often it syncs. The gateway allows a computer 120 hub calls a minute; a sync every half second is
    /// all of them, and the events of a run are refused on top. Once a second is still well inside what a
    /// browser test waits for.
    /// </summary>
    private static readonly TimeSpan SyncEvery = TimeSpan.FromSeconds(1);

    /// <summary>Between syncs, events owed are sent this often; a flush with nothing owed makes no call.</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(250);

    public static async Task<int> RunAsync(string[] args)
    {
        // A folder of its own unless told otherwise, deleted at the end: the keys in it are written in the clear.
        var named = Option(args, "--data");
        var data = named ?? Path.Combine(Path.GetTempPath(), "enactive-harness-" + Guid.NewGuid().ToString("N"));
        var workspaceName = Option(args, "--workspace") ?? "Harness workspace";
        Directory.CreateDirectory(data);

        var text = await Console.In.ReadLineAsync();
        if (Pairing.TryRead(text, out var problem) is not { } code)
        {
            Console.Error.WriteLine($"Not a connection code: {problem}");
            return 2;
        }

        // The test ends this computer by closing stdin, so a test that dies leaves no process behind it.
        using var stopping = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is not null) { }
            await stopping.CancelAsync();
        });

        try
        {
            await ServeAsync(code, data, workspaceName, stopping.Token);
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine($"The harness stopped: {failure}");
            return 1;
        }
        finally
        {
            if (named is null)
                Forget(data);
        }
    }

    private static void Forget(string data)
    {
        try
        {
            // SQLite's pool can still hold the file a moment after the store is disposed.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(data, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task ServeAsync(ConnectionCode code, string data, string workspaceName, CancellationToken ct)
    {
        using var store = new HostStore(Path.Combine(data, "remote.db"));
        using var keys = new HostKeyStore(store, code.HostId, protector: SecretProtector.ForTestsOnly);
        var gateway = code.Gateway.GetLeftPart(UriPartial.Authority);
        Pairing.Apply(code, keys);

        await using var connection = new SignalRGatewayConnection(GatewayAddress.Hub(gateway), code.Token);
        await connection.StartAsync(ct);
        await connection.HelloAsync(RemoteProtocol.Version, ct);

        var sealer = new Sealer(keys, TimeProvider.System);
        // The grants go out through a window that names each one, so a test can see whom the computer gave a key -
        // what the gateway does with a grant afterwards (refuse it, sweep it, hide it from a removed device) would
        // otherwise make a computer that granted the wrong device look exactly like one that did not.
        var loop = new DeliveryLoop(store, connection, sealer, new SaidGrants(keys));
        var administration = new KeyAdministration(keys, connection, TimeProvider.System);
        var runner = new RemoteRunner(store, new RemoteApprovals(), sealer, (task, wrap, _) =>
        {
            Say("RUN", task.RunId);

            // The desktop's handler wrapped as the desktop's is, so a permission goes through the real
            // RemoteDecisionHandler: published sealed, answered from the panel, checked against its hash.
            var engine = new ScriptedEngine(task.RunId, wrap(new NobodyAtTheDesk()), store, sealer);

            return Task.FromResult(new RemotePreparation(engine, new Intent(
                Guid.NewGuid(), task.Prompt, IntentSource.Remote,
                new WorkContext(Guid.NewGuid(), task.WorkspaceId, null, null, null, [], []),
                DateTimeOffset.UtcNow)));
        }, () => administration);

        // FAULT is what no scenario expects unless it says so: a command the computer refused, a grant the gateway
        // refused for good, a delivery that stopped, an invitation answer refused. Said as NOTICE, nobody read it.
        runner.Noticed += notice => Say("FAULT", $"refused {notice.Detail}");
        loop.Noticed += notice => Say("FAULT", notice.Kind == "GrantDropped"
            ? $"grant-dropped {notice.Detail}"
            : $"{notice.Kind} {notice.Detail}");
        administration.Noticed += notice => Say(notice.Refused ? "FAULT" : "NOTICE",
            notice.Refused ? $"admission-refused {notice.Detail}" : notice.Detail);

        var runs = new List<Task>();
        var nextSync = DateTimeOffset.MinValue;
        var paired = false;
        uint epoch = 0;

        while (!ct.IsCancellationRequested && !loop.Stopped)
        {
            if (DateTimeOffset.UtcNow >= nextSync)
            {
                nextSync = DateTimeOffset.UtcNow + SyncEvery;

                // Sealed again on every sync, as the desktop does: after a rotation the name has to be sealed
                // under the new key, or a device given only that key could not read it.
                IReadOnlyList<WorkspaceRef> workspaces = [new(WorkspaceId, sealer.WorkspaceName(WorkspaceId, workspaceName))];

                foreach (var command in await loop.TurnAsync(workspaces, ct))
                {
                    // On their own tasks, as the desktop runs them: a run waiting for an answer must not hold
                    // up the delivery of the answer.
                    runs.Add(Task.Run(() => runner.ApplyAsync(command, ct), ct));
                }

                await administration.SettleOwedRevocationsAsync(ct);

                if (keys.HasPendingInvites)
                    await administration.AnswerEnrollmentsAsync(ct);

                if (!paired)
                {
                    paired = true;
                    Say("PAIRED", keys.HostId);
                }
            }
            else
            {
                await loop.FlushAsync(ct);
            }

            if (keys.Current.Epoch != epoch)
            {
                epoch = keys.Current.Epoch;
                Say("EPOCH", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // A command whose task threw is said, not dropped with the finished ones: otherwise a runner that failed
            // showed only as a test waiting thirty seconds for something that was never going to happen.
            foreach (var faulted in runs.Where(run => run.IsFaulted))
                Say("FAULT", $"run {faulted.Exception?.GetBaseException().Message}");
            runs.RemoveAll(run => run.IsCompleted);
            await Task.Delay(FlushEvery, ct);
        }

        if (loop.Stopped)
            throw new InvalidOperationException(
                "The gateway refused this computer: " + loop.Notices.LastOrDefault(n => n.Kind == "Stopped")?.Detail);
    }

    /// <summary>One line the test can wait on. Never content: the prompt and the secrets stay in this process.</summary>
    internal static void Say(string what, string detail)
    {
        lock (Console.Out)
        {
            Console.Out.WriteLine($"{what} {detail}");
            Console.Out.Flush();
        }
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>
    /// The key store's grant outbox, saying <c>GRANTED &lt;epoch&gt; &lt;deviceId&gt;</c> the first time the delivery loop
    /// reads each grant - which it does before sending any, so every grant the computer made is said.
    /// </summary>
    private sealed class SaidGrants(HostKeyStore keys) : IGrantOutbox
    {
        private readonly HashSet<string> _said = [];

        public IReadOnlyList<PendingGrant> PendingGrants()
        {
            var pending = keys.PendingGrants();
            foreach (var grant in pending)
            {
                if (_said.Add(grant.Id))
                    Say("GRANTED", $"{grant.Grant.Epoch} {grant.Grant.DeviceId}");
            }
            return pending;
        }

        public void DiscardGrant(string id) => keys.DiscardGrant(id);
    }

    /// <summary>The person at the desk, who is not there: every permission is the panel's to answer.</summary>
    private sealed class NobodyAtTheDesk : IDecisionHandler
    {
        public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new DecisionOutcome("deny");
        }
    }
}

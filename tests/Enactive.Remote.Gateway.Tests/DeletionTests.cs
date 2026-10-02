namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// A person deleting their own account (<c>DELETE /api/account</c>): every row the account owns goes, in one
/// transaction, and nothing of anybody else's does.
///
/// <para>What is asserted about the rows is read from the schema itself - every table with an owner_id or a
/// user_id column - so a table added later is covered without anyone remembering to add it here: it fails
/// the "seeded" check until this test gives it a row, and then the "gone" check if its foreign key does not
/// cascade. The admission and the operator's decision about it have no owner column; they name the
/// identity, and are asked for by it.</para>
/// </summary>
public sealed class DeletionTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private WebApplicationFactory<Program> _gateway = null!;

    private Database Db => new(database.ConnectionString);

    private static string Name(string stem) => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _gateway.DisposeAsync().AsTask();

    // ── what goes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every table that names an owner holds a row of Alice's before, and none after - the security log
    /// included. Her account row goes, and so do the admission of the identity she signed in with and the
    /// operator's decision about it, which name the identity and not the account.
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_leaves_no_row_with_its_owner_id()
    {
        var name = Name("alice");
        using var alice = await PanelClient.SignedInAsync(_gateway, name);
        await SeedEverythingAsync(alice, name);

        var before = await OwnedRowsAsync(alice.UserId);
        Assert.All(before, table => Assert.True(table.Value > 0, $"{table.Key} holds nothing of Alice's to delete."));

        using var deleted = await alice.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var after = await OwnedRowsAsync(alice.UserId);
        Assert.All(after, table => Assert.True(table.Value == 0, $"{table.Key} still holds {table.Value} row(s) of Alice's."));
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM users WHERE id = '{alice.UserId}'"));
    }

    /// <summary>
    /// The admission has no foreign key to the account, so the cascade does not reach it: the identity's
    /// admission, and the operator's row recording the decision (which names the identity and belongs to
    /// nobody), are deleted by the deletion itself. Left, the operator's records would still say this person
    /// was here after the person was told everything of theirs was gone.
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_leaves_no_admission_or_operator_row_naming_its_identities()
    {
        var name = Name("alice");
        using var alice = await PanelClient.SignedInAsync(_gateway, name);
        await ApproveAsync(name);

        Assert.Equal(1, await AdmissionRowsAsync(name));
        Assert.Equal(1, await OperatorRowsAsync(name));

        using var deleted = await alice.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.Equal(0, await AdmissionRowsAsync(name));
        Assert.Equal(0, await OperatorRowsAsync(name));
    }

    /// <summary>
    /// The operator's rows are found by the identity exactly. Subjects are compared byte for byte everywhere
    /// else (admissions, external_identities), and a target compared without regard to case made deleting
    /// dev:bob-x also delete the operator's record about dev:Bob-x, a different identity of somebody else's.
    /// </summary>
    [Fact]
    public async Task Operator_rows_are_matched_exactly_not_by_case()
    {
        var stem = Guid.NewGuid().ToString("N")[..8];
        var upper = "Bob-" + stem;
        var lower = "bob-" + stem;
        using var upperBob = await PanelClient.SignedInAsync(_gateway, upper);
        using var lowerBob = await PanelClient.SignedInAsync(_gateway, lower);
        Assert.NotEqual(upperBob.UserId, lowerBob.UserId);
        await ApproveAsync(upper);
        await ApproveAsync(lower);

        using var deleted = await lowerBob.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.Equal(0, await OperatorRowsAsync(lower));
        Assert.Equal(1, await OperatorRowsAsync(upper));
        Assert.Equal(1, await AdmissionRowsAsync(upper));
    }

    /// <summary>
    /// Bob, with a row in every table too, keeps every one of them, his admission and the operator's
    /// decision about it included.
    /// </summary>
    [Fact]
    public async Task Another_persons_rows_survive_a_deletion()
    {
        var aliceName = Name("alice");
        var bobName = Name("bob");
        using var alice = await PanelClient.SignedInAsync(_gateway, aliceName);
        using var bob = await PanelClient.SignedInAsync(_gateway, bobName);
        await SeedEverythingAsync(alice, aliceName);
        await SeedEverythingAsync(bob, bobName);

        var before = await OwnedRowsAsync(bob.UserId);

        using var deleted = await alice.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.Equal(before, await OwnedRowsAsync(bob.UserId));
        Assert.Equal(1, await AdmissionRowsAsync(bobName));
        Assert.Equal(1, await OperatorRowsAsync(bobName));

        using var state = await bob.SendAsync(HttpMethod.Get, "/api/state", csrf: false);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    // ── who may ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A session opened more than ten minutes ago is refused with its own code, and the account stays. A
    /// cookie left in a borrowed browser, or taken from one, must not be enough to end everything; signing in
    /// again is what proves the person is at the keyboard now. The boundary is the session's opening, not its
    /// last use: a session kept busy is no fresher a proof.
    /// </summary>
    [Fact]
    public async Task An_old_session_must_sign_in_again_to_delete()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        // Ten seconds either side of the edge: close enough that a window of nine or eleven minutes fails one
        // half, and far enough that the time between the update and the request cannot cross it.
        await OpenedAgoAsync(alice.UserId, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(10));

        using var refused = await alice.SendAsync(HttpMethod.Delete, "/api/account");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = await ErrorOfAsync(refused);
        Assert.Equal("reauthenticate", error.Code);
        Assert.Equal("Sign in again to delete the account.", error.Error);
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM users WHERE id = '{alice.UserId}'"));

        // Inside the ten minutes the same session may.
        await OpenedAgoAsync(alice.UserId, TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(10));
        using var deleted = await alice.SendAsync(HttpMethod.Delete, "/api/account");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM users WHERE id = '{alice.UserId}'"));
    }

    /// <summary>
    /// A state-changing call like every other: another site's request carries the cookie and not the token,
    /// and a browser with no session is nobody whose account could be deleted.
    /// </summary>
    [Fact]
    public async Task Deleting_requires_the_csrf_token_and_a_session()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        using var forged = await alice.SendAsync(HttpMethod.Delete, "/api/account", csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("csrf", (await ErrorOfAsync(forged)).Code);

        using var stranger = new PanelClient(_gateway);
        await stranger.SessionAsync();
        using var anonymous = await stranger.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM users WHERE id = '{alice.UserId}'"));

        // And the browser that deleted is signed out: its cookie names a session that no longer exists.
        using var deleted = await alice.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False((await alice.SessionAsync()).Authenticated);
    }

    // ── the computers ───────────────────────────────────────────────────────

    /// <summary>
    /// The account's computer is cut off at once and cannot come back. The next call on its open connection
    /// fails (that the deletion closes it is asserted at the service, below); connecting again with its token
    /// is refused before any hub method runs, as for a revoked computer; and a call that raced the close and
    /// reached the service is answered with the unknown-computer code, which a Host treats as final, in words
    /// saying what happened.
    /// </summary>
    [Fact]
    public async Task A_deleted_accounts_computer_is_refused()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var computer = await alice.PostAsync<RegisteredHost>("/api/hosts", new { name = "Studio PC" });

        await using var connected = Connect(computer.Token);
        await connected.StartAsync();
        await connected.SyncAsync([], CancellationToken.None);

        using var deleted = await alice.SendAsync(HttpMethod.Delete, "/api/account");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // The connection is no longer open, so the call is never sent: the client refuses it itself. A coded
        // refusal here would be a connection still being served (that the deletion closes it is asserted at the
        // service, below - over long polling each poll is refused at authentication on its own).
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.SyncAsync([], CancellationToken.None));

        // What the desktop then does is dial again, and what it shows is this refusal's sentence: it has to
        // name a deleted account among its causes, or the person is told their credential was revoked.
        await using var reconnecting = Connect(computer.Token);
        var refusedAgain = await Assert.ThrowsAsync<GatewayCredentialRefusedException>(() => reconnecting.StartAsync());
        Assert.Equal(
            "The service refused this computer's credential - it was revoked, the account is disabled, or the "
            + "account was deleted. Make a new connection code in the browser and connect this computer with it.",
            refusedAgain.Message);

        var refused = await Assert.ThrowsAsync<GatewayFault>(() =>
            new HostService(Db).SyncAsync(new HostAccess(computer.Id, alice.UserId), []));
        Assert.Equal(FaultCode.UnknownHost, refused.Code);
        Assert.Equal(FaultDisposition.Fatal, refused.ToContract().Disposition);
        Assert.Equal(
            "This computer is not registered on the service any more - its account or its registration was removed.",
            refused.Message);
    }

    /// <summary>
    /// The deletion closes the connections of every computer of the account, and of no other, and tells the
    /// operator's log that an account was deleted - in words that name nobody. Asked of the service, because
    /// over HTTP the test client polls, and each poll is authenticated afresh and refused anyway: a WebSocket,
    /// which the desktop uses, is authenticated once, and without the close it would go on being served.
    /// </summary>
    [Fact]
    public async Task Deleting_closes_the_accounts_computers_and_says_so_without_naming_anyone()
    {
        var alice = await TestAccounts.CreateAsync(database, Name("alice"));
        var bob = await TestAccounts.CreateAsync(database, Name("bob"));
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var (studio, _, _) = await users.RegisterHostAsync(alice, "Studio PC", default);
        var (laptop, _, _) = await users.RegisterHostAsync(alice, "Laptop", default);
        var (bobs, _, _) = await users.RegisterHostAsync(bob, "Bob's PC", default);

        var closed = new List<string>();
        var connections = new HostConnections();
        foreach (var host in new[] { studio, laptop, bobs })
        {
            connections.Add("connection-" + host, host, () => closed.Add(host));
        }

        var log = new RecordingLog();
        await new AccountDeletion(Db, new SessionStore(Db, TimeProvider.System), connections, log)
            .DeleteAsync(alice, default);

        Assert.Equal(new[] { laptop, studio }.Order(StringComparer.Ordinal), closed.Order(StringComparer.Ordinal));
        var line = Assert.Single(log.Lines);
        Assert.Equal((LogLevel.Information, "An account was deleted."), line);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>The lines a service wrote to the operator's log, as they read.</summary>
    private sealed class RecordingLog : ILogger<AccountDeletion>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// A row of the person's in every table that has an owner: what signing in made (the account, its
    /// identity, session, event line, retention row and security log), a computer registered through the
    /// API, and the rest written straight into the tables. Written directly because what is under test is
    /// that the deletion and the foreign keys remove them, not how each is made - the other suites own that.
    /// </summary>
    private async Task SeedEverythingAsync(PanelClient person, string name)
    {
        var owner = person.UserId;
        var host = (await person.PostAsync<RegisteredHost>("/api/hosts", new { name = "Studio PC" })).Id;
        var device = Ids.New();
        var invite = Ids.New();
        var task = Guid.NewGuid().ToString();
        var run = Ids.New();
        var now = DateTime.UtcNow;

        await ApproveAsync(name);

        await database.ExecuteAsync(
            $"""
            INSERT INTO devices (id, owner_id, public_key, label, created_at)
            VALUES ('{device}', '{owner}', X'04', 'Laptop', @now);
            INSERT INTO host_workspaces (owner_id, host_id, workspace_id, sealed_name)
            VALUES ('{owner}', '{host}', 'workspace-1', 'sealed');
            INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
            VALUES ('{owner}', '{host}', '{device}', 1, @json, @now);
            INSERT INTO invites (id, owner_id, created_by_device, created_at, expires_at)
            VALUES ('{invite}', '{owner}', '{device}', @now, @now);
            INSERT INTO enrollments (invite_id, owner_id, device_id, mac, created_at)
            VALUES ('{invite}', '{owner}', '{device}', 'mac', @now);
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
            VALUES ('{owner}', '{task}', '{host}', 'workspace-1', 'sealed', REPEAT('0', 64), @now);
            INSERT INTO runs (id, owner_id, task_id, host_id, status, created_at)
            VALUES ('{run}', '{owner}', '{task}', '{host}', 'Running', @now);
            INSERT INTO commands (owner_id, id, host_id, run_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES ('{owner}', '{Ids.New()}', '{host}', '{run}', 'CancelRun', @json, REPEAT('0', 64), 'PendingDelivery', @now, @now);
            INSERT INTO approvals (owner_id, host_id, id, run_id, tool_call_id, action_hash, remote_decidable,
                                   sealed_action, status, created_at, expires_at)
            VALUES ('{owner}', '{host}', '{Ids.New()}', '{run}', 'call-1', REPEAT('0', 64), 1, 'sealed', 'Pending', @now, @now);
            INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, at, ordinal)
            VALUES ('{owner}', '{host}', '{Ids.New()}', '{run}', 1, 'Running', @now, 1);
            INSERT INTO notices (id, owner_id, run_id, kind, at, ordinal)
            VALUES ('{Ids.New()}', '{owner}', '{run}', 'run-finished', @now, 1);
            """,
            ("@now", now), ("@json", "{}"));
    }

    /// <summary>The operator approving the identity the development sign-in made, through the command line.</summary>
    private async Task ApproveAsync(string name)
        => Assert.Equal(0, await AdminCommands.RunAsync(["approve", $"{DevelopmentSignIn.Provider}:{name}"], Db, TextWriter.Null));

    /// <summary>How many rows of the account each table with an owner_id or a user_id column holds, by table.</summary>
    private async Task<SortedDictionary<string, long>> OwnedRowsAsync(string userId)
    {
        var columns = await database.StringsAsync(
            """
            SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME IN ('owner_id', 'user_id')
            ORDER BY TABLE_NAME
            """);

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);

        foreach (var column in columns)
        {
            var (table, name) = (column.Split('.')[0], column.Split('.')[1]);
            counts[column] = await database.ScalarLongAsync($"SELECT COUNT(*) FROM `{table}` WHERE `{name}` = '{userId}'");
        }

        // A schema read wrongly would make every assertion above pass on nothing.
        Assert.True(counts.Count >= 17, $"Only {counts.Count} owned tables were found.");
        return counts;
    }

    private Task<long> AdmissionRowsAsync(string name)
        => database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM admissions WHERE provider = '{DevelopmentSignIn.Provider}' AND subject = '{name}'");

    private Task<long> OperatorRowsAsync(string name)
        => database.ScalarLongAsync(
            // Compared as bytes here whatever the column's collation, so this counts one identity's rows only.
            $"SELECT COUNT(*) FROM audit WHERE owner_id IS NULL AND CAST(target AS BINARY) = CAST('{DevelopmentSignIn.Provider}:{name}' AS BINARY)");

    /// <summary>Moves the opening of the person's only session back by <paramref name="ago"/>.</summary>
    private Task OpenedAgoAsync(string userId, TimeSpan ago)
        => database.ExecuteAsync(
            "UPDATE user_sessions SET created_at = @at WHERE user_id = @user",
            ("@at", DateTime.UtcNow - ago), ("@user", userId));

    /// <summary>A Host client pointed at the in-process gateway.</summary>
    private SignalRGatewayConnection Connect(string token)
        => new(new Uri(_gateway.Server.BaseAddress, "hubs/host"), token, options =>
        {
            options.HttpMessageHandlerFactory = _ => _gateway.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
        });

    private static async Task<ErrorView> ErrorOfAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!;

    /// <summary>What every refusal on the API answers with. Both fields, because the wire refuses unknown ones.</summary>
    private sealed record ErrorView(string Code, string Error);

    private sealed record RegisteredHost(string Id, string Name, string Token);
}

namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;
using Xunit;

/// <summary>
/// Alice and Bob, over HTTP. Each has a computer that has published a workspace, a task, a started
/// run with events, a pending permission request and a notice; Bob is asked for everything of
/// Alice's, and everything has to look to him exactly as if it did not exist.
///
/// <para><b>Why this suite is separate from the service tests.</b> The services each have tests for
/// their own ownership filters, and each of those passes while a route hands the service the wrong
/// account, or answers a foreign id with a different sentence than a missing one. This calls the
/// real pipeline - cookie, antiforgery, route, service, database - as the other account, which is
/// the only place "a foreign id answers exactly like a missing one" can be seen whole.</para>
///
/// <para><b>Three things are asserted for every private path.</b> The status is 404, the body is
/// the body a made-up id of the same kind gets (a different sentence would answer "does somebody
/// else have one by that id?"), and nothing of Alice's has changed in the database (a 404 that still
/// revoked her computer is no refusal).</para>
///
/// <para>Devices, invites, enrollments and grants have no endpoints yet. Task 5.4 adds them to
/// this suite as part of its completion; nothing here stands in for them.</para>
/// </summary>
public sealed class IsolationTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string WorkspaceId = "workspace-1";

    private WebApplicationFactory<Program> _gateway = null!;
    private World _alice = null!;
    private World _bob = null!;

    private Database Db => new(database.ConnectionString);

    private HostService Host => new(Db);

    public async Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);

        // Both exist before any test asks anything, so a test about Bob has Alice's rows to leak.
        _alice = await BuildWorldAsync("alice");
        _bob = await BuildWorldAsync("bob");
    }

    public Task DisposeAsync()
    {
        _alice.Panel.Dispose();
        _bob.Panel.Dispose();
        return _gateway.DisposeAsync().AsTask();
    }

    // ── every private path, asked by the wrong person ───────────────────────

    /// <summary>
    /// What each private endpoint is called with, given the ids it is about. The same builder makes
    /// the request for Alice's ids and for a made-up set, so the two differ in nothing but the ids.
    /// </summary>
    /// <param name="Request">The request, and the caller's own computer for the paths that name one.</param>
    /// <param name="OwnStatus">
    /// What Alice herself is answered, to show the request is well-formed and the 404 is about
    /// ownership. A start is a 409: her task already has its run. Naming Bob's computer with her own
    /// approval is a 404 for her too, as the approval is not on that computer; the case is about Bob.
    /// </param>
    private sealed record PrivatePath(
        Func<TargetIds, string, (string Path, object Body)> Request, HttpStatusCode OwnStatus);

    private static readonly Dictionary<string, PrivatePath> Paths = new()
    {
        ["revoke a computer"] = new((ids, _) => ($"/api/hosts/{ids.HostId}/revoke", new { }), HttpStatusCode.OK),

        ["create a task on a computer"] = new((ids, _) =>
        {
            var taskId = Guid.NewGuid().ToString();
            return ("/api/tasks", new
            {
                taskId,
                hostId = ids.HostId,
                workspaceId = ids.WorkspaceId,
                sealedTask = new TestBrowser(ids.HostId).Task(taskId, ids.WorkspaceId, "Title", "Prompt")
            });
        }, HttpStatusCode.OK),

        ["start a task"] = new((ids, _) =>
        {
            var commandId = Guid.NewGuid().ToString();
            return ($"/api/tasks/{ids.TaskId}/start", new
            {
                commandId,
                @sealed = new TestBrowser(ids.HostId).Start(commandId, ids.TaskId, ids.WorkspaceId)
            });
        }, HttpStatusCode.Conflict),

        ["cancel a run"] = new((ids, _) =>
        {
            var commandId = Guid.NewGuid().ToString();
            return ($"/api/runs/{ids.RunId}/cancel", new
            {
                commandId,
                @sealed = new TestBrowser(ids.HostId).Cancel(commandId, ids.RunId)
            });
        }, HttpStatusCode.OK),

        ["decide an approval"] = new((ids, _) => Decision(ids, ids.HostId), HttpStatusCode.OK),

        // The approval is Alice's and the computer named is Bob's own: the lookup has to be by owner,
        // and not only by the computer the caller names.
        ["decide an approval on one's own computer"] =
            new((ids, ownHostId) => Decision(ids, ownHostId), HttpStatusCode.NotFound),

        ["send a device command: revoke"] = new((ids, _) => DeviceCommand(ids, CommandKind.RevokeDevice), HttpStatusCode.OK),

        ["send a device command: endorse"] = new((ids, _) => DeviceCommand(ids, CommandKind.EndorseDevice), HttpStatusCode.OK),
    };

    public static TheoryData<string> PathNames => new(Paths.Keys);

    private static (string Path, object Body) Decision(TargetIds ids, string hostId)
    {
        var commandId = Guid.NewGuid().ToString();
        return ($"/api/approvals/{ids.ApprovalId}/resolve", new
        {
            commandId,
            hostId,
            decision = RemoteDecision.Allow,
            actionHash = ids.ActionHash,
            @sealed = new TestBrowser(hostId).Decision(commandId, ids.ApprovalId, ids.ActionHash, RemoteDecision.Allow)
        });
    }

    private static (string Path, object Body) DeviceCommand(TargetIds ids, CommandKind kind)
    {
        var commandId = Guid.NewGuid().ToString();
        return ($"/api/hosts/{ids.HostId}/device-commands", new
        {
            commandId,
            kind,
            // The gateway checks the shape of a seal and never opens one.
            @sealed = ShapeOnly("a device")
        });
    }

    [Theory]
    [MemberData(nameof(PathNames))]
    public async Task Bob_is_refused_every_path_to_alices_ids_as_if_they_did_not_exist(string name)
    {
        var path = Paths[name];
        var aliceBefore = await ChecksumAsync(_alice.UserId);

        var (madeUpPath, madeUpBody) = path.Request(TargetIds.MadeUp(), _bob.HostId);
        var (alicesPath, alicesBody) = path.Request(_alice.Ids, _bob.HostId);

        var madeUp = await AnswerAsync(_bob, madeUpPath, madeUpBody);
        var alices = await AnswerAsync(_bob, alicesPath, alicesBody);

        // The made-up id first, and its body named: two refusals can be equal because both are the
        // same wrong request - a typo in the route, a body the binder refused - and equal bodies
        // would then prove nothing about ownership.
        Assert.Equal(HttpStatusCode.NotFound, madeUp.Status);
        Assert.Contains("\"code\":\"not-found\"", madeUp.Body);

        Assert.Equal(HttpStatusCode.NotFound, alices.Status);
        Assert.Equal(madeUp.Body, alices.Body);

        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));

        // And Alice is not refused the same request, which is what makes the 404 above about whose
        // it is. Last, because it changes her.
        var own = await AnswerAsync(_alice, alicesPath, alicesBody);
        Assert.Equal(path.OwnStatus, own.Status);
    }

    // ── snapshot, notices, cursor ───────────────────────────────────────────

    /// <summary>
    /// Nothing of Bob's is in Alice's snapshot: not a computer, task, run, request, event or notice,
    /// not his unread count, not his cursor, not his retention marker. Bob's world is bigger than
    /// Alice's and has had history trimmed, so each of those differs between them and a leak of any
    /// one shows.
    /// </summary>
    [Fact]
    public async Task Alices_snapshot_contains_nothing_of_bobs()
    {
        // More for Bob than for Alice: a cursor, a count or a list that came from the wrong person
        // must not be able to equal hers by coincidence.
        // Two requests waiting on him, so his unread count is two to her one and a leaked list of
        // pending requests is not hidden by his having been answered.
        await PublishAsync(_bob, RemoteEventKind.Progress, "Reading");
        await PublishAsync(_bob, RemoteEventKind.Progress, "Writing");
        await PublishAsync(_bob, RemoteEventKind.ApprovalRequested, "Permission needed", Guid.NewGuid().ToString());

        // His first event is old enough to be trimmed, through the real pass, which leaves the
        // marker behind that only his panel is to be told about.
        await database.ExecuteAsync(
            "UPDATE events SET at = UTC_TIMESTAMP(3) - INTERVAL 40 DAY WHERE owner_id = @owner AND sequence = 1",
            ("@owner", _bob.UserId));
        Assert.True(await new Retention(Db, days: 30).TrimAsync() > 0);

        var alicesText = await _alice.Panel.Http.GetStringAsync("/api/state");
        var alices = RemoteJson.Deserialize<GatewaySnapshot>(alicesText);
        var bobs = await _bob.Panel.GetAsync<GatewaySnapshot>("/api/state");

        // Bob really has all of it, so the emptiness below is not an empty fixture.
        Assert.Equal([_bob.Ids.HostId], bobs.Hosts.Select(h => h.Id));
        Assert.Equal([_bob.Ids.TaskId], bobs.Tasks.Select(t => t.Id));
        Assert.Equal([_bob.Ids.RunId], bobs.Runs.Select(r => r.Id));
        Assert.Equal(2, bobs.Approvals.Count);
        Assert.NotEmpty(bobs.Events);
        Assert.Equal(2, bobs.Notices.Count);
        Assert.NotNull(bobs.Retention.TrimmedBefore);

        // Every collection is Alice's own and only hers.
        Assert.Equal([_alice.Ids.HostId], alices.Hosts.Select(h => h.Id));
        Assert.Equal([WorkspaceId], Assert.Single(alices.Hosts).Workspaces.Select(w => w.Id));
        Assert.Equal([_alice.Ids.TaskId], alices.Tasks.Select(t => t.Id));
        Assert.Equal([_alice.Ids.RunId], alices.Runs.Select(r => r.Id));
        Assert.Equal([_alice.Ids.ApprovalId], alices.Approvals.Select(a => a.Id));
        Assert.Equal(3, alices.Events.Count);
        Assert.All(alices.Events, e => Assert.Equal(_alice.Ids.RunId, e.RunId));
        Assert.All(alices.Events, e => Assert.Equal(_alice.Ids.HostId, e.HostId));
        Assert.All(alices.Notices, n => Assert.Equal(_alice.Ids.RunId, n.RunId));

        // The count is hers: one notice, unread. Bob has two.
        Assert.False(Assert.Single(alices.Notices).Read);
        Assert.Equal(1, alices.UnreadNotices);
        Assert.Equal(2, bobs.UnreadNotices);

        // The cursor is where Alice's own line stands - the line of a person with three events and a
        // notice - and not where Bob's does.
        Assert.Equal(await LineAsync(_alice.UserId), alices.Cursor);
        Assert.NotEqual(bobs.Cursor, alices.Cursor);

        // Alice has lost nothing, so she is told nothing was trimmed; Bob has.
        Assert.Null(alices.Retention.TrimmedBefore);

        // And not a trace of Bob in the text itself: an id, a key, a sealed field, his account.
        foreach (var ofBob in new[]
        {
            _bob.UserId, _bob.Ids.HostId, _bob.Ids.TaskId, _bob.Ids.RunId, _bob.Ids.ApprovalId,
            _bob.SealedTask, _bob.SealedWorkspaceName
        })
        {
            Assert.DoesNotContain(ofBob, alicesText);
        }
    }

    [Fact]
    public async Task Bob_marking_notices_read_leaves_alices_unread()
    {
        var bobs = await _bob.Panel.GetAsync<GatewaySnapshot>("/api/state");
        Assert.True(bobs.UnreadNotices > 0);

        await _bob.Panel.PostAsync("/api/notices/read", new { through = bobs.Cursor });

        var bobsAfter = await _bob.Panel.GetAsync<GatewaySnapshot>("/api/state");
        Assert.Equal(0, bobsAfter.UnreadNotices);

        var alices = await _alice.Panel.GetAsync<GatewaySnapshot>("/api/state");
        Assert.Equal(1, alices.UnreadNotices);
        Assert.False(Assert.Single(alices.Notices).Read);
        Assert.Equal(1L, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM notices WHERE owner_id = '{_alice.UserId}' AND is_read = 0"));
    }

    [Fact]
    public async Task Bobs_events_leave_alices_cursor_and_unread_count_alone()
    {
        var before = await _alice.Panel.GetAsync<GatewaySnapshot>("/api/state");
        var lineBefore = await LineAsync(_alice.UserId);

        // Everything that takes one of Bob's ordinals and raises a notice of his: more progress, and
        // the run's end.
        await PublishAsync(_bob, RemoteEventKind.Progress, "Reading");
        await PublishAsync(_bob, RemoteEventKind.Completed, "Done");

        var bobs = await _bob.Panel.GetAsync<GatewaySnapshot>("/api/state");
        Assert.NotEqual(before.Cursor, bobs.Cursor);

        // The poll Alice's panel makes next, from the cursor it was given.
        var after = await _alice.Panel.GetAsync<GatewaySnapshot>($"/api/state?since={before.Cursor}");

        Assert.True(after.Delta);
        Assert.Empty(after.Events);
        Assert.Empty(after.Notices);
        Assert.Equal(before.Cursor, after.Cursor);
        Assert.Equal(before.UnreadNotices, after.UnreadNotices);
        Assert.Equal(lineBefore, await LineAsync(_alice.UserId));
    }

    /// <summary>
    /// The checksum covers every table that names an owner, found in the database rather than listed
    /// here. This is what makes that true: a table added without an <c>owner_id</c> or <c>user_id</c>
    /// would be invisible to the checksum, and its rows could change under a refused request unseen.
    /// </summary>
    [Fact]
    public async Task Every_table_names_its_owner_or_is_known_to_belong_to_nobody()
    {
        var owned = (await OwnedTablesAsync()).Select(t => t.Table).ToHashSet();
        var all = await database.StringsAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()");

        // Nobody's: the allow-list of who may sign up, and the migrator's own bookkeeping.
        var ownedByNobody = new HashSet<string> { "admissions", "schema_version" };

        Assert.DoesNotContain(all, t => !owned.Contains(t) && !ownedByNobody.Contains(t));
        Assert.Contains("tasks", owned);
        Assert.Contains("notices", owned);
    }

    // ── a world ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A person with a computer, a workspace, a task, a started run, three events, a pending permission
    /// request and a notice - made through the owner API and the computer's own service, the paths real
    /// ones are made through, and not by writing rows.
    /// </summary>
    private async Task<World> BuildWorldAsync(string name)
    {
        var panel = await PanelClient.SignedInAsync(_gateway, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        var computer = await panel.PostAsync<NewComputer>("/api/hosts", new { name = "Studio PC" });

        var browser = new TestBrowser(computer.Id);
        var sealer = browser.Computer.Sealer();
        var host = new HostAccess(computer.Id, panel.UserId);
        var sealedWorkspaceName = sealer.WorkspaceName(WorkspaceId, "Enactive");

        // The computer connects and publishes its workspace; until it has, a task cannot name one.
        await Host.SyncAsync(host, [new WorkspaceRef(WorkspaceId, sealedWorkspaceName)]);

        var taskId = Guid.NewGuid().ToString();
        var sealedTask = browser.Task(taskId, WorkspaceId, "Run the tests", "Please run them.");

        await panel.PostAsync("/api/tasks", new { taskId, hostId = computer.Id, workspaceId = WorkspaceId, sealedTask });

        var commandId = Guid.NewGuid().ToString();
        var start = await panel.PostAsync<HostCommand>($"/api/tasks/{taskId}/start", new
        {
            commandId,
            @sealed = browser.Start(commandId, taskId, WorkspaceId)
        });

        var runId = RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;
        await Host.AcknowledgeAsync(host, commandId);

        var approvalId = Guid.NewGuid().ToString();
        var ids = new TargetIds(computer.Id, WorkspaceId, taskId, runId, approvalId, Ids.Hash(approvalId));
        var world = new World(panel, ids, host, sealer, sealedTask, sealedWorkspaceName);

        await PublishAsync(world, RemoteEventKind.Running, "Started");
        await PublishAsync(world, RemoteEventKind.Progress, "Working");
        await PublishAsync(world, RemoteEventKind.ApprovalRequested, "Permission needed");

        return world;
    }

    /// <summary>
    /// The next event of a world's run, as its computer reports it. An approval request carries a
    /// permission request, sealed under the ids beside it.
    /// </summary>
    private async Task PublishAsync(World world, RemoteEventKind kind, string text, string? approvalId = null)
    {
        var sequence = ++world.Sequence;
        var ids = world.Ids;

        // The world's own request unless another is named; its hash is made the way the world's is.
        var id = approvalId ?? ids.ApprovalId;
        var hash = Ids.Hash(id);

        var approval = kind == RemoteEventKind.ApprovalRequested
            ? new ApprovalRequest(
                id, "call-1", hash, RemoteDecidable: true,
                world.Sealer.Action(ids.RunId, id, "call-1", hash, true,
                    new SealedAction("write_file", "{}", "Write notes.txt", "/work", "Notes")))
            : null;

        await Host.PublishAsync(world.Computer, new HostEvent(
            Guid.NewGuid().ToString("N"), ids.RunId, sequence, kind,
            world.Sealer.Detail(ids.RunId, sequence, kind, text), approval));
    }

    // ── looking ─────────────────────────────────────────────────────────────

    private sealed record Answer(HttpStatusCode Status, string Body);

    private static async Task<Answer> AnswerAsync(World caller, string path, object body)
    {
        using var response = await caller.Panel.SendAsync(HttpMethod.Post, path, body);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The cursor a person's own line stands at, as their panel is handed it.</summary>
    private async Task<string> LineAsync(string userId)
        => (await database.StringsAsync(
            $"SELECT CONCAT(epoch, '.', value) FROM user_streams WHERE owner_id = '{userId}'")).Single();

    /// <summary>
    /// One hash per table of everything the database holds under this person: every column of every
    /// row, in a fixed order. Per table, so a failure names the table that changed and not only that
    /// something did.
    /// </summary>
    private async Task<SortedDictionary<string, string>> ChecksumAsync(string userId)
    {
        var sums = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await database.OpenAsync();

        foreach (var (table, column) in await OwnedTablesAsync())
        {
            await using var command = new MySqlCommand($"SELECT * FROM `{table}` WHERE `{column}` = @owner", connection);
            command.Parameters.AddWithValue("@owner", userId);
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<string>();

            while (await reader.ReadAsync())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i =>
                    reader.GetName(i) + "=" + (reader.IsDBNull(i) ? "NULL" : Text(reader.GetValue(i))))));
            }

            // Sorted: a table has no order of its own, and a checksum that depended on one would
            // fail on a re-read that changed nothing.
            rows.Sort(StringComparer.Ordinal);
            sums[table] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
        }

        return sums;
    }

    private static string Text(object value)
        => value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

    /// <summary>
    /// Every table that holds somebody's rows, and the column that says whose: found from the schema,
    /// so a table a later migration adds is covered without anyone remembering to list it. The
    /// accounts table is keyed by the person's id itself.
    /// </summary>
    private async Task<List<(string Table, string Column)>> OwnedTablesAsync()
    {
        var found = await database.StringsAsync(
            """
            SELECT CONCAT(table_name, '.', column_name) FROM information_schema.columns
            WHERE table_schema = DATABASE() AND column_name IN ('owner_id', 'user_id')
            ORDER BY table_name
            """);

        var tables = found.Select(f => (Table: f.Split('.')[0], Column: f.Split('.')[1])).ToList();
        tables.Add(("users", "id"));
        return tables;
    }

    /// <summary>A field in the shape of a seal, from a key nobody holds.</summary>
    private static string ShapeOnly(string text)
        => Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes(text), []);

    private sealed record NewComputer(string Id, string Name, string Token);

    /// <summary>The ids of one person's world that another person's requests can name.</summary>
    internal sealed record TargetIds(
        string HostId, string WorkspaceId, string TaskId, string RunId, string ApprovalId, string ActionHash)
    {
        /// <summary>Ids of the same kinds that belong to nobody, in the forms the gateway itself makes.</summary>
        public static TargetIds MadeUp() => new(
            Ids.New(), "workspace-" + Guid.NewGuid().ToString("N")[..8],
            Guid.NewGuid().ToString(), Ids.New(), Guid.NewGuid().ToString(),
            Ids.Hash(Guid.NewGuid().ToString()));
    }

    private sealed class World(
        PanelClient panel, TargetIds ids, HostAccess computer, Sealer sealer, string sealedTask, string sealedWorkspaceName)
    {
        public PanelClient Panel { get; } = panel;

        public TargetIds Ids { get; } = ids;

        public HostAccess Computer { get; } = computer;

        public Sealer Sealer { get; } = sealer;

        public string SealedTask { get; } = sealedTask;

        public string SealedWorkspaceName { get; } = sealedWorkspaceName;

        public string UserId => Panel.UserId;

        public string HostId => Ids.HostId;

        /// <summary>The last sequence the computer reported for the run.</summary>
        public long Sequence { get; set; }
    }
}

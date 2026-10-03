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
/// <para><b>Devices, grants and invitations are in the same suite.</b> Each person has a browser device
/// that their computer has granted a key to, an invitation that device made and a second device that
/// answered it, and an invitation their computer made, answered by the second device too. Every call names the
/// device it is made from in the <c>X-Enactive-Device</c> header, so Bob is also asked with a header naming one of
/// Alice's devices (which has to be refused like a device nobody has - as removed) and with none (which is refused
/// for the header, before anything is looked up). The one call that is not refused is making an invitation
/// under an id Alice has: invitation ids are per person, so he makes his own, and what is asserted is that
/// hers is untouched.</para>
/// </summary>
public sealed class IsolationTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string WorkspaceId = "workspace-1";

    private WebApplicationFactory<Program> _gateway = null!;
    private World _alice = null!;
    private World _bob = null!;

    private Database Db => new(database.ConnectionString);

    private HostService Host => new(Db);

    private DeviceService Devices => new(Db, Limits.Unlimited, TimeProvider.System);

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
    /// How a call names the device it is made from. Every call names one; the endpoints that do nothing with it
    /// beyond refusing a removed device are asked <see cref="NotTaken"/> and <see cref="Ignored"/> besides.
    /// </summary>
    public enum CallerHeader
    {
        /// <summary>The test names no device, so Bob's browser names its own, as the panel does.</summary>
        NotTaken,

        /// <summary>
        /// Bob names his own registered device. It must change nothing: an endpoint that began to read it - to
        /// find who is calling, say - would let a header stand in for the ids in the request, and nothing else
        /// here would notice.
        /// </summary>
        Ignored,

        /// <summary>Bob's own live device: the header is true, and the ids in the request are Alice's.</summary>
        Own,

        /// <summary>
        /// A device of Alice's, which a header can name as easily as any text: it must be refused like a
        /// device nobody has, or the header would be a way to act as another person's browser.
        /// </summary>
        Alices,

        /// <summary>No header at all.</summary>
        Missing
    }

    /// <summary>
    /// What each private endpoint is called with, given the ids it is about. The same builder makes
    /// the request for Alice's ids and for a made-up set, so the two differ in nothing but the ids.
    /// </summary>
    /// <param name="Request">
    /// The request, from the ids it is about and Bob's own, for the paths that name one of those as well.
    /// </param>
    /// <param name="OwnStatus">
    /// What Alice herself is answered, to show the request is well-formed and the 404 is about
    /// ownership. A start is a 409: her task already has its run. Naming Bob's computer with her own
    /// approval is a 404 for her too, as the approval is not on that computer; the case is about Bob.
    /// Likewise where the request names Bob's device or invitation beside her own ids.
    /// </param>
    /// <param name="Method">The request's verb; a POST unless it says otherwise.</param>
    /// <param name="TakesDevice">Whether the call says which device it is made from, in the header.</param>
    /// <param name="OwnHeaderIsRefused">
    /// Whether Bob, naming his own device, is refused. False where the request carries no id of Alice's
    /// at all, so his own header makes a request that is his own affair: listing his grants, or making an
    /// invitation. Those have their own tests below.
    /// </param>
    /// <param name="Code">
    /// The code of the refusal when the device is not what is being refused. An invitation that is not his
    /// is "unknown-invite", in the words used for one that does not exist; everything else is "not-found".
    /// </param>
    /// <param name="OwnChangesRows">
    /// Whether Alice's own call writes rows of hers. A read does not, and asserting that her checksum
    /// changed would be asserting something false.
    /// </param>
    /// <param name="OwnRequest">
    /// What Alice sends as her own call when the request Bob is refused cannot succeed for her: it
    /// names Bob's device because Bob has no other to name. Hers is the same call made with her own, so
    /// the control is a real success and not a second 404.
    /// </param>
    private sealed record PrivatePath(
        Func<TargetIds, TargetIds, (string Path, object? Body)> Request, HttpStatusCode OwnStatus,
        HttpMethod? Method = null, bool TakesDevice = false, bool OwnHeaderIsRefused = true,
        string Code = "not-found", bool OwnChangesRows = true,
        Func<TargetIds, (string Path, object? Body)>? OwnRequest = null)
    {
        public HttpMethod Verb => Method ?? HttpMethod.Post;
    }

    /// <summary>The one pair key every invitation of the suite is answered under: the gateway never checks it.</summary>
    private static readonly byte[] PairKey = RandomNumberGenerator.GetBytes(32);

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
            new((ids, own) => Decision(ids, own.HostId), HttpStatusCode.NotFound),

        ["send a device command: revoke"] = new((ids, _) => DeviceCommand(ids, CommandKind.RevokeDevice), HttpStatusCode.OK),

        ["send a device command: endorse"] = new((ids, _) => DeviceCommand(ids, CommandKind.EndorseDevice), HttpStatusCode.OK),

        // ── devices, grants and invitations ─────────────────────────────────

        ["remove a browser device"] = new(
            (ids, _) => ($"/api/devices/{ids.DeviceId}/revoke", new { }), HttpStatusCode.OK),

        // The request carries nothing of Alice's but the header, so only a header naming her device is a
        // refusal; his own is a list of his own grants, which Bob_listing_grants_is_told_nothing_of_alices
        // looks into.
        ["read the grants made to a device"] = new(
            (_, _) => ("/api/grants", null), HttpStatusCode.OK,
            HttpMethod.Get, TakesDevice: true, OwnHeaderIsRefused: false, OwnChangesRows: false),

        // The grant names Alice's computer, which is not Bob's, from a device that is.
        ["pass on the keys of a computer"] = new(
            (ids, _) => ("/api/grants", new[] { GrantTo(ids, ids.SecondDevice, ids.InviteId) }),
            HttpStatusCode.OK, TakesDevice: true),

        // The computer is Bob's own and the device it is granted to is Alice's. For Alice the computer is
        // Bob's, which is a 404 for her too.
        ["pass on the keys of one's own computer to another person's device"] = new(
            (ids, own) => ("/api/grants", new[] { GrantTo(own, ids.SecondDevice, own.InviteId) }),
            HttpStatusCode.NotFound, TakesDevice: true),

        // The id is the caller's own to make, and Alice's id makes Bob an invitation of his own: that is
        // not refused, and Bob_inviting_under_alices_id_makes_his_own_invitation looks at it. What is
        // asked here is the header alone. Alice, who has the id, is told so.
        ["make an invitation"] = new(
            (ids, _) => ("/api/invites", new { id = ids.InviteId }), HttpStatusCode.Conflict,
            TakesDevice: true, OwnHeaderIsRefused: false),

        // Alice's invitation is open: nobody has answered it, so it is still there to be taken. Bob
        // answers with his own device, which is live, so the only thing wrong with the call is whose
        // invitation it is - and her invitation must stay open. Alice answers it with her second device.
        ["answer an open invitation"] = new(
            (ids, own) => AnswerBy(ids.OpenInviteId, own.Device),
            HttpStatusCode.OK, Code: "unknown-invite",
            OwnRequest: ids => AnswerBy(ids.OpenInviteId, ids.SecondDevice)),

        // The same once it has been answered, which is a different refusal for its owner (it is used) and
        // must still be the one for a stranger (it is nobody's).
        ["answer an invitation that has been answered"] = new(
            (ids, own) => AnswerBy(ids.InviteId, own.Device),
            HttpStatusCode.NotFound, Code: "unknown-invite"),

        // The invitation is Bob's own, open and unanswered, and the device answering it is Alice's.
        ["answer one's own invitation with another person's device"] = new(
            (ids, own) => ("/api/enrollments", new
            {
                inviteId = own.OpenInviteId,
                deviceId = ids.SecondDevice.Id,
                mac = Enrollment.Mac(PairKey, own.OpenInviteId, ids.SecondDevice.Id, ids.SecondDevice.Key)
            }),
            HttpStatusCode.NotFound),

        ["read the answer to an invitation"] = new(
            (ids, _) => ($"/api/invites/{ids.InviteId}/enrollment", null), HttpStatusCode.OK,
            HttpMethod.Get, TakesDevice: true, Code: "unknown-invite", OwnChangesRows: false),

        // Nobody has answered it, so its owner is told "not yet" (204) and a stranger must be told what he
        // is told for an invitation nobody made: a 204 to him would say the invitation exists.
        ["read the answer to an open invitation"] = new(
            (ids, _) => ($"/api/invites/{ids.OpenInviteId}/enrollment", null), HttpStatusCode.NoContent,
            HttpMethod.Get, TakesDevice: true, Code: "unknown-invite", OwnChangesRows: false),
    };

    /// <summary>An invitation answered by <paramref name="device"/>, with the MAC the device would make.</summary>
    private static (string Path, object? Body) AnswerBy(string inviteId, TestDevice device)
        => ("/api/enrollments", new
        {
            inviteId,
            deviceId = device.Id,
            mac = Enrollment.Mac(PairKey, inviteId, device.Id, device.Key)
        });

    /// <summary>Every path, and for each of those made from a device every way of naming one that is refused.</summary>
    public static TheoryData<string, CallerHeader> PathCases
    {
        get
        {
            var cases = new TheoryData<string, CallerHeader>();

            foreach (var (name, path) in Paths)
            {
                if (!path.TakesDevice)
                {
                    cases.Add(name, CallerHeader.NotTaken);
                    cases.Add(name, CallerHeader.Ignored);
                }
                else if (path.OwnHeaderIsRefused)
                {
                    cases.Add(name, CallerHeader.Own);
                }

                cases.Add(name, CallerHeader.Alices);
                cases.Add(name, CallerHeader.Missing);
            }

            return cases;
        }
    }

    /// <summary>
    /// A grant of <paramref name="from"/>'s computer to <paramref name="device"/>, in the shape the
    /// gateway checks and made the way a browser answering an invitation makes it. Carries the computer's
    /// pinned signing key, so a refusal is not for carrying another one.
    /// </summary>
    private static KeyGrant GrantTo(TargetIds from, TestDevice device, string inviteId)
        => Grants.CreatePaired(
            from.HostId, device.Id, device.Key, HostKey.Create(1), inviteId, PairKey, from.SigningPublic);

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
    [MemberData(nameof(PathCases))]
    public async Task Bob_is_refused_every_path_to_alices_ids_as_if_they_did_not_exist(string name, CallerHeader header)
    {
        var path = Paths[name];
        var aliceBefore = await ChecksumAsync(_alice.UserId);

        var madeUpIds = TargetIds.MadeUp();
        var (madeUpPath, madeUpBody) = path.Request(madeUpIds, _bob.Ids);
        var (alicesPath, alicesBody) = path.Request(_alice.Ids, _bob.Ids);

        var withoutDevice = header == CallerHeader.Missing;
        var madeUp = await AnswerAsync(_bob, path.Verb, madeUpPath, madeUpBody, DeviceNamed(header, madeUpIds), withoutDevice);
        var alices = await AnswerAsync(_bob, path.Verb, alicesPath, alicesBody, DeviceNamed(header, _alice.Ids), withoutDevice);

        // The made-up id first, and its body named: two refusals can be equal because both are the
        // same wrong request - a typo in the route, a body the binder refused - and equal bodies
        // would then prove nothing about ownership.
        if (header == CallerHeader.Missing)
        {
            // A call that names no device is refused for that, before any id in it is looked at - so it is
            // the same refusal whoever's ids it carries, and still must not have touched Alice.
            Assert.Equal(HttpStatusCode.BadRequest, madeUp.Status);
            Assert.Contains("\"code\":\"device-header\"", madeUp.Body);

            Assert.Equal(HttpStatusCode.BadRequest, alices.Status);
        }
        else if (header == CallerHeader.Alices)
        {
            // A header naming a device of somebody else's is refused as the device, whatever the rest of the
            // request names: as a removed one, the answer a device nobody has gets too.
            Assert.Equal(HttpStatusCode.Forbidden, madeUp.Status);
            Assert.Contains("\"code\":\"device-revoked\"", madeUp.Body);

            Assert.Equal(HttpStatusCode.Forbidden, alices.Status);
        }
        else
        {
            // A true header leaves the refusal to the ids.
            Assert.Equal(HttpStatusCode.NotFound, madeUp.Status);
            Assert.Contains($"\"code\":\"{path.Code}\"", madeUp.Body);

            Assert.Equal(HttpStatusCode.NotFound, alices.Status);
        }

        Assert.Equal(madeUp.Body, alices.Body);

        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));

        // And Alice is not refused the same request, which is what makes the 404 above about whose
        // it is. Last, because it changes her.
        var (ownPath, ownBody) = path.OwnRequest?.Invoke(_alice.Ids) ?? (alicesPath, alicesBody);
        var own = await AnswerAsync(_alice, path.Verb, ownPath, ownBody, path.TakesDevice ? _alice.Ids.DeviceId : null);
        Assert.Equal(path.OwnStatus, own.Status);

        // What she did changed her rows, and the checksum shows it. Without this the unchanged
        // checksum above could mean only that the checksum cannot see change: every request here that
        // succeeds writes something of hers, except a read.
        if (own.Status == HttpStatusCode.OK && path.OwnChangesRows)
        {
            Assert.NotEqual(aliceBefore, await ChecksumAsync(_alice.UserId));
        }
    }

    /// <summary>
    /// The device a call names in its header, for a request about <paramref name="ids"/>. Bob's own is
    /// the same whichever ids the request carries; the other is a device of the owner of those ids.
    /// </summary>
    private string? DeviceNamed(CallerHeader header, TargetIds ids) => header switch
    {
        CallerHeader.Own or CallerHeader.Ignored => _bob.Ids.DeviceId,
        CallerHeader.Alices => ids.DeviceId,
        _ => null
    };

    // ── devices, grants and invitations: what the table above cannot say ────

    /// <summary>
    /// Bob, from his own device, asks for the answer to an invitation of Alice's - the one her browser
    /// made, and the one her computer made - and is told what he is told for an invitation nobody made.
    /// Without the owner filter on the invitation's lookup he is handed the key, label and MAC of her
    /// second device: the 404 is the only thing between a stranger and the public keys of her browsers.
    /// Only her browser's invitation proves that filter: the computer's is also refused by the query's
    /// "made by a device" condition, so its refusal below is that and the owner filter together, a defence
    /// in depth, and passes with either one removed.
    /// </summary>
    [Fact]
    public async Task Bob_reads_alices_enrollment()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);
        var bobsDevice = _bob.Ids.DeviceId;

        var madeUp = await AnswerAsync(_bob, HttpMethod.Get, EnrollmentPath(Ids.New()), null, bobsDevice);
        var alices = await AnswerAsync(_bob, HttpMethod.Get, EnrollmentPath(_alice.Ids.InviteId), null, bobsDevice);

        // The computer's own invitation is refused to a browser even for its owner (see
        // DeviceServiceTests), so for Bob this does not show the owner filter by itself.
        var alicesComputers = await AnswerAsync(_bob, HttpMethod.Get, EnrollmentPath(_alice.Ids.HostInviteId), null, bobsDevice);

        Assert.Equal(HttpStatusCode.NotFound, madeUp.Status);
        Assert.Contains("\"code\":\"unknown-invite\"", madeUp.Body);

        Assert.Equal(HttpStatusCode.NotFound, alices.Status);
        Assert.Equal(madeUp.Body, alices.Body);
        Assert.Equal(HttpStatusCode.NotFound, alicesComputers.Status);
        Assert.Equal(madeUp.Body, alicesComputers.Body);

        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));

        // What he is refused exists, and is hers to read: the answer is her second device, with its own
        // key and a MAC. Bob reading his own invitation is answered with his own second device, and
        // nothing of Alice's is in it.
        var hers = await AnswerAsync(_alice, HttpMethod.Get, EnrollmentPath(_alice.Ids.InviteId), null, _alice.Ids.DeviceId);
        var his = await AnswerAsync(_bob, HttpMethod.Get, EnrollmentPath(_bob.Ids.InviteId), null, bobsDevice);

        Assert.Equal(HttpStatusCode.OK, hers.Status);
        Assert.Contains(_alice.Ids.SecondDevice.Id, hers.Body);
        Assert.Contains(B64.Url(_alice.Ids.SecondDevice.Key), hers.Body);

        Assert.Equal(HttpStatusCode.OK, his.Status);
        Assert.Contains(_bob.Ids.SecondDevice.Id, his.Body);
        Assert.DoesNotContain(_alice.Ids.SecondDevice.Id, his.Body);
        Assert.DoesNotContain(B64.Url(_alice.Ids.SecondDevice.Key), his.Body);
    }

    private static string EnrollmentPath(string inviteId) => $"/api/invites/{inviteId}/enrollment";

    /// <summary>
    /// A device of Bob's, asked for the grants made to it, is handed Bob's and nothing of Alice's: the
    /// request names no id but the header, so there is nothing to refuse and what has to hold is whose
    /// rows answer it. Her device named in the header is refused in the table above.
    /// </summary>
    [Fact]
    public async Task Bob_listing_grants_is_told_nothing_of_alices()
    {
        var bobs = await AnswerAsync(_bob, HttpMethod.Get, "/api/grants", null, _bob.Ids.DeviceId);
        var alices = await AnswerAsync(_alice, HttpMethod.Get, "/api/grants", null, _alice.Ids.DeviceId);

        Assert.Equal(HttpStatusCode.OK, bobs.Status);
        Assert.Equal(HttpStatusCode.OK, alices.Status);

        var his = Assert.Single(RemoteJson.Deserialize<List<GrantsOfComputer>>(bobs.Body));
        Assert.Equal(_bob.HostId, his.HostId);
        Assert.Equal(_bob.Ids.DeviceId, Assert.Single(his.Grants).DeviceId);

        // She has a grant too, so the emptiness of Alice's in his list is not an empty fixture.
        var hers = Assert.Single(RemoteJson.Deserialize<List<GrantsOfComputer>>(alices.Body));
        Assert.Equal(_alice.HostId, hers.HostId);
        Assert.Equal(_alice.Ids.DeviceId, Assert.Single(hers.Grants).DeviceId);

        foreach (var ofAlice in new[]
        {
            _alice.UserId, _alice.HostId, _alice.Ids.DeviceId, _alice.Ids.SecondDevice.Id,
            B64.Url(_alice.Ids.SigningPublic)
        })
        {
            Assert.DoesNotContain(ofAlice, bobs.Body);
        }
    }

    /// <summary>
    /// Bob's list of devices holds his two and none of Alice's, and registering the public key of one of
    /// hers makes him a device of his own: a key is no identity, and nothing of hers moves.
    /// </summary>
    [Fact]
    public async Task Bobs_device_list_holds_none_of_alices_and_registering_her_key_changes_nothing_of_hers()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);

        var listed = await _bob.Panel.Http.GetStringAsync("/api/devices");
        var ids = System.Text.Json.JsonDocument.Parse(listed).RootElement.EnumerateArray()
            .Select(device => device.GetProperty("id").GetString()).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            new[] { _bob.Ids.DeviceId, _bob.Ids.SecondDevice.Id }.Order(StringComparer.Ordinal), ids);

        foreach (var ofAlice in new[]
        {
            _alice.Ids.DeviceId, _alice.Ids.SecondDevice.Id, B64.Url(_alice.Ids.Device.Key), B64.Url(_alice.Ids.SecondDevice.Key)
        })
        {
            Assert.DoesNotContain(ofAlice, listed);
        }

        var registered = await _bob.Panel.PostAsync<NewDevice>(
            "/api/devices", new { publicKey = B64.Url(_alice.Ids.Device.Key), label = "Copy" });

        Assert.NotEqual(_alice.Ids.DeviceId, registered.Id);
        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));
    }

    /// <summary>
    /// An invitation id is the maker's own, and an id Alice has is not taken for Bob: he makes his own
    /// under it, from his browser and from his computer, and hers - answered, with its enrollment - is
    /// exactly as she left it. The answer is not a 404 because there is nothing foreign to refuse: if it
    /// were a conflict, the id would tell him an invitation by it exists.
    /// </summary>
    [Fact]
    public async Task Bob_inviting_under_alices_id_makes_his_own_invitation()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);

        var made = await AnswerAsync(
            _bob, HttpMethod.Post, "/api/invites", new { id = _alice.Ids.InviteId }, _bob.Ids.DeviceId);

        Assert.Equal(HttpStatusCode.OK, made.Status);
        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));

        // His: made from his device, open, with nobody's answer.
        Assert.Equal(_bob.Ids.DeviceId, await InviteMakerAsync(_bob.UserId, _alice.Ids.InviteId));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM enrollments WHERE owner_id = '{_bob.UserId}' AND invite_id = '{_alice.Ids.InviteId}'"));

        // Hers: still made by her device, and still answered by her second.
        Assert.Equal(_alice.Ids.DeviceId, await InviteMakerAsync(_alice.UserId, _alice.Ids.InviteId));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM enrollments WHERE owner_id = '{_alice.UserId}' AND invite_id = '{_alice.Ids.InviteId}' "
            + $"AND device_id = '{_alice.Ids.SecondDevice.Id}'"));

        // Now it is taken, for him, as any id of his own would be; and for her, who had it first.
        var again = await AnswerAsync(
            _bob, HttpMethod.Post, "/api/invites", new { id = _alice.Ids.InviteId }, _bob.Ids.DeviceId);
        var hers = await AnswerAsync(
            _alice, HttpMethod.Post, "/api/invites", new { id = _alice.Ids.InviteId }, _alice.Ids.DeviceId);

        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal(HttpStatusCode.Conflict, hers.Status);
        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));

        // The same from his computer, under the id Alice's computer made one with.
        await Devices.CreateInviteAsync(_bob.Computer, _alice.Ids.HostInviteId, default);

        Assert.Equal(_bob.HostId, await database.ScalarAsync(
            $"SELECT created_by_host FROM invites WHERE owner_id = '{_bob.UserId}' AND id = '{_alice.Ids.HostInviteId}'"));
        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));
    }

    private async Task<object?> InviteMakerAsync(string ownerId, string inviteId)
        => await database.ScalarAsync(
            $"SELECT created_by_device FROM invites WHERE owner_id = '{ownerId}' AND id = '{inviteId}'");

    /// <summary>
    /// Bob, with a live device of his own, answers an invitation of Alice's that nobody has answered, and
    /// is told it does not exist: it is still open, so there is nothing for the lookup to refuse but whose
    /// it is. It stays unconsumed, and she can answer it herself afterwards - the refusal took nothing
    /// from her. The other refusals of this kind are of invitations already answered, where "used" would
    /// be the answer for a wrong reason.
    /// </summary>
    [Fact]
    public async Task Bob_cannot_answer_alices_open_invitation()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);
        var open = _alice.Ids.OpenInviteId;

        var (madeUpPath, madeUpBody) = AnswerBy(Ids.New(), _bob.Ids.Device);
        var (path, body) = AnswerBy(open, _bob.Ids.Device);

        var madeUp = await AnswerAsync(_bob, HttpMethod.Post, madeUpPath, madeUpBody);
        var alices = await AnswerAsync(_bob, HttpMethod.Post, path, body);

        Assert.Equal(HttpStatusCode.NotFound, madeUp.Status);
        Assert.Contains("\"code\":\"unknown-invite\"", madeUp.Body);
        Assert.Equal(HttpStatusCode.NotFound, alices.Status);
        Assert.Equal(madeUp.Body, alices.Body);

        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM enrollments WHERE invite_id = '{open}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM invites WHERE owner_id = '{_alice.UserId}' AND id = '{open}' AND consumed_at IS NULL"));

        // Hers to answer, and she is not refused: what makes the refusal above about whose it is.
        var (hersPath, hersBody) = AnswerBy(open, _alice.Ids.SecondDevice);
        var hers = await AnswerAsync(_alice, HttpMethod.Post, hersPath, hersBody);

        Assert.Equal(HttpStatusCode.OK, hers.Status);
    }

    /// <summary>
    /// The checksum can see the <c>invites</c> table change. No refused call changes it, and no control in
    /// the table above writes to it (Alice making an invitation she has is a conflict), so without this the
    /// unchanged <c>invites</c> hash after Bob's calls could mean only that it cannot change. Alice makes
    /// a fresh invitation: her hash for the table changes, and Bob's rows do not.
    /// </summary>
    [Fact]
    public async Task The_checksum_sees_an_invitation_alice_makes()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);
        var bobBefore = await ChecksumAsync(_bob.UserId);

        await PostFromAsync(_alice.Panel, "/api/invites", new { id = Ids.New() }, _alice.Ids.DeviceId);

        var aliceAfter = await ChecksumAsync(_alice.UserId);

        Assert.NotEqual(aliceBefore["invites"], aliceAfter["invites"]);

        // Her call touched her invitations and her audit trail, and no other table of hers.
        Assert.Equal(
            aliceBefore.Where(t => t.Key is not ("invites" or "audit")),
            aliceAfter.Where(t => t.Key is not ("invites" or "audit")));

        Assert.Equal(bobBefore, await ChecksumAsync(_bob.UserId));
    }

    /// <summary>
    /// Bob's computer grants its own key to a device. Alice's device is refused in the words used for a
    /// device nobody has, and a grant of Alice's computer is refused as a grant of another computer than
    /// the caller's - a 400 before any lookup, which is why that one says nothing about whose it is. No
    /// grant is stored for either, and her rows are as they were.
    /// </summary>
    [Fact]
    public async Task Bobs_computer_cannot_publish_a_grant_to_alices_device()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);
        var bobsGrants = await GrantCountAsync(_bob.UserId);

        var madeUp = await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(
            _bob.Computer, [GrantTo(_bob.Ids, TestDevice.Made(), _bob.Ids.InviteId)], default));
        var alices = await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(
            _bob.Computer, [GrantTo(_bob.Ids, _alice.Ids.SecondDevice, _bob.Ids.InviteId)], default));

        Assert.Equal(404, madeUp.Status);
        Assert.Equal("not-found", madeUp.Code);
        Assert.Equal(madeUp.Status, alices.Status);
        Assert.Equal(madeUp.Code, alices.Code);
        Assert.Equal(madeUp.Message, alices.Message);

        // Alice's computer, from Bob's: not a grant of its own keys. Whoever it belongs to.
        var ofHers = await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(
            _bob.Computer, [GrantTo(_alice.Ids, _bob.Ids.Device, _alice.Ids.InviteId)], default));
        var ofNobodys = await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(
            _bob.Computer, [GrantTo(TargetIds.MadeUp(), _bob.Ids.Device, _bob.Ids.InviteId)], default));

        Assert.Equal("bad-grant", ofHers.Code);
        Assert.Equal(ofNobodys.Code, ofHers.Code);

        Assert.Equal(bobsGrants, await GrantCountAsync(_bob.UserId));
        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));

        // Alice's computer granting her own second device is not refused, which is what makes the
        // refusals above about whose the device is.
        await Devices.PublishGrantsAsync(
            _alice.Computer, [GrantTo(_alice.Ids, _alice.Ids.SecondDevice, _alice.Ids.InviteId)], default);

        Assert.NotEqual(aliceBefore, await ChecksumAsync(_alice.UserId));
    }

    private Task<long> GrantCountAsync(string ownerId)
        => database.ScalarLongAsync($"SELECT COUNT(*) FROM grants WHERE owner_id = '{ownerId}'");

    /// <summary>
    /// Bob's computer says it has handled the answer to Alice's computer's invitation, and is told no such
    /// invitation exists - as it is for one nobody made. Her enrollment is still unanswered, so her
    /// computer is handed it again, and saying so herself is what answers it. The query also asks for the
    /// computer that made the invitation, and Bob's is not Alice's: the refusal is that condition and the
    /// owner filter together, a defence in depth, so it does not show either one by itself.
    /// </summary>
    [Fact]
    public async Task Bobs_computer_cannot_mark_alices_invitation_answered()
    {
        var aliceBefore = await ChecksumAsync(_alice.UserId);

        var madeUp = await Assert.ThrowsAsync<GatewayFault>(
            () => Devices.AnsweredInviteAsync(_bob.Computer, Ids.New(), default));
        var alices = await Assert.ThrowsAsync<GatewayFault>(
            () => Devices.AnsweredInviteAsync(_bob.Computer, _alice.Ids.HostInviteId, default));

        Assert.Equal(404, madeUp.Status);
        Assert.Equal("unknown-invite", madeUp.Code);
        Assert.Equal(madeUp.Status, alices.Status);
        Assert.Equal(madeUp.Code, alices.Code);
        Assert.Equal(madeUp.Message, alices.Message);

        Assert.Equal(aliceBefore, await ChecksumAsync(_alice.UserId));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM enrollments WHERE owner_id = '{_alice.UserId}' AND invite_id = '{_alice.Ids.HostInviteId}' "
            + "AND answered_at IS NULL"));

        await Devices.AnsweredInviteAsync(_alice.Computer, _alice.Ids.HostInviteId, default);

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM enrollments WHERE owner_id = '{_alice.UserId}' AND invite_id = '{_alice.Ids.HostInviteId}' "
            + "AND answered_at IS NOT NULL"));
        Assert.NotEqual(aliceBefore, await ChecksumAsync(_alice.UserId));
    }

    /// <summary>
    /// What a computer is handed to answer is its own invitations': Bob's computer is given the one his own
    /// second device answered and not the one Alice's did, and the reverse. The list is asked for by owner
    /// and by the computer that made each invitation, so a leak would need both conditions gone: this is
    /// the defence in depth, and the owner filter alone is not shown by it.
    /// </summary>
    [Fact]
    public async Task Bobs_computer_is_handed_only_its_own_enrollments()
    {
        var bobs = await Devices.EnrollmentsAsync(_bob.Computer, default);
        var alices = await Devices.EnrollmentsAsync(_alice.Computer, default);

        var his = Assert.Single(bobs);
        Assert.Equal(_bob.Ids.HostInviteId, his.InviteId);
        Assert.Equal(_bob.Ids.SecondDevice.Id, his.DeviceId);

        var hers = Assert.Single(alices);
        Assert.Equal(_alice.Ids.HostInviteId, hers.InviteId);
        Assert.Equal(_alice.Ids.SecondDevice.Id, hers.DeviceId);

        // Not by id and not by key: her browsers' keys are not his to be handed either.
        var text = RemoteJson.Serialize(bobs);
        Assert.DoesNotContain(_alice.Ids.HostInviteId, text);
        Assert.DoesNotContain(_alice.Ids.SecondDevice.Id, text);
        Assert.DoesNotContain(B64.Url(_alice.Ids.SecondDevice.Key), text);
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

        // Every collection is Alice's own and only hers. Asserted first: a leak shows here as hers
        // holding his, before anything is asked about his own panel.
        Assert.Equal([_alice.Ids.HostId], alices.Hosts.Select(h => h.Id));
        Assert.Equal([WorkspaceId], Assert.Single(alices.Hosts).Workspaces.Select(w => w.Id));
        Assert.Equal([_alice.Ids.TaskId], alices.Tasks.Select(t => t.Id));
        Assert.Equal([_alice.Ids.RunId], alices.Runs.Select(r => r.Id));
        Assert.Equal([_alice.Ids.ApprovalId], alices.Approvals.Select(a => a.Id));
        Assert.Equal(3, alices.Events.Count);
        Assert.All(alices.Events, e => Assert.Equal(_alice.Ids.RunId, e.RunId));
        Assert.All(alices.Events, e => Assert.Equal(_alice.Ids.HostId, e.HostId));
        Assert.All(alices.Notices, n => Assert.Equal(_alice.Ids.RunId, n.RunId));

        // The count is hers: one notice, unread.
        Assert.False(Assert.Single(alices.Notices).Read);
        Assert.Equal(1, alices.UnreadNotices);

        // The cursor is where Alice's own line stands - the line of a person with three events and a
        // notice.
        Assert.Equal(await LineAsync(_alice.UserId), alices.Cursor);

        // Alice has lost nothing, so she is told nothing was trimmed.
        Assert.Null(alices.Retention.TrimmedBefore);

        // Bob really has all of it, and more, so the emptiness above is not an empty fixture and
        // each of her values differs from his.
        Assert.Equal([_bob.Ids.HostId], bobs.Hosts.Select(h => h.Id));
        Assert.Equal([_bob.Ids.TaskId], bobs.Tasks.Select(t => t.Id));
        Assert.Equal([_bob.Ids.RunId], bobs.Runs.Select(r => r.Id));
        Assert.Equal(2, bobs.Approvals.Count);
        Assert.NotEmpty(bobs.Events);
        Assert.Equal(2, bobs.Notices.Count);
        Assert.Equal(2, bobs.UnreadNotices);
        Assert.NotEqual(bobs.Cursor, alices.Cursor);
        Assert.NotNull(bobs.Retention.TrimmedBefore);

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
    /// The checksum can fail: one column of one row of Alice's, changed through SQL, and the checksum
    /// of the table it is in differs - and only that table's. A checksum that could not tell would
    /// make "unchanged" in every other test mean nothing. The time column is the smallest change:
    /// one millisecond.
    /// </summary>
    [Fact]
    public async Task The_checksum_sees_a_single_column_of_alices_change()
    {
        var before = await ChecksumAsync(_alice.UserId);

        await database.ExecuteAsync(
            "UPDATE notices SET is_read = 1 WHERE owner_id = @owner", ("@owner", _alice.UserId));
        var read = await ChecksumAsync(_alice.UserId);

        Assert.NotEqual(before["notices"], read["notices"]);
        Assert.Equal(
            before.Where(t => t.Key != "notices"), read.Where(t => t.Key != "notices"));

        await database.ExecuteAsync(
            "UPDATE hosts SET created_at = created_at + INTERVAL 1000 MICROSECOND WHERE owner_id = @owner",
            ("@owner", _alice.UserId));

        Assert.NotEqual(read["hosts"], (await ChecksumAsync(_alice.UserId))["hosts"]);
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

        // Nobody's: the allow-list of who may sign up, the migrator's own bookkeeping, and the provider answers
        // already redeemed - a random id and a time each, which name no person and are gone in a day.
        var ownedByNobody = new HashSet<string> { "admissions", "schema_version", "signin_redemptions" };

        Assert.DoesNotContain(all, t => !owned.Contains(t) && !ownedByNobody.Contains(t));
        Assert.Contains("tasks", owned);
        Assert.Contains("notices", owned);

        // The ones the suite above reads for devices, grants and invitations: dropping the owner column
        // from one of these would make a refused call's effect on it invisible to every test in it.
        Assert.Contains("devices", owned);
        Assert.Contains("grants", owned);
        Assert.Contains("invites", owned);
        Assert.Contains("enrollments", owned);
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

        // First, so it is the device this browser names on every call, as a panel's first registration is.
        var device = await RegisterDeviceAsync(panel, "Laptop");
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
        var (secondDevice, inviteId, hostInviteId, openInviteId, signingPublic) =
            await SeedDevicesAsync(panel, host, browser, device);

        var ids = new TargetIds(
            computer.Id, WorkspaceId, taskId, runId, approvalId, Ids.Hash(approvalId),
            device, secondDevice, inviteId, hostInviteId, openInviteId, signingPublic);
        var world = new World(panel, ids, host, sealer, sealedTask, sealedWorkspaceName);

        await PublishAsync(world, RemoteEventKind.Running, "Started");
        await PublishAsync(world, RemoteEventKind.Progress, "Working");
        await PublishAsync(world, RemoteEventKind.ApprovalRequested, "Permission needed");

        return world;
    }

    /// <summary>
    /// A person's browsers, grants and invitations, made through the paths real ones are made through: the
    /// browser has registered its key (<paramref name="device"/>), the computer grants it the first key (which pins the computer's signing
    /// key), the browser invites another, the other registers and answers, and the computer makes an
    /// invitation of its own, which the same second browser answers. One more invitation is left open.
    /// </summary>
    private async Task<(TestDevice SecondDevice, string InviteId, string HostInviteId,
        string OpenInviteId, byte[] SigningPublic)> SeedDevicesAsync(
        PanelClient panel, HostAccess host, TestBrowser browser, TestDevice device)
    {
        // The computer's own call, as its hub makes it: the only path that may pin a signing key.
        using var signer = P256.GenerateSigning();
        var signingPublic = P256.SigningPublicRaw(signer);

        await Devices.PublishGrantsAsync(host, [
            Grants.CreatePaired(host.HostId, device.Id, device.Key, browser.Computer.Current, "connect", PairKey, signingPublic)
        ], default);

        var inviteId = Ids.New();
        await PostFromAsync(panel, "/api/invites", new { id = inviteId }, device.Id);

        var secondDevice = await RegisterDeviceAsync(panel, "Phone");
        await panel.PostAsync("/api/enrollments", EnrollmentBody(inviteId, secondDevice));

        // Made by the computer, as "Add a device" on the desktop does, and answered by the same phone.
        var hostInviteId = Ids.New();
        await Devices.CreateInviteAsync(host, hostInviteId, default);
        await panel.PostAsync("/api/enrollments", EnrollmentBody(hostInviteId, secondDevice));

        var openInviteId = Ids.New();
        await PostFromAsync(panel, "/api/invites", new { id = openInviteId }, device.Id);

        return (secondDevice, inviteId, hostInviteId, openInviteId, signingPublic);
    }

    private static object EnrollmentBody(string inviteId, TestDevice device)
        => AnswerBy(inviteId, device).Body!;

    private static async Task<TestDevice> RegisterDeviceAsync(PanelClient panel, string label)
    {
        var key = NewPublicKey();
        var registered = await panel.PostAsync<NewDevice>("/api/devices", new { publicKey = B64.Url(key), label });
        return new TestDevice(registered.Id, key);
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

    private static Task<Answer> AnswerAsync(
        World caller, HttpMethod method, string path, object? body, string? deviceId = null, bool withoutDevice = false)
        => AnswerAsync(caller.Panel, method, path, body, deviceId, withoutDevice);

    /// <param name="deviceId">The device named; null for the browser's own, as the panel names it.</param>
    /// <param name="withoutDevice">Name none at all.</param>
    private static async Task<Answer> AnswerAsync(
        PanelClient panel, HttpMethod method, string path, object? body, string? deviceId = null,
        bool withoutDevice = false)
    {
        using var response = await panel.SendAsync(method, path, body, configure: request =>
        {
            if (withoutDevice)
            {
                PanelClient.WithoutDevice(request);
            }
            else if (deviceId is not null)
            {
                request.Headers.Add(DeviceHeader.Name, deviceId);
            }
        });

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

    // "O" for a time: the default text drops the milliseconds, and the DATETIME(3) columns change
    // within one second - a checksum blind to that would miss a refused request that still touched a row.
    private static string Text(object value) => value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        DateTime time => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset time => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!
    };

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

    /// <summary>A call made from a device, which has to succeed: the way a world's own are made.</summary>
    private static async Task PostFromAsync(PanelClient panel, string path, object body, string deviceId)
    {
        var answer = await AnswerAsync(panel, HttpMethod.Post, path, body, deviceId);
        Assert.True(answer.Status == HttpStatusCode.OK, $"{(int)answer.Status} {path}: {answer.Body}");
    }

    private sealed record NewComputer(string Id, string Name, string Token);

    private sealed record NewDevice(string Id);

    /// <summary>What <c>GET /api/grants</c> answers for one computer. Every field, because the wire refuses unknown ones.</summary>
    private sealed record GrantsOfComputer(string HostId, uint KeyEpoch, List<KeyGrant> Grants);

    /// <summary>A browser device: the id the gateway gave it and the public key it was registered with.</summary>
    internal sealed record TestDevice(string Id, byte[] Key)
    {
        public static TestDevice Made() => new(Ids.New(), NewPublicKey());
    }

    private static byte[] NewPublicKey()
    {
        using var key = P256.Generate();
        return P256.PublicRaw(key);
    }

    /// <summary>The ids of one person's world that another person's requests can name.</summary>
    /// <param name="Device">The browser the person made their invitation from.</param>
    /// <param name="SecondDevice">The browser that answered their invitations.</param>
    /// <param name="InviteId">The invitation <paramref name="Device"/> made, answered by <paramref name="SecondDevice"/>.</param>
    /// <param name="HostInviteId">The invitation the person's computer made, answered by <paramref name="SecondDevice"/>.</param>
    /// <param name="OpenInviteId">An invitation of <paramref name="Device"/>'s that nobody has answered.</param>
    /// <param name="SigningPublic">The signing key the person's computer pinned with its first grant.</param>
    internal sealed record TargetIds(
        string HostId, string WorkspaceId, string TaskId, string RunId, string ApprovalId, string ActionHash,
        TestDevice Device, TestDevice SecondDevice, string InviteId, string HostInviteId, string OpenInviteId,
        byte[] SigningPublic)
    {
        public string DeviceId => Device.Id;

        /// <summary>Ids of the same kinds that belong to nobody, in the forms the gateway itself makes.</summary>
        public static TargetIds MadeUp()
        {
            using var signer = P256.GenerateSigning();

            return new(
                Ids.New(), "workspace-" + Guid.NewGuid().ToString("N")[..8],
                Guid.NewGuid().ToString(), Ids.New(), Guid.NewGuid().ToString(),
                Ids.Hash(Guid.NewGuid().ToString()),
                TestDevice.Made(), TestDevice.Made(), Ids.New(), Ids.New(), Ids.New(),
                P256.SigningPublicRaw(signer));
        }
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

namespace Enactive.Remote.Gateway.Services;

using System.Globalization;
using System.Text.Json;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Administration;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

internal sealed record QuotaChange(int? ExpectedVersion, Dictionary<string, string?>? Values, string? Reason, int? ExpectedDefaultsVersion = null);
internal sealed record QuotaItem(string Key, string Effective, string? Override, string Default, string Startup,
    string Source, string? Usage, bool OverLimit);
internal sealed record QuotaView(int Version, int DefaultsVersion, IReadOnlyList<QuotaItem> Items);
internal sealed record QueueUsage(string HostId, string Name, long Pending, long DeviceChanges, long Cancellations);

/// <summary>Persisted decisions, read inside the transaction that consumes them, never cached per process.</summary>
internal sealed class QuotaSettings(Database db, Limits fallback, TimeProvider clock)
{
    internal static readonly string[] Keys = ["HostsPerUser", "DevicesPerUser", "ActiveRunsPerUser",
        "QueuedCommandsPerHost", "TasksPerDay", "OpenInvitesPerUser", "SealedBytesPerUser"];
    private sealed record Stored(int Version, Dictionary<string, long> Values);

    private static async Task<Stored> StoredAsync(MySqlConnection c, MySqlTransaction tx, string? owner)
    {
        var stored = await c.ReadOneAsync(tx, owner is null
            ? "SELECT revision, settings FROM quota_defaults WHERE id = 1 FOR SHARE"
            : "SELECT revision, settings FROM user_quotas WHERE owner_id = @owner FOR SHARE",
            r => new Stored(r.GetInt32(0), JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetString(1))!),
            owner is null ? [] : [("@owner", owner)]);
        // Missing user settings mean inheritance; missing singleton means a broken restore.
        // Treating both as inheritance would report a successful defaults update of zero rows.
        return stored ?? (owner is null
            ? throw new InvalidOperationException("Quota defaults row is missing. Verify the database restore.")
            : new Stored(0, new()));
    }

    internal static Dictionary<string, long> Values(Limits limits) => new()
    {
        [Keys[0]] = limits.HostsPerUser, [Keys[1]] = limits.DevicesPerUser,
        [Keys[2]] = limits.ActiveRunsPerUser, [Keys[3]] = limits.QueuedCommandsPerHost,
        [Keys[4]] = limits.TasksPerDay, [Keys[5]] = limits.OpenInvitesPerUser, [Keys[6]] = limits.SealedBytesPerUser
    };
    private static Limits Combine(Limits fallback, Stored defaults, Stored user)
    {
        var v = Values(fallback);
        foreach (var pair in defaults.Values.Concat(user.Values)) v[pair.Key] = pair.Value;
        return new((int)v[Keys[0]], (int)v[Keys[1]], (int)v[Keys[2]], (int)v[Keys[3]],
            (int)v[Keys[4]], (int)v[Keys[5]], v[Keys[6]]);
    }

    internal static async Task<Limits> ResolveAsync(MySqlConnection c, MySqlTransaction tx, string owner, Limits fallback)
    {
        // Callers hold the account first. Current locking reads avoid a stale REPEATABLE READ
        // snapshot and share the defaults lock until commit: an edit takes effect atomically.
        var defaults = await StoredAsync(c, tx, null);
        return Combine(fallback, defaults, await StoredAsync(c, tx, owner));
    }

    public Task<QuotaView> ReadAsync(string? owner, CancellationToken ct)
        => db.InTransactionAsync(async (c, tx) =>
        {
            if (owner is not null) { AdminDirectory.UserId(owner); await LockOwnerAsync(c, tx, owner); }
            var defaults = await StoredAsync(c, tx, null);
            var user = owner is null ? new Stored(0, new()) : await StoredAsync(c, tx, owner);
            var effective = Values(Combine(fallback, defaults, user));
            var start = Values(fallback);
            var usage = new Dictionary<string, long>();
            if (owner is not null)
            {
                var now = clock.GetUtcNow();
                usage = (await c.ReadOneAsync(tx, """
                    SELECT sealed_bytes,
                      (SELECT COUNT(*) FROM hosts WHERE owner_id = u.id AND revoked = 0) hosts,
                      (SELECT COUNT(*) FROM devices WHERE owner_id = u.id AND revoked_at IS NULL) devices,
                      (SELECT COUNT(*) FROM runs WHERE owner_id = u.id AND status NOT IN ('Completed','Failed','Incomplete','Cancelled','Interrupted') AND host_id IN (SELECT id FROM hosts WHERE owner_id = u.id AND revoked = 0)) runs,
                      (SELECT COUNT(*) FROM tasks WHERE owner_id = u.id AND created_at > @day) tasks,
                      (SELECT COUNT(*) FROM invites WHERE owner_id = u.id AND consumed_at IS NULL AND expires_at > @now) invites
                    FROM users u WHERE id = @owner
                    """, r => new Dictionary<string, long> { [Keys[0]] = r.GetInt64("hosts"), [Keys[1]] = r.GetInt64("devices"),
                        [Keys[2]] = r.GetInt64("runs"), [Keys[4]] = r.GetInt64("tasks"), [Keys[5]] = r.GetInt64("invites"),
                        [Keys[6]] = r.GetInt64("sealed_bytes") }, ("@owner", owner), ("@now", now), ("@day", now.AddDays(-1))))!;
            }
            return new QuotaView(owner is null ? defaults.Version : user.Version, defaults.Version,
                Keys.Select(key => new QuotaItem(key, Number(effective[key]),
                    (owner is null ? defaults.Values : user.Values).TryGetValue(key, out var value) ? Number(value) : null,
                    Number(defaults.Values.GetValueOrDefault(key, start[key])), Number(start[key]),
                    user.Values.ContainsKey(key) ? "user" : defaults.Values.ContainsKey(key) ? "default" : "startup",
                    usage.TryGetValue(key, out var held) ? Number(held) : null,
                    usage.TryGetValue(key, out held) && held > effective[key])).ToArray());
        }, ct);

    private static async Task LockOwnerAsync(MySqlConnection c, MySqlTransaction tx, string owner)
    {
        if (!await c.ExistsAsync(tx, "SELECT id FROM users WHERE id = @owner FOR UPDATE", ("@owner", owner)))
            throw GatewayFault.NotFound("Account not found.");
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public async Task ChangeAsync(string? owner, QuotaChange request, AdminMutation mutation, CancellationToken ct)
    {
        if (owner is not null) AdminDirectory.UserId(owner);
        if (request.ExpectedVersion is null or < 0 || (owner is not null && request.ExpectedDefaultsVersion is null or < 0) || request.Values is null || request.Values.Count is < 1 or > 7
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
            throw GatewayFault.BadRequest("Supply a revision, 1–7 quota values and a reason of at most 500 characters.");
        var patch = new Dictionary<string, long?>();
        foreach (var (key, text) in request.Values)
        {
            if (!Keys.Contains(key)) throw GatewayFault.BadRequest("Unknown quota.");
            long? value = null;
            if (text is not null)
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 1 || (key != Keys[6] && parsed > int.MaxValue))
                    throw GatewayFault.BadRequest("Quota values must be positive whole numbers within the supported range. Clear a value to inherit.");
                value = parsed;
            }
            patch[key] = value;
        }
        var administration = new AdministrationService(db, clock);
        await db.InTransactionAsync(async (c, tx) =>
        {
            await administration.AuthorizeAsync(c, tx, mutation);
            if (owner is not null) await LockOwnerAsync(c, tx, owner);
            // Same account/defaults/override order as creation. Default edits never lock accounts.
            await c.ExecuteAsync(tx, "SELECT id FROM quota_defaults WHERE id = 1 " + (owner is null ? "FOR UPDATE" : "FOR SHARE"));
            var defaults = await StoredAsync(c, tx, null);
            if (owner is not null && request.ExpectedDefaultsVersion != defaults.Version)
                throw new GatewayFault("admin-conflict", 409, "Shared defaults changed. Refresh and review before saving.");
            var before = owner is null ? defaults : await StoredAsync(c, tx, owner);
            if (request.ExpectedVersion != before.Version)
                throw new GatewayFault("admin-conflict", 409, "Quotas changed. Refresh and review before saving.");
            var after = new Dictionary<string, long>(before.Values);
            foreach (var (key, value) in patch) { if (value is null) after.Remove(key); else after[key] = value.Value; }
            if (owner is null)
                await c.ExecuteAsync(tx, "UPDATE quota_defaults SET revision = revision + 1, settings = @values WHERE id = 1",
                    ("@values", JsonSerializer.Serialize(after)));
            else
                await c.ExecuteAsync(tx, """
                    INSERT INTO user_quotas (owner_id, revision, settings) VALUES (@owner, 1, @values)
                    ON DUPLICATE KEY UPDATE revision = revision + 1, settings = @values
                    """, ("@owner", owner), ("@values", JsonSerializer.Serialize(after)));
            await administration.AuditAsync(c, tx, owner, "quota.changed", owner ?? "quota-defaults", mutation,
                new { before = before.Values, after, reason = request.Reason.Trim() });
        }, ct);
    }

    public async Task<AdminPage<QueueUsage>> QueuesAsync(string owner, string? after, CancellationToken ct)
    {
        AdminDirectory.UserId(owner);
        if (!string.IsNullOrEmpty(after)) AdminDirectory.UserId(after);
        await using var c = await db.OpenAsync(ct);
        var rows = await c.ReadAllAsync(null, """
            SELECT h.id, h.label,
              (SELECT COUNT(*) FROM commands WHERE owner_id = @owner AND host_id = h.id AND status = 'PendingDelivery'
                AND expires_at > @now AND kind NOT IN ('RevokeDevice','EndorseDevice','CancelRun')) pending,
              (SELECT COUNT(*) FROM commands WHERE owner_id = @owner AND host_id = h.id AND status = 'PendingDelivery'
                AND expires_at > @now AND kind IN ('RevokeDevice','EndorseDevice')) changes,
              (SELECT COUNT(*) FROM commands WHERE owner_id = @owner AND host_id = h.id AND status = 'PendingDelivery'
                AND expires_at > @now AND kind = 'CancelRun') cancellations
            FROM hosts h WHERE owner_id = @owner AND revoked = 0 AND id > @after ORDER BY id LIMIT 101
            """, r => new QueueUsage(r.GetString("id"), r.GetString("label"), r.GetInt64("pending"), r.GetInt64("changes"), r.GetInt64("cancellations")),
            ("@owner", owner), ("@after", after ?? ""), ("@now", clock.GetUtcNow()));
        return new(rows.Take(100).ToArray(), rows.Count > 100 ? rows[99].HostId : null);
    }
}

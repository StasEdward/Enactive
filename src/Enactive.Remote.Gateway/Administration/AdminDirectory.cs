namespace Enactive.Remote.Gateway.Administration;

using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;

internal sealed record AdminPage<T>(IReadOnlyList<T> Items, string? Next);
internal sealed record AdminUser(string Id, string DisplayName, string Status, long SealedBytes, DateTimeOffset CreatedAt, int Version);
internal sealed record AdminRegistration(string Provider, string Subject, string Display, string State,
    DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt, int Version);
internal sealed record AdminOverview(long Users, long DisabledUsers, long WaitingRegistrations);
internal sealed record AdminUserDetails(AdminUser User, long Hosts, long Devices, long Tasks, long Runs, DateTimeOffset? LastHostSeenAt);

/// <summary>Metadata only. Explicit projections prevent future secret or encrypted columns leaking into the API.</summary>
internal sealed class AdminDirectory(Database db)
{
    private static void Validate(string? search, int size)
    {
        // One extra row detects another page without loading the directory or counting each search.
        if (size is < 1 or > 100 || search?.Length > 100)
            throw GatewayFault.BadRequest("Page size must be 1–100 and search at most 100 characters.");
    }

    internal static void UserId(string id)
    {
        if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c)))
            throw GatewayFault.BadRequest("Invalid user identifier.");
    }

    public async Task<AdminPage<AdminUser>> UsersAsync(string? search, string? state, string? after, int size, CancellationToken ct)
    {
        Validate(search, size);
        if (state is not (null or "" or "Active" or "Disabled")) throw GatewayFault.BadRequest("Invalid account status.");
        if (!string.IsNullOrEmpty(after)) UserId(after);
        await using var connection = await db.OpenAsync(ct);
        // The primary key is the stable pagination index. Never use offsets, which skip accounts
        // when a preceding account is deleted. Search is literal, including SQL wildcard characters.
        var rows = await connection.ReadAllAsync(null, """
            SELECT id, display_name, status, sealed_bytes, created_at, security_version FROM users
            WHERE id > @after AND (@state = '' OR status = @state)
              AND (@q = '' OR INSTR(display_name, @q) > 0 OR id = @q)
            ORDER BY id LIMIT @take
            """, r => new AdminUser(r.GetString("id"), r.GetString("display_name"), r.GetString("status"),
                r.GetInt64("sealed_bytes"), r.Utc("created_at"), r.GetInt32("security_version")),
            ("@after", after ?? ""), ("@state", state ?? ""), ("@q", search?.Trim() ?? ""), ("@take", size + 1));
        return new(rows.Take(size).ToArray(), rows.Count > size ? rows[size - 1].Id : null);
    }

    public async Task<AdminPage<AdminRegistration>> RegistrationsAsync(string? search, string? state, string? after, int size, CancellationToken ct)
    {
        Validate(search, size);
        if (state is not (null or "" or "Waiting" or "Approved" or "Refused")) throw GatewayFault.BadRequest("Invalid registration state.");
        AdmissionIdentity? cursor = null;
        if (!string.IsNullOrEmpty(after) && !AdmissionIdentity.TryParse(after, out cursor))
            throw GatewayFault.BadRequest("Invalid registration cursor.");
        await using var connection = await db.OpenAsync(ct);
        // Match the composite primary key, including its case-sensitive subject collation.
        var rows = await connection.ReadAllAsync(null, """
            SELECT provider, subject, display, state, requested_at, decided_at, revision FROM admissions
            WHERE (provider > @provider OR (provider = @provider AND subject > @subject))
              AND (@state = '' OR state = @state)
              AND (@q = '' OR INSTR(display, @q) > 0 OR subject = @q)
            ORDER BY provider, subject LIMIT @take
            """, r => new AdminRegistration(r.GetString("provider"), r.GetString("subject"), r.GetString("display"),
                r.GetString("state"), r.Utc("requested_at"), r.UtcOrNull("decided_at"), r.GetInt32("revision")),
            ("@provider", cursor?.Provider ?? ""), ("@subject", cursor?.Subject ?? ""),
            ("@state", state ?? ""), ("@q", search?.Trim() ?? ""), ("@take", size + 1));
        var last = rows.Take(size).LastOrDefault();
        return new(rows.Take(size).ToArray(), rows.Count > size ? $"{last!.Provider}:{last.Subject}" : null);
    }

    public async Task<AdminOverview> OverviewAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return (await connection.ReadOneAsync(null, """
            SELECT COUNT(*) AS users, COALESCE(SUM(status = 'Disabled'), 0) AS disabled,
              (SELECT COUNT(*) FROM admissions WHERE state = 'Waiting') AS waiting FROM users
            """, r => new AdminOverview(r.GetInt64("users"), r.GetInt64("disabled"), r.GetInt64("waiting"))))!;
    }

    public async Task<AdminUserDetails> UserAsync(string id, CancellationToken ct)
    {
        UserId(id);
        await using var connection = await db.OpenAsync(ct);
        // One statement gives mutually consistent counts; never read task bodies or host credentials.
        return await connection.ReadOneAsync(null, """
            SELECT id, display_name, status, sealed_bytes, created_at, security_version,
              (SELECT COUNT(*) FROM hosts WHERE owner_id = u.id) AS hosts,
              (SELECT COUNT(*) FROM devices WHERE owner_id = u.id) AS devices,
              (SELECT COUNT(*) FROM tasks WHERE owner_id = u.id) AS tasks,
              (SELECT COUNT(*) FROM runs WHERE owner_id = u.id) AS runs,
              (SELECT MAX(last_seen_at) FROM hosts WHERE owner_id = u.id) AS last_seen
            FROM users u WHERE id = @id
            """, r => new AdminUserDetails(new(r.GetString("id"), r.GetString("display_name"), r.GetString("status"),
                r.GetInt64("sealed_bytes"), r.Utc("created_at"), r.GetInt32("security_version")), r.GetInt64("hosts"), r.GetInt64("devices"),
                r.GetInt64("tasks"), r.GetInt64("runs"), r.UtcOrNull("last_seen")), ("@id", id))
            ?? throw GatewayFault.NotFound("Account not found.");
    }
}
